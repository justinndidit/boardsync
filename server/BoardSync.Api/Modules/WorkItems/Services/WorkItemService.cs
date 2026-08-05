using BoardSync.Api.Data;
using BoardSync.Api.Modules.WorkItems.DTOs;
using BoardSync.Api.Modules.WorkItems.Events;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.WorkItems.Services;

public class WorkItemService : IWorkItemService
{
    private readonly BoardSyncDbContext _context;
    private readonly IEventBus _eventBus;
    private readonly ILogger<WorkItemService> _logger;

    public WorkItemService(
        BoardSyncDbContext context,
        IEventBus eventBus,
        ILogger<WorkItemService> logger)
    {
        _context = context;
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
        if (!await _context.Projects.AnyAsync(p => p.Id == projectId && p.IsActive, ct))
            throw new NotFoundException("Project", projectId);

        if (request.ParentId.HasValue)
        {
            var parent = await _context.WorkItems
                .FirstOrDefaultAsync(w => w.Id == request.ParentId.Value && w.ProjectId == projectId && w.IsActive, ct)
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

        _context.WorkItems.Add(item);

        // Tags
        foreach (var tag in request.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct())
        {
            _context.WorkItemTags.Add(new WorkItemTag
            {
                WorkItemId = item.Id,
                Name = tag.Trim().ToLowerInvariant(),
                CreatedBy = createdBy
            });
        }

        // Initial history entry
        AddHistory(item.Id, createdBy, "State", null, WorkItemState.New.ToString());

        await _context.SaveChangesAsync(ct);

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
        var query = _context.WorkItems
            .Include(w => w.Tags)
            .Where(w => w.ProjectId == projectId && w.IsActive);

        if (filter.Type.HasValue)
            query = query.Where(w => w.Type == filter.Type.Value);

        if (filter.State.HasValue)
            query = query.Where(w => w.State == filter.State.Value);

        if (filter.AssigneeId.HasValue)
            query = query.Where(w => w.AssigneeId == filter.AssigneeId.Value);

        if (filter.TeamId.HasValue)
            query = query.Where(w => w.TeamId == filter.TeamId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Tag))
            query = query.Where(w => w.Tags.Any(t => t.Name == filter.Tag.ToLowerInvariant()));

        var total = await query.CountAsync(ct);

        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var page = Math.Max(filter.Page, 1);
        var skip = (page - 1) * pageSize;

        var items = await query
            .OrderByDescending(w => w.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);

        var childCounts = await _context.WorkItems
            .Where(w => w.ParentId != null && w.IsActive && items.Select(i => i.Id).Contains(w.ParentId.Value))
            .GroupBy(w => w.ParentId!.Value)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ParentId, x => x.Count, ct);

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
        var item = await _context.WorkItems
            .Include(w => w.Tags)
            .FirstOrDefaultAsync(w => w.Id == workItemId && w.IsActive, ct)
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
        var newTagNames = request.Tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

        foreach (var removed in existingTags.Where(t => !newTagNames.Contains(t.Name)))
            _context.WorkItemTags.Remove(removed);

        foreach (var added in newTagNames.Where(n => existingTags.All(t => t.Name != n)))
            _context.WorkItemTags.Add(new WorkItemTag { WorkItemId = item.Id, Name = added, CreatedBy = updatedBy });

        await _context.SaveChangesAsync(ct);

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

        await _context.SaveChangesAsync(ct);

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

        await _context.SaveChangesAsync(ct);

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

        _context.WorkItemComments.Add(comment);
        item.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

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
        var comment = await _context.WorkItemComments
            .FirstOrDefaultAsync(c => c.Id == commentId, ct)
            ?? throw new NotFoundException("Comment", commentId);

        if (comment.AuthorId != updatedBy)
            throw new ForbiddenException("Only the comment author can edit this comment.");

