using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Modules.WorkItems.DTOs;
using BoardSync.Api.Modules.WorkItems.Events;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Modules.WorkItems.Repository;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;

namespace BoardSync.Api.Modules.WorkItems.Services;

/// <summary>
/// Business logic for the WorkItems module: validation, the state machine, the type
/// hierarchy, audit history and domain events. All persistence goes through
/// <see cref="IWorkItemRepository"/>; project references go through <see cref="IProjectService"/>.
/// </summary>
public class WorkItemService : IWorkItemService
{
    private readonly IWorkItemRepository _repository;
    private readonly IProjectService _projectService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<WorkItemService> _logger;

    public WorkItemService(
        IWorkItemRepository repository,
        IProjectService projectService,
        IEventBus eventBus,
        ILogger<WorkItemService> logger)
    {
        _repository = repository;
        _projectService = projectService;
        _eventBus = eventBus;
        _logger = logger;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<WorkItemResponse> CreateAsync(
        Guid projectId,
        CreateWorkItemRequest request,
        Guid createdBy,
        CancellationToken ct = default)
    {
        if (!await _projectService.ExistsAsync(projectId, ct))
            throw new NotFoundException("Project", projectId);

        if (request.ParentId.HasValue)
        {
            var parent = await _repository.GetActiveInProjectAsync(request.ParentId.Value, projectId, ct)
                ?? throw new NotFoundException("Parent work item", request.ParentId.Value);

            ValidateHierarchy(parent.Type, request.Type);
        }

        var item = new WorkItem
        {
            ProjectId = projectId,
            TeamId = request.TeamId,
            ParentId = request.ParentId,
            Type = request.Type,
            State = WorkItemState.New,
            Priority = request.Priority,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            AssigneeId = request.AssigneeId,
            StoryPoints = request.StoryPoints,
            CreatedBy = createdBy
        };

        _repository.Add(item);

        // Tags — normalize before de-duplicating, otherwise "Payments" and "payments"
        // survive as two rows and violate the unique (WorkItemId, Name) index.
        foreach (var tag in NormalizeTags(request.Tags))
        {
            _repository.AddTag(new WorkItemTag
            {
                WorkItemId = item.Id,
                Name = tag,
                CreatedBy = createdBy
            });
        }

        // Initial history entry
        AddHistory(item.Id, createdBy, "State", null, WorkItemState.New.ToString());

        await _repository.SaveChangesAsync(ct);

        await _eventBus.PublishAsync(
            new WorkItemCreated(item.Id, projectId, item.Type, item.Title, createdBy), ct);

        _logger.LogInformation("WorkItem '{Title}' ({Id}) created in project {ProjectId} by {UserId}",
            item.Title, item.Id, projectId, createdBy);

        return await MapToResponseAsync(item.Id, ct);
    }

    public async Task<WorkItemResponse> GetByIdAsync(Guid workItemId, CancellationToken ct = default)
    {
        _ = await GetWorkItemOrThrowAsync(workItemId, ct);
        return await MapToResponseAsync(workItemId, ct);
    }

    public async Task<PagedResult<WorkItemSummaryResponse>> GetForProjectAsync(
        Guid projectId,
        WorkItemFilterQuery filter,
        CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var page = Math.Max(filter.Page, 1);
        var skip = (page - 1) * pageSize;

        var (items, total) = await _repository.GetForProjectAsync(projectId, filter, skip, pageSize, ct);

        var childCounts = await _repository.GetChildCountsAsync(items.Select(i => i.Id), ct);

        var summaries = items.Select(w => new WorkItemSummaryResponse(
            w.Id,
            w.Type,
            w.State,
            w.Priority,
            w.Title,
            w.AssigneeId,
            w.StoryPoints,
            w.Tags.Select(t => t.Name).ToList(),
            childCounts.GetValueOrDefault(w.Id, 0),
            w.CreatedAt
        )).ToList();

        return new PagedResult<WorkItemSummaryResponse>(summaries, total, page, pageSize);
    }

    public async Task<WorkItemResponse> UpdateAsync(
        Guid workItemId,
        UpdateWorkItemRequest request,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var item = await _repository.GetActiveWithTagsAsync(workItemId, ct)
            ?? throw new NotFoundException("WorkItem", workItemId);

        // Track and record field changes
        TrackChange(item, updatedBy, "Title", item.Title, request.Title.Trim());
        TrackChange(item, updatedBy, "Description", item.Description, request.Description?.Trim());
        TrackChange(item, updatedBy, "Priority", item.Priority.ToString(), request.Priority.ToString());
        TrackChange(item, updatedBy, "AssigneeId", item.AssigneeId?.ToString(), request.AssigneeId?.ToString());
        TrackChange(item, updatedBy, "StoryPoints", item.StoryPoints?.ToString(), request.StoryPoints?.ToString());
        TrackChange(item, updatedBy, "TeamId", item.TeamId?.ToString(), request.TeamId?.ToString());

        var previousAssignee = item.AssigneeId;

        item.Title = request.Title.Trim();
        item.Description = request.Description?.Trim();
        item.Priority = request.Priority;
        item.AssigneeId = request.AssigneeId;
        item.StoryPoints = request.StoryPoints;
        item.TeamId = request.TeamId;
        item.UpdatedAt = DateTime.UtcNow;

        // Sync tags: remove old, add new
        var existingTags = item.Tags.ToList();
        var newTagNames = NormalizeTags(request.Tags);

        foreach (var removed in existingTags.Where(t => !newTagNames.Contains(t.Name)))
            _repository.RemoveTag(removed);

        foreach (var added in newTagNames.Where(n => existingTags.All(t => t.Name != n)))
            _repository.AddTag(new WorkItemTag { WorkItemId = item.Id, Name = added, CreatedBy = updatedBy });

        await _repository.SaveChangesAsync(ct);

        if (previousAssignee != request.AssigneeId)
        {
            await _eventBus.PublishAsync(
                new WorkItemAssigned(item.Id, item.ProjectId, previousAssignee, request.AssigneeId, updatedBy), ct);
        }

        return await MapToResponseAsync(workItemId, ct);
    }

    public async Task<WorkItemResponse> UpdateStateAsync(
        Guid workItemId,
        WorkItemState newState,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var item = await GetWorkItemOrThrowAsync(workItemId, ct);

        if (item.State == newState)
            throw new BusinessRuleException($"Work item is already in state '{newState}'.");

        ValidateStateTransition(item.State, newState);

        var oldState = item.State;
        AddHistory(item.Id, updatedBy, "State", oldState.ToString(), newState.ToString());

        item.State = newState;
        item.UpdatedAt = DateTime.UtcNow;

        await _repository.SaveChangesAsync(ct);

        await _eventBus.PublishAsync(
            new WorkItemStateChanged(item.Id, item.ProjectId, oldState, newState, updatedBy), ct);

        return await MapToResponseAsync(workItemId, ct);
    }

    public async Task DeleteAsync(Guid workItemId, Guid deletedBy, CancellationToken ct = default)
    {
        var item = await GetWorkItemOrThrowAsync(workItemId, ct);

        // Soft delete
        item.IsActive = false;
        item.UpdatedAt = DateTime.UtcNow;

        await _repository.SaveChangesAsync(ct);

        await _eventBus.PublishAsync(new WorkItemDeleted(item.Id, item.ProjectId, deletedBy), ct);

        _logger.LogInformation("WorkItem {Id} soft-deleted by {UserId}", workItemId, deletedBy);
    }

    // ── Comments ──────────────────────────────────────────────────────────────

    public async Task<WorkItemCommentResponse> AddCommentAsync(
        Guid workItemId,
        AddWorkItemCommentRequest request,
        Guid authorId,
        CancellationToken ct = default)
    {
        var item = await GetWorkItemOrThrowAsync(workItemId, ct);

        var comment = new WorkItemComment
        {
            WorkItemId = workItemId,
            AuthorId = authorId,
            Body = request.Body.Trim(),
            CreatedBy = authorId
        };

        _repository.AddComment(comment);
        item.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveChangesAsync(ct);

        await _eventBus.PublishAsync(
            new WorkItemCommentAdded(comment.Id, workItemId, item.ProjectId, authorId), ct);

        return MapCommentToResponse(comment);
    }

    public async Task<WorkItemCommentResponse> UpdateCommentAsync(
        Guid commentId,
        UpdateWorkItemCommentRequest request,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var comment = await _repository.GetCommentAsync(commentId, ct)
            ?? throw new NotFoundException("Comment", commentId);

        if (comment.AuthorId != updatedBy)
            throw new ForbiddenException("Only the comment author can edit this comment.");

        comment.Body = request.Body.Trim();
        comment.IsEdited = true;
        comment.UpdatedAt = DateTime.UtcNow;

        await _repository.SaveChangesAsync(ct);
        return MapCommentToResponse(comment);
    }

    public async Task DeleteCommentAsync(Guid commentId, Guid deletedBy, CancellationToken ct = default)
    {
        var comment = await _repository.GetCommentAsync(commentId, ct)
            ?? throw new NotFoundException("Comment", commentId);

        if (comment.AuthorId != deletedBy)
            throw new ForbiddenException("Only the comment author can delete this comment.");

        _repository.RemoveComment(comment);
        await _repository.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<WorkItemCommentResponse>> GetCommentsAsync(
        Guid workItemId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        _ = await GetWorkItemOrThrowAsync(workItemId, ct);

        var (items, total) = await _repository.GetCommentsAsync(
            workItemId, pagination.Skip, pagination.PageSize, ct);

        return new PagedResult<WorkItemCommentResponse>(
            items.Select(MapCommentToResponse).ToList(),
            total, pagination.Page, pagination.PageSize);
    }

    // ── History ───────────────────────────────────────────────────────────────

    public async Task<PagedResult<WorkItemHistoryResponse>> GetHistoryAsync(
        Guid workItemId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        _ = await GetWorkItemOrThrowAsync(workItemId, ct);

        var (items, total) = await _repository.GetHistoryAsync(
            workItemId, pagination.Skip, pagination.PageSize, ct);

        return new PagedResult<WorkItemHistoryResponse>(
            items.Select(h => new WorkItemHistoryResponse(
                h.Id, h.WorkItemId, h.ChangedBy, h.FieldName, h.OldValue, h.NewValue, h.CreatedAt
            )).ToList(),
            total, pagination.Page, pagination.PageSize);
    }

    // ── Links ─────────────────────────────────────────────────────────────────

    public async Task<WorkItemLinkResponse> AddLinkAsync(
        Guid workItemId,
        AddWorkItemLinkRequest request,
        Guid createdBy,
        CancellationToken ct = default)
    {
        var source = await GetWorkItemOrThrowAsync(workItemId, ct);
        var target = await _repository.GetActiveAsync(request.TargetId, ct)
            ?? throw new NotFoundException("Target work item", request.TargetId);

        if (source.ProjectId != target.ProjectId)
            throw new BusinessRuleException("Cannot link work items from different projects.");

        if (await _repository.LinkExistsAsync(workItemId, request.TargetId, request.LinkType, ct))
            throw new ConflictException("This link already exists.");

        var link = new WorkItemLink
        {
            SourceId = workItemId,
            TargetId = request.TargetId,
            LinkType = request.LinkType,
            CreatedBy = createdBy
        };

        _repository.AddLink(link);
        await _repository.SaveChangesAsync(ct);

        await _eventBus.PublishAsync(
            new WorkItemLinked(workItemId, request.TargetId, request.LinkType, createdBy), ct);

        return new WorkItemLinkResponse(
            link.Id, link.SourceId, link.TargetId, link.LinkType,
            target.Title, target.Type, target.State);
    }

    public async Task RemoveLinkAsync(Guid linkId, Guid removedBy, CancellationToken ct = default)
    {
        var link = await _repository.GetLinkAsync(linkId, ct)
            ?? throw new NotFoundException("WorkItemLink", linkId);

        _repository.RemoveLink(link);
        await _repository.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WorkItemLinkResponse>> GetLinksAsync(
        Guid workItemId,
        CancellationToken ct = default)
    {
        _ = await GetWorkItemOrThrowAsync(workItemId, ct);

        var links = await _repository.GetLinksWithTargetAsync(workItemId, ct);

        return links.Select(l => new WorkItemLinkResponse(
            l.Id, l.SourceId, l.TargetId, l.LinkType,
            l.Target.Title, l.Target.Type, l.Target.State
        )).ToList();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<WorkItem> GetWorkItemOrThrowAsync(Guid id, CancellationToken ct)
        => await _repository.GetActiveAsync(id, ct)
           ?? throw new NotFoundException("WorkItem", id);

    private void TrackChange(WorkItem item, Guid changedBy, string field, string? oldValue, string? newValue)
    {
        if (oldValue == newValue) return;
        AddHistory(item.Id, changedBy, field, oldValue, newValue);
    }

    private void AddHistory(Guid workItemId, Guid changedBy, string field, string? oldValue, string? newValue)
    {
        _repository.AddHistory(new WorkItemHistory
        {
            WorkItemId = workItemId,
            ChangedBy = changedBy,
            FieldName = field,
            OldValue = oldValue,
            NewValue = newValue,
            CreatedBy = changedBy
        });
    }

    /// <summary>
    /// Trims, lower-cases and de-duplicates tag names, dropping blanks. Normalization must happen
    /// before de-duplication so that casing variants collapse to the single row the unique
    /// (WorkItemId, Name) index allows.
    /// </summary>
    private static List<string> NormalizeTags(IEnumerable<string> tags) =>
        tags.Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

    private static void ValidateStateTransition(WorkItemState current, WorkItemState next)
    {
        // Allowed transitions for MVP state machine
        var allowed = current switch
        {
            WorkItemState.New => new[] { WorkItemState.Active },
            WorkItemState.Active => new[] { WorkItemState.Resolved, WorkItemState.Closed },
            WorkItemState.Resolved => new[] { WorkItemState.Closed, WorkItemState.Active },
            WorkItemState.Closed => new[] { WorkItemState.Active },
            _ => Array.Empty<WorkItemState>()
        };

        if (!allowed.Contains(next))
            throw new BusinessRuleException(
                $"Transition from '{current}' to '{next}' is not allowed. " +
                $"Valid next states: {string.Join(", ", allowed)}");
    }

    private static void ValidateHierarchy(WorkItemType parentType, WorkItemType childType)
    {
        var valid = (parentType, childType) switch
        {
            (WorkItemType.Epic, WorkItemType.Feature) => true,
            (WorkItemType.Feature, WorkItemType.UserStory) => true,
            (WorkItemType.UserStory, WorkItemType.Task) => true,
            (WorkItemType.UserStory, WorkItemType.Bug) => true,
            _ => false
        };

        if (!valid)
            throw new BusinessRuleException(
                $"A '{childType}' cannot be a child of '{parentType}'. " +
                "Valid hierarchy: Epic → Feature → Story → Task/Bug.");
    }

    private async Task<WorkItemResponse> MapToResponseAsync(Guid workItemId, CancellationToken ct)
    {
        var item = await _repository.GetWithTagsAsync(workItemId, ct)
            ?? throw new NotFoundException("WorkItem", workItemId);

        var commentCount = await _repository.GetCommentCountAsync(workItemId, ct);
        var childCount = await _repository.GetChildCountAsync(workItemId, ct);

        return new WorkItemResponse(
            item.Id,
            item.ProjectId,
            item.TeamId,
            item.ParentId,
            item.Type,
            item.State,
            item.Priority,
            item.Title,
            item.Description,
            item.AssigneeId,
            item.StoryPoints,
            item.Tags.Select(t => t.Name).ToList(),
            commentCount,
            childCount,
            item.CreatedAt,
            item.UpdatedAt,
            item.CreatedBy
        );
    }

    private static WorkItemCommentResponse MapCommentToResponse(WorkItemComment c) =>
        new(c.Id, c.WorkItemId, c.AuthorId, c.Body, c.IsEdited, c.CreatedAt, c.UpdatedAt);
}
