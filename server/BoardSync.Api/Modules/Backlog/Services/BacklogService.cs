using BoardSync.Api.Modules.Backlog.DTOs;
using BoardSync.Api.Modules.Backlog.Models;
using BoardSync.Api.Modules.Backlog.Repositories;
using BoardSync.Api.Modules.Sprints.Domain;
using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Services;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Exceptions;

namespace BoardSync.Api.Modules.Backlog.Services;

/// <summary>
/// The product backlog: which work items a project is carrying, and in what order.
/// </summary>
/// <remarks>
/// <para>
/// A backlog entry owns <em>rank</em>, and nothing else. Whether an item is in a sprint is the
/// Sprints module's business, so the two bulk operations here delegate to
/// <see cref="ISprintService"/> rather than writing <c>SprintWorkItem</c> rows themselves. That is
/// not tidiness: sprint membership carries an authorization rule — a work item may only join a
/// sprint belonging to its own team — and a second writer would be a second place for that rule to
/// be forgotten. <c>SprintId</c> here is a cached view of the answer, not the answer.
/// </para>
/// </remarks>
public class BacklogService : IBacklogService
{
    private readonly IBacklogRepository _repository;
    private readonly ISprintService _sprints;
    private readonly ILogger<BacklogService> _logger;

    public BacklogService(
        IBacklogRepository repository,
        ISprintService sprints,
        ILogger<BacklogService> logger)
    {
        _repository = repository;
        _sprints = sprints;
        _logger = logger;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<PagedResult<BacklogItemResponse>> GetForProjectAsync(
        Guid projectId,
        Guid? teamId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        var (entries, total) = await _repository.GetUnscheduledPageAsync(
            projectId, teamId, pagination.Skip, pagination.PageSize, ct);

        if (entries.Count == 0)
            return new PagedResult<BacklogItemResponse>([], total, pagination.Page, pagination.PageSize);

        var workItemIds = entries.Select(b => b.WorkItemId).ToList();

        var workItems = await _repository.GetWorkItemsAsync(workItemIds, ct);
        var childCounts = await _repository.GetChildCountsAsync(workItemIds, ct);

        // An entry whose work item has since been deleted is skipped rather than rendered blank.
        var items = entries
            .Where(b => workItems.ContainsKey(b.WorkItemId))
            .Select(b => Map(b, workItems[b.WorkItemId], childCounts.GetValueOrDefault(b.WorkItemId, 0)))
            .ToList();

        return new PagedResult<BacklogItemResponse>(items, total, pagination.Page, pagination.PageSize);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task<BacklogItemResponse> AddAsync(
        Guid projectId,
        AddToBacklogRequest request,
        Guid addedBy,
        CancellationToken ct = default)
    {
        if (!await _repository.ProjectExistsAsync(projectId, ct))
            throw new NotFoundException("Project", projectId);

        // Scoped to the project, so an item belonging to somewhere else reads as absent rather than
        // as forbidden — the caller has no business knowing it exists.
        var workItem = await _repository.GetWorkItemInProjectAsync(request.WorkItemId, projectId, ct)
            ?? throw new NotFoundException("WorkItem", request.WorkItemId);

        var existing = await _repository.GetEntryAsync(projectId, request.WorkItemId, ct);

        if (existing is not null)
            return await BuildResponseAsync(existing, workItem, ct);

        var entry = new BacklogItem
        {
            ProjectId = projectId,
            WorkItemId = request.WorkItemId,
            TeamId = request.TeamId,
            Rank = Ranking.Between(await _repository.GetMaxRankAsync(projectId, ct), null),
            CreatedBy = addedBy
        };

        _repository.Add(entry);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "WorkItem {WorkItemId} added to the backlog of project {ProjectId}",
            request.WorkItemId, projectId);

        return await BuildResponseAsync(entry, workItem, ct);
    }

    public async Task RemoveAsync(Guid projectId, Guid workItemId, CancellationToken ct = default)
    {
        var entry = await _repository.GetEntryAsync(projectId, workItemId, ct)
            ?? throw new NotFoundException("BacklogItem", workItemId);

        _repository.Remove(entry);
        await _repository.SaveChangesAsync(ct);
    }

    public async Task ReorderAsync(
        Guid projectId,
        ReorderBacklogRequest request,
        CancellationToken ct = default)
    {
        if (!await _repository.ProjectExistsAsync(projectId, ct))
            throw new NotFoundException("Project", projectId);

        var entries = await _repository.GetAllEntriesAsync(projectId, ct);
        var byWorkItem = entries.ToDictionary(b => b.WorkItemId);

        // Renumbers the named items across the whole backlog, so it is last-writer-wins between two
        // people submitting orderings at the same time. That is inherent to an endpoint that takes a
        // complete sequence; the fractional ranks exist so a *single* move need not do this, and a
        // move endpoint like the sprint backlog's is the fix if concurrent reordering becomes real.
        var rank = Ranking.Step;

        foreach (var workItemId in request.WorkItemIds)
        {
            if (!byWorkItem.TryGetValue(workItemId, out var entry))
                continue;

            entry.Rank = rank;
            rank += Ranking.Step;
        }

        // Anything the caller did not mention keeps its relative order, below the items that were.
        var named = request.WorkItemIds.ToHashSet();

        foreach (var entry in entries.Where(b => !named.Contains(b.WorkItemId)).OrderBy(b => b.Rank))
        {
            entry.Rank = rank;
            rank += Ranking.Step;
        }

        await _repository.SaveChangesAsync(ct);
    }

    public async Task<BacklogBulkOperationResponse> MoveToSprintAsync(
        Guid projectId,
        MoveToSprintRequest request,
        Guid movedBy,
        CancellationToken ct = default)
    {
        var entries = await _repository.GetEntriesAsync(projectId, request.WorkItemIds, ct);

        if (entries.Count == 0)
            return new BacklogBulkOperationResponse(0, "No matching backlog items found.");

        var moved = 0;

        foreach (var entry in entries)
        {
            // Delegated, so the sprint's own rule — that a work item may only join a sprint of its
            // own team — is enforced here too, by the code that owns it. A duplicate is not an
            // error: the caller asked for the item to end up in the sprint, and it is.
            try
            {
                await _sprints.AddWorkItemAsync(
                    request.SprintId,
                    new AddSprintWorkItemRequest { WorkItemId = entry.WorkItemId },
                    movedBy,
                    ct);
            }
            catch (ConflictException)
            {
            }

            entry.SprintId = request.SprintId;
            moved++;
        }

        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "{Count} item(s) moved into sprint {SprintId} from the backlog of project {ProjectId}",
            moved, request.SprintId, projectId);

        return new BacklogBulkOperationResponse(moved, $"{moved} item(s) moved to sprint.");
    }

