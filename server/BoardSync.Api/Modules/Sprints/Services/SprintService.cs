
using BoardSync.Api.Data;
using BoardSync.Api.Modules.Backlog.Models;
using BoardSync.Api.Modules.Backlog.Services;
using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.Sprints.Domain;
using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Events;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.Sprints.Repositories.Interfaces;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Modules.WorkItems.Repository;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Sprints.Services;

public class SprintService : ISprintService
{
    private readonly ISprintRepository _repository;
    private readonly IWorkItemRepository _workItems;
    private readonly IEventBus _eventBus;
    private readonly IBacklogSprintLink _backlog;
    private readonly ILogger<SprintService> _logger;
    private readonly BoardSyncDbContext _context;   // ← added field

    // ── Constructor — fixed: removed duplicate IEventBus, added _context ──
    public SprintService(
        BoardSyncDbContext context,
        ISprintRepository repository,
        IWorkItemRepository workItems,
        IEventBus eventBus,
        IBacklogSprintLink backlog,
        ILogger<SprintService> logger)
    {
        _context        = context;        // ← now properly assigned
        _repository     = repository;
        _workItems      = workItems;
        _eventBus       = eventBus;
        _backlog = backlog;
        _logger         = logger;
    }

    /// <summary>
    /// Checks a sprint's window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// End is compared as an <b>instant</b>: sprints carry a time of day now, so a team can end one
    /// at five on a Friday rather than at midnight UTC, and comparing dates would have let a sprint
    /// end before it started on the same day.
    /// </para>
    /// <para>
    /// Start is compared against <b>the beginning of today</b>, not the current instant. "Not in the
    /// past" has always meant "not a past day" here — a sprint that began at nine this morning is an
    /// ordinary thing to record, and rejecting it would also make "start now" fail intermittently
    /// as the request aged in flight.
    /// </para>
    /// <para>
    /// <paramref name="mustBeFuture"/> is false when editing: an Active sprint's start is in the
    /// past by definition, and requiring otherwise made a running sprint's end date uneditable.
    /// </para>
    /// </remarks>
    private static void ValidateDates(
        DateTime startDate, DateTime endDate, bool mustBeFuture = true)
    {
        if (mustBeFuture && startDate < DateTime.UtcNow.Date)
            throw new BusinessRuleException("Sprint start cannot be before today.");

        if (endDate <= startDate)
            throw new BusinessRuleException("A sprint must end after it starts.");
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<SprintResponse> CreateAsync(
        Guid teamId,
        CreateSprintRequest request,
        Guid createdBy,
        CancellationToken ct = default)
    {
        if (!await _repository.TeamExistsAsync(teamId, ct))
            throw new NotFoundException("Team", teamId);

        ValidateDates(request.StartDate, request.EndDate);

        if (await _repository.HasOverlappingSprintAsync(teamId, request.StartDate, request.EndDate, ct))
            throw new ConflictException("Sprint dates overlap with an existing sprint for this team.");

        var sprint = new Sprint
        {
            TeamId    = teamId,
            Number    = await _repository.GetNextNumberAsync(teamId, ct),
            Goal      = request.Goal?.Trim(),
            // The whole instant, not the date. Truncating to midnight threw away the time a team
            // chose and made every sprint end at 00:00 UTC, which is the middle of somebody's night.
            StartDate = DateTime.SpecifyKind(request.StartDate.ToUniversalTime(), DateTimeKind.Utc),
            EndDate   = DateTime.SpecifyKind(request.EndDate.ToUniversalTime(), DateTimeKind.Utc),
            Status    = SprintStatus.Planning,
            CreatedBy = createdBy
        };

        _repository.Add(sprint);

        await EnqueueAsync(sprint, orgId => new SprintCreated(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint), createdBy), ct);

        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Sprint {Number} created for team {TeamId} by {UserId}",
            sprint.Number, sprint.TeamId, createdBy);