        comment.Body = request.Body.Trim();
        comment.IsEdited = true;
        comment.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return MapCommentToResponse(comment);
    }

    public async Task DeleteCommentAsync(Guid commentId, Guid deletedBy, CancellationToken ct = default)
    {
        var comment = await _context.WorkItemComments
            .FirstOrDefaultAsync(c => c.Id == commentId, ct)
            ?? throw new NotFoundException("Comment", commentId);

        if (comment.AuthorId != deletedBy)
            throw new ForbiddenException("Only the comment author can delete this comment.");

        _context.WorkItemComments.Remove(comment);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<WorkItemCommentResponse>> GetCommentsAsync(
        Guid workItemId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        _ = await GetWorkItemOrThrowAsync(workItemId, ct);

        var query = _context.WorkItemComments
            .Where(c => c.WorkItemId == workItemId)
            .OrderBy(c => c.CreatedAt);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .ToListAsync(ct);

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

        var query = _context.WorkItemHistory
            .Where(h => h.WorkItemId == workItemId)
            .OrderByDescending(h => h.CreatedAt);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .ToListAsync(ct);

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
        var target = await _context.WorkItems
            .FirstOrDefaultAsync(w => w.Id == request.TargetId && w.IsActive, ct)
            ?? throw new NotFoundException("Target work item", request.TargetId);

        if (source.ProjectId != target.ProjectId)
            throw new BusinessRuleException("Cannot link work items from different projects.");

        var duplicate = await _context.WorkItemLinks.AnyAsync(
            l => l.SourceId == workItemId && l.TargetId == request.TargetId && l.LinkType == request.LinkType, ct);

        if (duplicate)
            throw new ConflictException("This link already exists.");

        var link = new WorkItemLink
        {
            SourceId = workItemId,
            TargetId = request.TargetId,
            LinkType = request.LinkType,
            CreatedBy = createdBy
        };

        _context.WorkItemLinks.Add(link);
        await _context.SaveChangesAsync(ct);

        await _eventBus.PublishAsync(
            new WorkItemLinked(workItemId, request.TargetId, request.LinkType, createdBy), ct);

        return new WorkItemLinkResponse(
            link.Id, link.SourceId, link.TargetId, link.LinkType,
            target.Title, target.Type, target.State);
    }

    public async Task RemoveLinkAsync(Guid linkId, Guid removedBy, CancellationToken ct = default)
    {
        var link = await _context.WorkItemLinks
            .FirstOrDefaultAsync(l => l.Id == linkId, ct)
            ?? throw new NotFoundException("WorkItemLink", linkId);

        _context.WorkItemLinks.Remove(link);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WorkItemLinkResponse>> GetLinksAsync(
        Guid workItemId,
        CancellationToken ct = default)
    {
        _ = await GetWorkItemOrThrowAsync(workItemId, ct);

        var links = await _context.WorkItemLinks
            .Include(l => l.Target)
            .Where(l => l.SourceId == workItemId)
            .ToListAsync(ct);

        return links.Select(l => new WorkItemLinkResponse(
            l.Id, l.SourceId, l.TargetId, l.LinkType,
            l.Target.Title, l.Target.Type, l.Target.State
        )).ToList();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<WorkItem> GetWorkItemOrThrowAsync(Guid id, CancellationToken ct)
        => await _context.WorkItems.FirstOrDefaultAsync(w => w.Id == id && w.IsActive, ct)
           ?? throw new NotFoundException("WorkItem", id);

    private void TrackChange(WorkItem item, Guid changedBy, string field, string? oldValue, string? newValue)
    {
        if (oldValue == newValue) return;
        AddHistory(item.Id, changedBy, field, oldValue, newValue);
    }

    private void AddHistory(Guid workItemId, Guid changedBy, string field, string? oldValue, string? newValue)
    {
        _context.WorkItemHistory.Add(new WorkItemHistory
        {
            WorkItemId = workItemId,
            ChangedBy = changedBy,
            FieldName = field,
            OldValue = oldValue,
            NewValue = newValue,
            CreatedBy = changedBy
        });
    }

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
        var item = await _context.WorkItems
            .Include(w => w.Tags)
            .FirstAsync(w => w.Id == workItemId, ct);

        var commentCount = await _context.WorkItemComments.CountAsync(c => c.WorkItemId == workItemId, ct);
        var childCount = await _context.WorkItems.CountAsync(w => w.ParentId == workItemId && w.IsActive, ct);

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