    public async Task<BacklogBulkOperationResponse> ReturnToBacklogAsync(
        Guid projectId,
        ReturnToBacklogRequest request,
        Guid returnedBy,
        CancellationToken ct = default)
    {
        // Only entries actually in the named sprint. Matching on work item alone is what previously
        // let returning an item from one sprint remove it from every sprint it appeared in.
        var entries = (await _repository.GetEntriesAsync(projectId, request.WorkItemIds, ct))
            .Where(b => b.SprintId == request.SprintId)
            .ToList();

        if (entries.Count == 0)
            return new BacklogBulkOperationResponse(0, "No matching items found in that sprint.");

        foreach (var entry in entries)
        {
            await _sprints.RemoveWorkItemAsync(request.SprintId, entry.WorkItemId, returnedBy, ct);
            entry.SprintId = null;
        }

        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "{Count} item(s) returned from sprint {SprintId} to the backlog of project {ProjectId}",
            entries.Count, request.SprintId, projectId);

        return new BacklogBulkOperationResponse(entries.Count,
            $"{entries.Count} item(s) returned to backlog.");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<BacklogItemResponse> BuildResponseAsync(
        BacklogItem entry, WorkItem workItem, CancellationToken ct)
    {
        var counts = await _repository.GetChildCountsAsync([workItem.Id], ct);

        return Map(entry, workItem, counts.GetValueOrDefault(workItem.Id, 0));
    }

    private static BacklogItemResponse Map(BacklogItem entry, WorkItem w, int childCount) =>
        new(entry.Id, entry.WorkItemId, entry.ProjectId, entry.TeamId, entry.SprintId, entry.Rank,
            w.Title, w.Type, w.State, w.Priority, w.AssigneeId, w.StoryPoints,
            w.Tags.Select(t => t.Name).ToList(),
            childCount, w.CreatedAt);
}
