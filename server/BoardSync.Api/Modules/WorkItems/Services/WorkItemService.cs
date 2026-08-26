using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Modules.WorkItems.Domain;
using BoardSync.Api.Modules.WorkItems.DTOs;
using BoardSync.Api.Modules.WorkItems.Events;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Modules.WorkItems.Repository;
using BoardSync.Api.Shared.Auth.Services;
using BoardSync.Api.Shared.Auth.Services.Implementations;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.WorkItems.Services;

/// <summary>
/// Business logic for the WorkItems module: validation, the state machine, the type
/// hierarchy, audit history and domain events. All persistence goes through
/// <see cref="IWorkItemRepository"/>; project references go through <see cref="IProjectService"/>.
/// </summary>
/// <remarks>
/// <b>Every <c>_eventBus.Enqueue</c> must come before its <c>SaveChangesAsync</c>.</b>
/// <see cref="Shared.Kernel.Events.OutboxEventBus"/> does no I/O — it stages an outbox row on the
/// request's shared <c>DbContext</c>, which is what makes the event and the change it describes
/// commit together. Enqueueing afterwards leaves that row in the change tracker with nothing left to
/// persist it, so it is discarded when the scope is disposed: no exception, no log, the event simply
/// never happened. Every method here had the two the wrong way round, which meant no work item event
/// was ever delivered — see the note on <see cref="CreateAsync"/>.
/// </remarks>
public class WorkItemService : IWorkItemService
{
    private readonly IWorkItemRepository _repository;
    private readonly IProjectService _projectService;
    private readonly ITeamService _teamService;
    private readonly IRbacService _rbac;
    private readonly IEventBus _eventBus;
    private readonly ILogger<WorkItemService> _logger;

