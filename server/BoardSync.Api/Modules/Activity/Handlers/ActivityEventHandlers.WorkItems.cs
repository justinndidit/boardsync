using BoardSync.Api.Modules.Activity.Models;
using BoardSync.Api.Modules.Sprints.Events;
using BoardSync.Api.Modules.WorkItems.Events;
using BoardSync.Api.Shared.Kernel.Events;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Activity.Handlers;

/// <summary>
/// Work item, sprint and board subscribers. Split from the OrgProject half of the class purely
/// for file length — see <see cref="ActivityEventHandlers"/> for the recording contract.
/// </summary>
public partial class ActivityEventHandlers :
    IEventHandler<WorkItemCreated>,
    IEventHandler<WorkItemUpdated>,
    IEventHandler<WorkItemStateChanged>,
    IEventHandler<WorkItemAssigned>,
    IEventHandler<WorkItemDeleted>,
    IEventHandler<WorkItemCommentAdded>,
    IEventHandler<WorkItemLinked>,
    IEventHandler<SprintCreated>,
    IEventHandler<SprintUpdated>,
    IEventHandler<SprintStatusChanged>,
    IEventHandler<SprintDeleted>,
    IEventHandler<SprintWorkItemAdded>,
    IEventHandler<SprintWorkItemRemoved>,
    IEventHandler<BoardChanged>
{
    // ── Work items ───────────────────────────────────────────────────────────
    //
    // The work item events carry ProjectId but not OrganizationId, so each handler resolves the
    // owning organization first. That is one indexed lookup per event, off the request's critical
    // path — cheaper than widening every event in the WorkItems module.

    public async Task HandleAsync(WorkItemCreated e, CancellationToken ct = default)
    {
        var scope = await ProjectScopeAsync(e.ProjectId, ct);
        if (scope is null) return;

        await RecordAsync(e, scope.Value.OrganizationId, ActivityEntityType.WorkItem, e.WorkItemId,
            e.Title, ActivityVerb.Created, e.CreatedByUserId, ct,
            projectId: e.ProjectId, fieldName: "Type", newValue: e.Type.ToString());
    }

    public async Task HandleAsync(WorkItemUpdated e, CancellationToken ct = default)
    {
        var scope = await ProjectScopeAsync(e.ProjectId, ct);
        if (scope is null) return;

        await RecordAsync(e, scope.Value.OrganizationId, ActivityEntityType.WorkItem, e.WorkItemId,
            await WorkItemTitleAsync(e.WorkItemId, ct), ActivityVerb.Updated, e.ChangedByUserId, ct,
            projectId: e.ProjectId, fieldName: e.FieldName, oldValue: e.OldValue, newValue: e.NewValue);
    }

    public async Task HandleAsync(WorkItemStateChanged e, CancellationToken ct = default)
    {
        var scope = await ProjectScopeAsync(e.ProjectId, ct);
        if (scope is null) return;

        await RecordAsync(e, scope.Value.OrganizationId, ActivityEntityType.WorkItem, e.WorkItemId,
            await WorkItemTitleAsync(e.WorkItemId, ct), ActivityVerb.StateChanged, e.ChangedByUserId, ct,
            projectId: e.ProjectId, fieldName: "State",
            oldValue: e.OldState.ToString(), newValue: e.NewState.ToString());
    }

    public async Task HandleAsync(WorkItemAssigned e, CancellationToken ct = default)
    {
        var scope = await ProjectScopeAsync(e.ProjectId, ct);
        if (scope is null) return;

        await RecordAsync(e, scope.Value.OrganizationId, ActivityEntityType.WorkItem, e.WorkItemId,
            await WorkItemTitleAsync(e.WorkItemId, ct), ActivityVerb.Assigned, e.ChangedByUserId, ct,
            projectId: e.ProjectId, fieldName: "Assignee",
            oldValue: e.PreviousAssigneeId is { } prev ? await UserNameAsync(prev, ct) : null,
            newValue: e.NewAssigneeId is { } next ? await UserNameAsync(next, ct) : null);
    }

    public async Task HandleAsync(WorkItemDeleted e, CancellationToken ct = default)
    {
        var scope = await ProjectScopeAsync(e.ProjectId, ct);
        if (scope is null) return;

        await RecordAsync(e, scope.Value.OrganizationId, ActivityEntityType.WorkItem, e.WorkItemId,
            await WorkItemTitleAsync(e.WorkItemId, ct), ActivityVerb.Deleted, e.DeletedByUserId, ct,
            projectId: e.ProjectId);
    }

    public async Task HandleAsync(WorkItemCommentAdded e, CancellationToken ct = default)
    {
        var scope = await ProjectScopeAsync(e.ProjectId, ct);
        if (scope is null) return;

        var body = await _context.WorkItemComments
            .Where(c => c.Id == e.CommentId)
            .Select(c => c.Body)
            .FirstOrDefaultAsync(ct);

        // The subject is the work item, not the comment — the feed reads "commented on X", and the
        // comment id travels in EntityId so the client can deep-link to it.
        await RecordAsync(e, scope.Value.OrganizationId, ActivityEntityType.Comment, e.CommentId,
            await WorkItemTitleAsync(e.WorkItemId, ct), ActivityVerb.Commented, e.AuthorId, ct,
            projectId: e.ProjectId, newValue: body);
    }

    public async Task HandleAsync(WorkItemLinked e, CancellationToken ct = default)
    {
        var source = await _context.WorkItems
            .Where(w => w.Id == e.SourceId)
            .Select(w => new { w.Title, w.ProjectId })
            .FirstOrDefaultAsync(ct);
        if (source is null) return;

        var scope = await ProjectScopeAsync(source.ProjectId, ct);
        if (scope is null) return;

        await RecordAsync(e, scope.Value.OrganizationId, ActivityEntityType.WorkItem, e.SourceId,
            source.Title, ActivityVerb.Linked, e.LinkedByUserId, ct,
            projectId: source.ProjectId, fieldName: e.LinkType.ToString(),
            newValue: await WorkItemTitleAsync(e.TargetId, ct));
    }

    // ── Sprints and boards ───────────────────────────────────────────────────

    public Task HandleAsync(SprintCreated e, CancellationToken ct = default) =>
        RecordAsync(e, e.OrganizationId, ActivityEntityType.Sprint, e.SprintId,
            e.Name, ActivityVerb.Created, e.CreatedByUserId, ct, teamId: e.TeamId);

    public Task HandleAsync(SprintUpdated e, CancellationToken ct = default) =>
        RecordAsync(e, e.OrganizationId, ActivityEntityType.Sprint, e.SprintId,
            e.Name, ActivityVerb.Updated, e.UpdatedByUserId, ct, teamId: e.TeamId,
            fieldName: e.FieldName, oldValue: e.OldValue, newValue: e.NewValue);

    public Task HandleAsync(SprintStatusChanged e, CancellationToken ct = default) =>
        RecordAsync(e, e.OrganizationId, ActivityEntityType.Sprint, e.SprintId,
            e.Name, ActivityVerb.StateChanged, e.ChangedByUserId, ct, teamId: e.TeamId,
            fieldName: "Status", oldValue: e.OldStatus.ToString(), newValue: e.NewStatus.ToString());

    public Task HandleAsync(SprintDeleted e, CancellationToken ct = default) =>
        RecordAsync(e, e.OrganizationId, ActivityEntityType.Sprint, e.SprintId,
            e.Name, ActivityVerb.Deleted, e.DeletedByUserId, ct, teamId: e.TeamId);

    public Task HandleAsync(SprintWorkItemAdded e, CancellationToken ct = default) =>
        RecordAsync(e, e.OrganizationId, ActivityEntityType.Sprint, e.SprintId,
            e.SprintName, ActivityVerb.Updated, e.AddedByUserId, ct, teamId: e.TeamId,
            fieldName: "Work item added", newValue: e.WorkItemTitle);

    public Task HandleAsync(SprintWorkItemRemoved e, CancellationToken ct = default) =>
        RecordAsync(e, e.OrganizationId, ActivityEntityType.Sprint, e.SprintId,
            e.SprintName, ActivityVerb.Updated, e.RemovedByUserId, ct, teamId: e.TeamId,
            fieldName: "Work item removed", oldValue: e.WorkItemTitle);

    public Task HandleAsync(BoardChanged e, CancellationToken ct = default) =>
        RecordAsync(e, e.OrganizationId, ActivityEntityType.Board, e.BoardId,
            e.BoardName, ActivityVerb.Updated, e.ChangedByUserId, ct, projectId: e.ProjectId,
            fieldName: e.Change, oldValue: e.OldValue, newValue: e.NewValue);

    // ── Lookups ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Owning organization of a project, or null if the project has since been hard-deleted — in
    /// which case there is no organization to file the entry under and it is dropped.
    /// </summary>
    private async Task<(Guid OrganizationId, string Name)?> ProjectScopeAsync(Guid projectId, CancellationToken ct)
    {
        var row = await _context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new { p.OrganizationId, p.Name })
            .FirstOrDefaultAsync(ct);

        return row is null ? null : (row.OrganizationId, row.Name);
    }

    private async Task<string> WorkItemTitleAsync(Guid workItemId, CancellationToken ct) =>
        await _context.WorkItems.Where(w => w.Id == workItemId)
            .Select(w => w.Title).FirstOrDefaultAsync(ct) ?? string.Empty;
}