        return await BuildResponseAsync(sprint.Id, ct);
    }

    public async Task<SprintResponse> GetByIdAsync(
        Guid sprintId,
        CancellationToken ct = default)
    {
        _ = await GetOrThrowAsync(sprintId, ct);
        return await BuildResponseAsync(sprintId, ct);
    }

    public async Task<PagedResult<SprintSummaryResponse>> GetForTeamAsync(
        Guid teamId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        var (items, total) = await _repository.GetForTeamAsync(
            teamId, pagination.Skip, pagination.PageSize, ct);

        return new PagedResult<SprintSummaryResponse>(items, total, pagination.Page, pagination.PageSize);
    }

    /// <summary>The sprints of the team that builds a project.</summary>
    public async Task<PagedResult<SprintSummaryResponse>> GetForProjectAsync(
        Guid projectId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        var teamId = await _repository.GetTeamIdForProjectAsync(projectId, ct)
            ?? throw new NotFoundException("Project", projectId);

        return await GetForTeamAsync(teamId, pagination, ct);
    }

    public async Task<SprintResponse?> GetActiveForProjectAsync(
        Guid projectId,
        CancellationToken ct = default)
    {
        var sprint = await _repository.GetActiveForProjectAsync(projectId, ct);
        return sprint is null ? null : await BuildResponseAsync(sprint.Id, ct);
    }

    public async Task<SprintResponse> UpdateAsync(
        Guid sprintId,
        UpdateSprintRequest request,
        Guid updatedBy,
        CancellationToken ct = default)
    {
       var sprint = await GetOrThrowAsync(sprintId, ct);

        /*
         * A running sprint can be extended or cut short — that happens, and refusing it meant
         * closing the sprint and making another, which loses its number and its record.
         *
         * Completed stays locked. Its dates are what every figure was measured against — completed
         * points are counted at the end date — so moving them would rewrite past velocity with no
         * event to explain the change.
         */
        if (sprint.Status == SprintStatus.Completed)
            throw new BusinessRuleException(
                "A completed sprint cannot be edited. Its dates are what its figures were measured "
                + "against, and moving them would change what it reported having delivered.");

        // Editing, so the start is allowed to be in the past — it usually is.
        ValidateDates(request.StartDate, request.EndDate, mustBeFuture: false);

        var changes      = new List<(string Field, string? Old, string? New)>();
        var newGoal      = request.Goal?.Trim();
        var newStartDate = DateTime.SpecifyKind(request.StartDate.ToUniversalTime(), DateTimeKind.Utc);
        var newEndDate   = DateTime.SpecifyKind(request.EndDate.ToUniversalTime(), DateTimeKind.Utc);

        if (sprint.Goal      != newGoal)
            changes.Add(("Goal",      sprint.Goal,                    newGoal));
        if (sprint.StartDate != newStartDate)
            changes.Add(("StartDate", sprint.StartDate.ToString("u"), newStartDate.ToString("u")));
        if (sprint.EndDate   != newEndDate)
            changes.Add(("EndDate",   sprint.EndDate.ToString("u"),   newEndDate.ToString("u")));

        sprint.Goal      = newGoal;
        sprint.StartDate = newStartDate;
        sprint.EndDate   = newEndDate;
        sprint.UpdatedAt = DateTime.UtcNow;

        foreach (var (field, oldValue, newValue) in changes)
        {
            await EnqueueAsync(sprint, orgId => new SprintUpdated(
                sprint.Id, sprint.TeamId, orgId, SprintName(sprint),
                field, oldValue, newValue, updatedBy), ct);
        }

        await _repository.SaveChangesAsync(ct);
        return await BuildResponseAsync(sprintId, ct);
    }

    public async Task<SprintResponse> UpdateStatusAsync(
        Guid sprintId,
        SprintStatus newStatus,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var sprint = await GetOrThrowAsync(sprintId, ct);

        /*
         * Completing is not a status change.
         *
         * It is a status change *plus* a decision about the work that did not finish, and this
         * endpoint only ever made the first half — leaving unfinished items attached to a sprint
         * nobody opens again, in no backlog, in no sprint, on no board. The UI was moved to
         * POST /close; this closes the door behind it, because the door was the bug rather than the
         * client that walked through it.
         */
        if (newStatus == SprintStatus.Completed)
        {
            throw new BusinessRuleException(
                "Use POST /api/sprints/{id}/close to complete a sprint. Closing decides where "
                + "unfinished work goes, and a bare status change would strand it.");
        }

        ValidateTransition(sprint.Status, newStatus);

        if (newStatus == SprintStatus.Active
            && await _repository.HasAnotherActiveSprintAsync(sprint.TeamId, sprintId, ct))
        {
            throw new ConflictException(
                "Another sprint is already active for this team. Complete it before starting a new one.");
        }

        var oldStatus    = sprint.Status;
        sprint.Status    = newStatus;
        sprint.UpdatedAt = DateTime.UtcNow;

        await EnqueueAsync(sprint, orgId => new SprintStatusChanged(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint),
            oldStatus, newStatus, updatedBy), ct);

        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Sprint {SprintId} → {Status} by {UserId}",
            sprintId, newStatus, updatedBy);

        return await BuildResponseAsync(sprintId, ct);
    }

    public async Task DeleteAsync(
        Guid sprintId,
        Guid deletedBy,
        CancellationToken ct = default)
    {
        var sprint = await GetOrThrowAsync(sprintId, ct);

        if (sprint.Status != SprintStatus.Planning)
            throw new BusinessRuleException("Only Planning sprints can be deleted.");

        if (await _repository.HasBacklogEntriesAsync(sprintId, ct))
            throw new BusinessRuleException("Remove all work items from the sprint before deleting it.");

        _repository.Remove(sprint);

        await EnqueueAsync(sprint, orgId => new SprintDeleted(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint), deletedBy), ct);

        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Sprint {SprintId} deleted by {UserId}", sprintId, deletedBy);
    }

    // ── Backlog ───────────────────────────────────────────────────────────────

    public async Task<bool> IsDecompositionOfSprintWorkAsync(
        Guid sprintId,
        Guid workItemId,
        CancellationToken ct = default)
    {
        var workItem = await _workItems.GetActiveAsync(workItemId, ct);

        // A missing item is not a decomposition of anything. Saying no here also keeps the caller
        // on the path that reports the item as not found, rather than as forbidden.
        if (workItem?.ParentId is not Guid parentId)
            return false;

        return await _repository.BacklogContainsAsync(sprintId, parentId, ct);
    }

    public async Task<SprintWorkItemResponse> AddWorkItemAsync(
        Guid sprintId,
        AddSprintWorkItemRequest request,
        Guid addedBy,
        CancellationToken ct = default)
    {
        var sprint = await GetOrThrowAsync(sprintId, ct);

        if (sprint.Status == SprintStatus.Completed)
            throw new BusinessRuleException("Cannot add work items to a completed sprint.");

        var workItem = await _workItems.GetActiveAsync(request.WorkItemId, ct)
            ?? throw new NotFoundException("WorkItem", request.WorkItemId);

        // The caller was authorized against the *sprint's team*; nothing so far has authorized the
        // *work item*. Without this check any team member could name any work item id in the system
        // — including one in another organization — and read its title, assignee and points back
        // off their own board.
        //
        // The boundary is now the team's projects rather than one project: a sprint may hold work
        // from anything the team serves, and nothing else. See docs/adr-001-team-sprints.md.
        //
        // Reported as not-found rather than forbidden on purpose. The caller cannot see this work
        // item, and answering "forbidden" would confirm the id names something real.
        if (!await _repository.TeamServesProjectAsync(sprint.TeamId, workItem.ProjectId, ct))
            throw new NotFoundException("WorkItem", request.WorkItemId);

        if (await _repository.BacklogContainsAsync(sprintId, request.WorkItemId, ct))
            throw new ConflictException("Work item is already in this sprint.");

        var position = request.Position ?? await _repository.GetNextPositionAsync(sprintId, ct);
        var maxRank  = await _repository.GetMaxRankAsync(sprintId, ct);

        var entry = new SprintWorkItem
        {
            SprintId   = sprintId,
            WorkItemId = request.WorkItemId,
            Position   = position,
            Rank       = Ranking.Between(maxRank, null),
            CreatedBy  = addedBy
        };

        _repository.AddBacklogEntry(entry);

        await EnqueueAsync(sprint, orgId => new SprintWorkItemAdded(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint),
            workItem.Id, workItem.Title, addedBy), ct);

        await _repository.SaveChangesAsync(ct);

        /*
         * The backlog is "everything with no sprint", and this is one of two doors into a sprint.
         * The other — the backlog's own move-to-sprint — already pointed the entry here; this one
         * did not, so an item committed from a board stayed listed as unscheduled and could be
         * planned into a second sprint by anybody reading the backlog.
         */
        await _backlog.AssignSprintAsync(sprint.Id, [workItem.Id], ct);

        // Read directly rather than through the project service: this module already holds the
        // context, and the key is one column on a row it has the id for.
        var projectKey = await _context.Projects
            .Where(p => p.Id == workItem.ProjectId)
            .Select(p => p.Key)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        return new SprintWorkItemResponse(
            workItem.Id, $"{projectKey}-{workItem.Number}", workItem.Title, workItem.Type,
            workItem.State, workItem.Priority,
            workItem.AssigneeId, workItem.StoryPoints, position, entry.Rank);
    }

    public async Task RemoveWorkItemAsync(
        Guid sprintId,
        Guid workItemId,
        Guid removedBy,
        CancellationToken ct = default)
    {
        var entry = await _repository.GetBacklogEntryAsync(sprintId, workItemId, ct)
            ?? throw new NotFoundException("SprintWorkItem", workItemId);

        var sprint = await GetOrThrowAsync(sprintId, ct);
        var title  = (await _workItems.GetActiveAsync(workItemId, ct))?.Title ?? string.Empty;

        _repository.RemoveBacklogEntry(entry);

        await EnqueueAsync(sprint, orgId => new SprintWorkItemRemoved(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint),
            workItemId, title, removedBy), ct);

        await _repository.SaveChangesAsync(ct);

        // And back out again — taking work out of a sprint returns it to the unscheduled backlog,
        // at the rank it left, because the entry was kept rather than deleted.
        await _backlog.ClearSprintAsync(sprintId, [workItemId], ct);
    }

    public async Task<PagedResult<SprintWorkItemResponse>> GetWorkItemsAsync(
        Guid sprintId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        _ = await GetOrThrowAsync(sprintId, ct);

        var (items, total) = await _repository.GetWorkItemsAsync(
            sprintId, pagination.Skip, pagination.PageSize, ct);

        return new PagedResult<SprintWorkItemResponse>(items, total, pagination.Page, pagination.PageSize);
    }

    public async Task<decimal> MoveWorkItemAsync(
        Guid sprintId,
        Guid workItemId,
        MoveSprintWorkItemRequest request,
        CancellationToken ct = default)
    {
        _ = await GetOrThrowAsync(sprintId, ct);

        var entry = await _repository.GetBacklogEntryAsync(sprintId, workItemId, ct)
            ?? throw new NotFoundException("SprintWorkItem", workItemId);

        (decimal? before, decimal? after) = await _repository.GetNeighbourRanksAsync(
            sprintId, request.AfterWorkItemId, request.BeforeWorkItemId, ct);

        if (Ranking.NeedsRebalance(before, after))
        {
            await RebalanceAsync(sprintId, ct);

            (before, after) = await _repository.GetNeighbourRanksAsync(
                sprintId, request.AfterWorkItemId, request.BeforeWorkItemId, ct);
        }

        entry.Rank      = Ranking.Between(before, after);
        entry.UpdatedAt = DateTime.UtcNow;

        await _repository.SaveChangesAsync(ct);
        return entry.Rank;
    }

    private async Task RebalanceAsync(Guid sprintId, CancellationToken ct)
    {
        var entries = await _repository.GetBacklogEntriesAsync(sprintId, ct);
        var ordered = entries.OrderBy(e => e.Rank).ToList();

        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Rank = Ranking.RankAt(i);

        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Rebalanced {Count} backlog ranks for sprint {SprintId}",
            ordered.Count, sprintId);
    }

    public async Task ReorderWorkItemsAsync(
        Guid sprintId,
        ReorderSprintWorkItemsRequest request,
        CancellationToken ct = default)
    {
        _ = await GetOrThrowAsync(sprintId, ct);

        var entries = await _repository.GetBacklogEntriesAsync(sprintId, ct);

        for (int i = 0; i < request.WorkItemIds.Count; i++)
        {
            var entry = entries.FirstOrDefault(sw => sw.WorkItemId == request.WorkItemIds[i]);
            if (entry is not null)
            {
                entry.Position = i;
                entry.Rank     = Ranking.RankAt(i);
            }
        }

        await _repository.SaveChangesAsync(ct);
    }

    // ── Sprint close-out ──────────────────────────────────────────────────────

    public async Task<CloseSprintResponse> CloseAsync(
        Guid sprintId,
        CloseSprintRequest request,
        Guid closedBy,
        CancellationToken ct = default)
    {
        var sprint = await GetOrThrowAsync(sprintId, ct);

        if (sprint.Status != SprintStatus.Active)
            throw new BusinessRuleException("Only Active sprints can be closed.");

        if (request.IncompleteItemsDestination == IncompleteItemsDestination.MoveToNextSprint
            && !request.NextSprintId.HasValue)
            throw new BusinessRuleException(
                "NextSprintId is required when IncompleteItemsDestination is MoveToNextSprint.");

        // ── Fetch data via _context (now properly injected) ───────────────
        var projectId = await _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId)
            .Join(_context.WorkItems,
                sw => sw.WorkItemId,
                w  => w.Id,
                (sw, w) => w.ProjectId)
            .FirstOrDefaultAsync(ct);

        var allSprintWorkItemIds = await _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId)
            .Select(sw => sw.WorkItemId)
            .ToListAsync(ct);

        var completedStates = new[] { WorkItemState.Resolved, WorkItemState.Closed };

        var completedIds = await _context.WorkItems
            .Where(w => allSprintWorkItemIds.Contains(w.Id)
                     && completedStates.Contains(w.State))
            .Select(w => w.Id)
            .ToListAsync(ct);

        var incompleteIds = allSprintWorkItemIds.Except(completedIds).ToList();

        // ── Route incomplete items ────────────────────────────────────────
        if (incompleteIds.Count > 0 && projectId != Guid.Empty)
        {
            if (request.IncompleteItemsDestination == IncompleteItemsDestination.ReturnToBacklog)
            {
                /*
                 * The backlog entry is released; the SprintWorkItem row is deliberately kept.
                 *
                 * The comment here used to claim the sprint-side rows were dropped "below". They
                 * are not, and they must not be: CommittedItems and CommittedPoints are counted
                 * from sprint membership, so dropping the rows would rewrite the sprint's own
                 * record. A sprint that committed to eight items and finished five would report
                 * five committed and five completed — a hundred percent, every time, for every
                 * team — and velocity would stop describing anything.
                 *
                 * Returning an item mid-sprint through the backlog module is a different act and
                 * does remove the row: taking work out of a running sprint is a change of scope,
                 * and the sprint should not be credited with having carried it.
                 */
                await _backlog.ClearSprintAsync(sprintId, incompleteIds, ct);
            }
            else
            {
                var nextSprint = await _context.Sprints
                    .FirstOrDefaultAsync(s => s.Id == request.NextSprintId!.Value
                                           && s.Status != SprintStatus.Completed, ct)
                    ?? throw new NotFoundException("Next sprint", request.NextSprintId!.Value);

                var nextPosition = (await _context.SprintWorkItems
                    .Where(sw => sw.SprintId == nextSprint.Id)
                    .MaxAsync(sw => (int?)sw.Position, ct) ?? -1) + 1;

                foreach (var workItemId in incompleteIds)
                {
                    var backlogEntry = await _context.BacklogItems
                        .FirstOrDefaultAsync(b => b.ProjectId == projectId
                                               && b.WorkItemId == workItemId, ct);

                    if (backlogEntry is not null)
                        backlogEntry.SprintId = nextSprint.Id;

                    var alreadyThere = await _context.SprintWorkItems
                        .AnyAsync(sw => sw.SprintId == nextSprint.Id
                                     && sw.WorkItemId == workItemId, ct);

                    if (!alreadyThere)
                    {
                        _context.SprintWorkItems.Add(new SprintWorkItem
                        {
                            SprintId   = nextSprint.Id,
                            WorkItemId = workItemId,
                            Position   = nextPosition++,
                            CreatedBy  = closedBy
                        });
                    }
                }

                await _context.SaveChangesAsync(ct);
            }
        }

        // ── Mark sprint completed ─────────────────────────────────────────
        sprint.Status    = SprintStatus.Completed;
        sprint.UpdatedAt = DateTime.UtcNow;

        await EnqueueAsync(sprint, orgId => new SprintStatusChanged(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint),
            SprintStatus.Active, SprintStatus.Completed, closedBy), ct);

        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Sprint {SprintId} closed by {UserId}. Completed: {C}, Incomplete: {I}",
            sprintId, closedBy, completedIds.Count, incompleteIds.Count);

        return new CloseSprintResponse(
            await BuildResponseAsync(sprintId, ct),
            completedIds.Count,
            incompleteIds.Count,
            request.IncompleteItemsDestination,
            request.NextSprintId);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<Sprint> GetOrThrowAsync(Guid sprintId, CancellationToken ct)
        => await _repository.GetByIdAsync(sprintId, ct)
           ?? throw new NotFoundException("Sprint", sprintId);

    private async Task EnqueueAsync<TEvent>(
        Sprint sprint,
        Func<Guid, TEvent> build,
        CancellationToken ct) where TEvent : IDomainEvent
    {
        var orgId = await _repository.GetOrganizationIdForTeamAsync(sprint.TeamId, ct);
        if (orgId is null) return;
        _eventBus.Enqueue(build(orgId.Value));
    }

    private static string SprintName(Sprint sprint) => $"Sprint {sprint.Number}";

    /// <remarks>
    /// Still lists <c>Active → Completed</c> as a legal move of the state machine, because it is —
    /// <see cref="CloseAsync"/> makes exactly that transition. What <see cref="UpdateStatusAsync"/>
    /// refuses is reaching it through a bare status change.
    /// </remarks>
    private static void ValidateTransition(SprintStatus current, SprintStatus next)
    {
        var valid = (current, next) switch
        {
            (SprintStatus.Planning, SprintStatus.Active)    => true,
            (SprintStatus.Active,   SprintStatus.Completed) => true,
            _ => false
        };

        if (!valid)
            throw new BusinessRuleException(
                $"Cannot transition sprint from '{current}' to '{next}'. " +
                "Allowed: Planning → Active → Completed.");
    }

    private async Task<SprintResponse> BuildResponseAsync(Guid sprintId, CancellationToken ct)
    {
        var sprint   = await _repository.GetByIdAsync(sprintId, ct)
            ?? throw new NotFoundException("Sprint", sprintId);
        var progress = await _repository.GetProgressAsync(sprintId, ct);

        return new SprintResponse(
            sprint.Id, sprint.TeamId, sprint.Number, sprint.Goal,
            sprint.StartDate, sprint.EndDate, sprint.Status,
            progress.TotalItems, progress.CompletedItems,
            progress.TotalPoints, progress.CompletedPoints,
            sprint.CreatedAt);
    }
}