    public WorkItemService(
        IWorkItemRepository repository,
        IProjectService projectService,
        ITeamService teamService,
        IRbacService rbac,
        IEventBus eventBus,
        ILogger<WorkItemService> logger)
    {
        _repository = repository;
        _projectService = projectService;
        _teamService = teamService;
        _rbac = rbac;
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
        // The parse result is checked rather than discarded. Ignoring it left the out parameter at
        // its default — Epic — so an unrecognised or misspelled type was silently created as an
        // epic instead of being rejected, and the caller got a 201 describing something they had
        // not asked for.
        if (!Enum.TryParse<WorkItemType>(request.Type, ignoreCase: true, out var workItemTypeParsed))
            throw new BusinessRuleException(
                $"'{request.Type}' is not a valid work item type. Valid types: {string.Join(", ", Enum.GetNames<WorkItemType>())}.");

        if (!await _projectService.ExistsAsync(projectId, ct))
            throw new NotFoundException("Project", projectId);

        if (request.ParentId.HasValue)
        {
            var parent = await _repository.GetActiveInProjectAsync(request.ParentId.Value, projectId, ct)
                ?? throw new NotFoundException("Parent work item", request.ParentId.Value);

            ValidateHierarchy(parent.Type, workItemTypeParsed);
        }

        // A work item is always assigned, and only to someone on the owning team.
        // BusinessRuleException carries the reason through to the caller as a 422;
        // InvalidOperationException would surface as a bare 400 "Invalid operation".
        if (!await _teamService.IsMemberAsync(request.TeamId, request.AssigneeId, ct))
            throw new BusinessRuleException(
                $"User '{request.AssigneeId}' is not a member of team '{request.TeamId}' and cannot be assigned this work item.");

        var item = new WorkItem
        {
            ProjectId = projectId,
            Number = await _projectService.TakeNextWorkItemNumberAsync(projectId, ct),
            TeamId = request.TeamId,
            ParentId = request.ParentId,
            Type = workItemTypeParsed,
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
        AddHistory(item, createdBy, "State", null, WorkItemState.New.ToString());

        _eventBus.Enqueue(new WorkItemCreated(item.Id, projectId, item.Type, item.Title, createdBy));

        await _repository.SaveChangesAsync(ct);

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

        ExpectVersion(item, request.ExpectedVersion);

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

        SyncTags(item, NormalizeTags(request.Tags), updatedBy);

        if (previousAssignee != request.AssigneeId)
        {
            _eventBus.Enqueue(new WorkItemAssigned(item.Id, item.ProjectId, previousAssignee, request.AssigneeId, updatedBy));
        }

        await SaveDetectingConflictsAsync(workItemId, ct);

        return await MapToResponseAsync(workItemId, ct);
    }

    public async Task<WorkItemResponse> PatchAsync(
        Guid workItemId,
        PatchWorkItemRequest request,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var item = await _repository.GetActiveWithTagsAsync(workItemId, ct)
            ?? throw new NotFoundException("WorkItem", workItemId);

        ExpectVersion(item, request.ExpectedVersion);

        // Resolved once, so the value written and the value recorded in history are the same thing
        // and cannot drift apart. Or(current) leaves a field untouched when it was not mentioned.
        var title = request.Title.IsSet ? Trimmed(request.Title.Value, "title") : item.Title;
        var description = request.Description.IsSet
            ? request.Description.Value?.Trim()
            : item.Description;
        var priority = request.Priority.Or(item.Priority);
        var assigneeId = request.AssigneeId.Or(item.AssigneeId);
        var teamId = request.TeamId.Or(item.TeamId);
        var storyPoints = request.StoryPoints.Or(item.StoryPoints);

        ValidateLength(title, 255, "title");
        ValidateLength(description, 10000, "description");

        if (storyPoints is < 0 or > 1000)
            throw new BusinessRuleException("Story points must be between 0 and 1000.");

        // Only when the caller is actually moving the work, and only against the team it will
        // belong to afterwards — the same rule CreateAsync applies.
        if ((request.AssigneeId.IsSet || request.TeamId.IsSet)
            && assigneeId is { } newAssignee
            && teamId is { } owningTeam
            && !await _teamService.IsMemberAsync(owningTeam, newAssignee, ct))
        {
            throw new BusinessRuleException(
                $"User '{newAssignee}' is not a member of team '{owningTeam}' and cannot be " +
                "assigned this work item.");
        }

        TrackChange(item, updatedBy, "Title", item.Title, title);
        TrackChange(item, updatedBy, "Description", item.Description, description);
        TrackChange(item, updatedBy, "Priority", item.Priority.ToString(), priority.ToString());
        TrackChange(item, updatedBy, "AssigneeId", item.AssigneeId?.ToString(), assigneeId?.ToString());
        TrackChange(item, updatedBy, "StoryPoints", item.StoryPoints?.ToString(), storyPoints?.ToString());
        TrackChange(item, updatedBy, "TeamId", item.TeamId?.ToString(), teamId?.ToString());

        var previousAssignee = item.AssigneeId;

        item.Title = title;
        item.Description = description;
        item.Priority = priority;
        item.AssigneeId = assigneeId;
        item.TeamId = teamId;
        item.StoryPoints = storyPoints;
        item.UpdatedAt = DateTime.UtcNow;

        // Absent tags means "leave the tags alone", not "remove them all" — the difference a full
        // replace cannot express.
        if (request.Tags.IsSet)
            SyncTags(item, NormalizeTags(request.Tags.Value ?? []), updatedBy);

        if (previousAssignee != assigneeId)
            _eventBus.Enqueue(new WorkItemAssigned(item.Id, item.ProjectId, previousAssignee, assigneeId, updatedBy));

        await SaveDetectingConflictsAsync(workItemId, ct);

        return await MapToResponseAsync(workItemId, ct);
    }

    public async Task<WorkItemResponse> UpdateStateAsync(
        Guid workItemId,
        WorkItemState newState,
        Guid updatedBy,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        var item = await GetWorkItemOrThrowAsync(workItemId, ct);

        ExpectVersion(item, expectedVersion);

        if (item.State == newState)
            throw new BusinessRuleException($"Work item is already in state '{newState}'.");

        ValidateStateTransition(item.State, newState);
        await AuthorizeTransitionAsync(item, newState, updatedBy, ct);

        var oldState = item.State;
        AddHistory(item, updatedBy, "State", oldState.ToString(), newState.ToString());

        item.State = newState;
        item.UpdatedAt = DateTime.UtcNow;

        _eventBus.Enqueue(new WorkItemStateChanged(item.Id, item.ProjectId, oldState, newState, updatedBy));

        await SaveDetectingConflictsAsync(workItemId, ct);

        return await MapToResponseAsync(workItemId, ct);
    }

    public async Task DeleteAsync(Guid workItemId, Guid deletedBy, CancellationToken ct = default)
    {
        var item = await GetWorkItemOrThrowAsync(workItemId, ct);

        // Soft delete
        item.IsActive = false;
        item.UpdatedAt = DateTime.UtcNow;

        _eventBus.Enqueue(new WorkItemDeleted(item.Id, item.ProjectId, deletedBy));

        await _repository.SaveChangesAsync(ct);

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
        _eventBus.Enqueue(new WorkItemCommentAdded(comment.Id, workItemId, item.ProjectId, authorId));

        await _repository.SaveChangesAsync(ct);

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
                h.Id, h.WorkItemId, h.ChangedBy, h.ActorType, h.AttributedToUserId,
                h.FieldName, h.OldValue, h.NewValue, h.CreatedAt
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
        _eventBus.Enqueue(new WorkItemLinked(workItemId, request.TargetId, request.LinkType, createdBy));

        await _repository.SaveChangesAsync(ct);

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

    // ── Scope resolution ──────────────────────────────────────────────────────

    public async Task<Guid> GetProjectIdForLinkAsync(Guid linkId, CancellationToken ct = default)
        => await _repository.GetProjectIdForLinkAsync(linkId, ct)
           ?? throw new NotFoundException("WorkItemLink", linkId);

    public async Task<Guid> GetProjectIdForCommentAsync(Guid commentId, CancellationToken ct = default)
        => await _repository.GetProjectIdForCommentAsync(commentId, ct)
           ?? throw new NotFoundException("Comment", commentId);

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<WorkItem> GetWorkItemOrThrowAsync(Guid id, CancellationToken ct)
        => await _repository.GetActiveAsync(id, ct)
           ?? throw new NotFoundException("WorkItem", id);

    /// <summary>
    /// Tells EF which version of the row the caller was working from, so a concurrent write is
    /// detected instead of silently overwritten.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="WorkItem.Version"/> maps to Postgres' <c>xmin</c>, which the server bumps on every
    /// update by itself. EF's own check only spans load-to-save inside one request, which is not
    /// where the conflict lives: the real race is A reads, B saves, A saves. Closing it needs the
    /// version the client actually read, which is what this applies.
    /// </para>
    /// <para>
    /// All of this machinery already existed — the mapped column, the repository helper, the DTO
    /// field, the controller wiring — and nothing ever called it. The version travelled from the
    /// client to this service and was dropped, so the guarantee the DTO documents was never made.
    /// </para>
    /// <para>
    /// A missing <paramref name="expectedVersion"/> stays last-write-wins, deliberately: making it
    /// required would break every client that does not send one yet, and the safe default is
    /// available to anyone who opts in.
    /// </para>
    /// </remarks>
    private void ExpectVersion(WorkItem item, long? expectedVersion)
    {
        if (expectedVersion is not { } version) return;

        // xmin is a 32-bit unsigned counter; the DTO carries it as a long because JSON has no
        // unsigned integers. Anything outside that range cannot be a version this server issued.
        if (version is < 0 or > uint.MaxValue)
            throw new BusinessRuleException(
                $"'{version}' is not a valid work item version. Send back the 'version' field from " +
                "the item you read.");

        _repository.SetOriginalVersion(item, (uint)version);
    }

    /// <summary>
    /// Saves, turning a lost update into a 409 rather than a silent overwrite.
    /// </summary>
    /// <remarks>
    /// The client's move on a 409 is to re-read and reconcile, so the message says so. The current
    /// state is deliberately not embedded in the error: the caller needs the whole item to merge
    /// against, and a GET returns it in the shape they already parse.
    /// </remarks>
    private async Task SaveDetectingConflictsAsync(Guid workItemId, CancellationToken ct)
    {
        try
        {
            await _repository.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(
                "This work item was changed by someone else after you loaded it. Re-read it and " +
                "apply your change to the current version.");
        }
    }

    private void TrackChange(WorkItem item, Guid changedBy, string field, string? oldValue, string? newValue)
    {
        if (oldValue == newValue) return;
        AddHistory(item, changedBy, field, oldValue, newValue);
    }

    /// <summary>
    /// Records one field change against a work item.
    /// </summary>
    /// <remarks>
    /// Takes the work item rather than its id so <see cref="WorkItemHistory.ProjectId"/> cannot be
    /// left unset. It was: every history row ever written carried <c>Guid.Empty</c>, because this
    /// method only ever received an id and there was nothing to copy the project from. The column
    /// exists, the migration shipped it, and <c>(ProjectId, CreatedAt)</c> was indexed for it — but
    /// nothing wrote it, so the notification feed, which filters on exactly that column, returned
    /// nothing to anybody. Passing the entity makes the omission unrepresentable rather than merely
    /// fixed.
    /// </remarks>
    private void AddHistory(WorkItem item, Guid changedBy, string field, string? oldValue, string? newValue)
    {
        _repository.AddHistory(new WorkItemHistory
        {
            WorkItemId = item.Id,
            ProjectId = item.ProjectId,
            ChangedBy = changedBy,
            FieldName = field,
            OldValue = oldValue,
            NewValue = newValue,
            CreatedBy = changedBy
        });
    }

    /// <summary>Replaces an item's tags with exactly <paramref name="tagNames"/>.</summary>
    private void SyncTags(WorkItem item, List<string> tagNames, Guid actingUserId)
    {
        var existing = item.Tags.ToList();

        foreach (var removed in existing.Where(t => !tagNames.Contains(t.Name)))
            _repository.RemoveTag(removed);

        foreach (var added in tagNames.Where(n => existing.All(t => t.Name != n)))
            _repository.AddTag(new WorkItemTag { WorkItemId = item.Id, Name = added, CreatedBy = actingUserId });
    }

    /// <summary>
    /// Rejects a required text field the caller sent as null or blank.
    /// </summary>
    /// <remarks>
    /// <c>[Required]</c> cannot see through <see cref="Patch{T}"/> — it inspects the struct, not the
    /// string — so fields carried that way are checked here instead. The trade is deliberate: being
    /// able to tell "absent" from "explicitly null" is worth validating a couple of fields by hand.
    /// </remarks>
    private static string Trimmed(string? value, string field)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
            throw new BusinessRuleException($"A work item's {field} cannot be empty.");

        return trimmed;
    }

    private static void ValidateLength(string? value, int max, string field)
    {
        if (value is not null && value.Length > max)
            throw new BusinessRuleException($"A work item's {field} cannot exceed {max} characters.");
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

    /// <summary>
    /// Rejects a transition the caller is not entitled to make.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not an attribute.</b> Which permission a transition needs depends on the state
    /// being moved from and to, and the target arrives in the request body — so
    /// <c>[RequirePermission]</c>, which resolves a scope from a route parameter before model binding,
    /// cannot express it. The endpoint still declares <c>workitem:write</c> as the floor: touching a
    /// work item's state at all requires being able to write to it. This adds the rest.
    /// </para>
    /// <para>
    /// It costs a permission check only when the transition needs more than the floor already proved,
    /// which is the moves out of the QA lane.
    /// </para>
    /// <para>
    /// <b>Self-certification.</b> Signing off your own work defeats the point of a separate
    /// authority, so by default the assignee cannot certify their own item even when they hold
    /// <c>workitem:verify</c>. It is a project setting rather than a rule, because a three-person team
    /// where everybody tests is a real shape, and the alternative — them granting each other
    /// <c>project:admin</c> to route around it — is worse than letting them turn it off knowingly.
    /// A project administrator is exempt: they can already grant themselves anything.
    /// </para>
    /// </remarks>
    private async Task AuthorizeTransitionAsync(
        WorkItem item, WorkItemState newState, Guid actingUserId, CancellationToken ct)
    {
        var required = WorkItemStateMachine.RequiredPermission(item.State, newState);

        if (required != Permissions.WorkItemWrite
            && !await _rbac.HasPermissionAsync(
                actingUserId, required, RoleScope.Project, item.ProjectId, ct))
        {
            // Deliberately a bare ForbiddenException: the middleware answers these with a generic
            // "Access forbidden", the same as every other denial, so a refusal never doubles as a
            // description of what the caller lacks. A client that wants to know before trying reads
            // the transition's requiresPermission from GET /api/metadata and checks it against
            // GET /api/me/capabilities — which is what those endpoints are for.
            throw new ForbiddenException(
                $"Moving a work item from '{item.State}' to '{newState}' requires '{required}'.");
        }

        if (newState is not WorkItemState.Closed || item.AssigneeId != actingUserId) return;

        if (await _projectService.AllowsSelfCertificationAsync(item.ProjectId, ct)) return;

        if (await _rbac.HasPermissionAsync(
                actingUserId, Permissions.ProjectAdmin, RoleScope.Project, item.ProjectId, ct))
            return;

        // BusinessRuleException, not ForbiddenException. The caller is not missing a permission —
        // they hold workitem:verify — so answering "access forbidden" would send them looking for a
        // grant that would not help. This is a rule about whose work it is, and the 422 carries the
        // explanation, matching how every other business rule in the system answers.
        throw new BusinessRuleException(
            "You cannot certify work assigned to you. Ask someone else with testing authority to " +
            "verify it, or enable self-certification on the project.");
    }

    /// <summary>
    /// Rejects a state change the workflow does not allow.
    /// </summary>
    /// <remarks>
    /// The table lives in <see cref="WorkItemStateMachine"/> rather than here, so the rule the
    /// service enforces and the rule <c>GET /api/metadata</c> publishes are the same one. A client
    /// building its "Move to…" menu from a second copy would offer transitions this method rejects.
    /// </remarks>
    private static void ValidateStateTransition(WorkItemState current, WorkItemState next)
    {
        if (WorkItemStateMachine.CanTransition(current, next)) return;

        throw new BusinessRuleException(
            $"Transition from '{current}' to '{next}' is not allowed. " +
            $"Valid next states: {string.Join(", ", WorkItemStateMachine.AllowedFrom(current))}");
    }

    /// <summary>
    /// Rejects a parent/child pairing the hierarchy does not allow.
    /// </summary>
    /// <remarks>See the note on <see cref="ValidateStateTransition"/> — same reason, same table.</remarks>
    private static void ValidateHierarchy(WorkItemType parentType, WorkItemType childType)
    {
        if (WorkItemHierarchy.CanNest(parentType, childType)) return;

        throw new BusinessRuleException(
            $"A '{childType}' cannot be a child of '{parentType}'. " +
            $"Valid hierarchy: {WorkItemHierarchy.Description}.");
    }

    private async Task<WorkItemResponse> MapToResponseAsync(Guid workItemId, CancellationToken ct)
    {
        var item = await _repository.GetWithTagsAsync(workItemId, ct)
            ?? throw new NotFoundException("WorkItem", workItemId);

        var commentCount = await _repository.GetCommentCountAsync(workItemId, ct);
        var childCount = await _repository.GetChildCountAsync(workItemId, ct);

        var projectKey = await _projectService.GetKeyAsync(item.ProjectId, ct);

        return new WorkItemResponse(
            item.Id,
            item.ProjectId,
            item.Number,
            $"{projectKey}-{item.Number}",
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
            item.CreatedBy,
            item.Version
        );
    }

    private static WorkItemCommentResponse MapCommentToResponse(WorkItemComment c) =>
        new(c.Id, c.WorkItemId, c.AuthorId, c.Body, c.IsEdited, c.CreatedAt, c.UpdatedAt);
}
