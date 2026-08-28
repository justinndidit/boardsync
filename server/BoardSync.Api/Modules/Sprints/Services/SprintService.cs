
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
using BoardSync.Api.Modules.WorkItems.Services;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
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
    private readonly IWorkItemService _workItemService;
    private readonly IRbacService _rbac;

    // ── Constructor — fixed: removed duplicate IEventBus, added _context ──
    public SprintService(
        BoardSyncDbContext context,
        ISprintRepository repository,
        IWorkItemRepository workItems,
        IWorkItemService workItemService,
        IRbacService rbac,
        IEventBus eventBus,
        IBacklogSprintLink backlog,
        ILogger<SprintService> logger)
    {
        _context        = context;        // ← now properly assigned
        _repository     = repository;
        _workItems      = workItems;
        _workItemService = workItemService;
        _rbac = rbac;
        _eventBus       = eventBus;
        _backlog = backlog;
        _logger         = logger;
    }

    private static void ValidateDates(DateTime startDate, DateTime endDate)
{
    if (startDate.Date < DateTime.UtcNow.Date)
        throw new BusinessRuleException("Sprint start date cannot be in the past.");

    if (endDate <= startDate)
        throw new BusinessRuleException("End date must be after start date.");
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
            StartDate = DateTime.SpecifyKind(request.StartDate.Date, DateTimeKind.Utc),
            EndDate   = DateTime.SpecifyKind(request.EndDate.Date, DateTimeKind.Utc),
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

        if (sprint.Status != SprintStatus.Planning)
           throw new BusinessRuleException("Only Planning sprints can be updated.");

         ValidateDates(request.StartDate, request.EndDate);
        if (request.EndDate <= request.StartDate)
            throw new BusinessRuleException("End date must be after start date.");

        var changes      = new List<(string Field, string? Old, string? New)>();
        var newGoal      = request.Goal?.Trim();
        var newStartDate = DateTime.SpecifyKind(request.StartDate.Date, DateTimeKind.Utc);
        var newEndDate   = DateTime.SpecifyKind(request.EndDate.Date, DateTimeKind.Utc);

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

        // Read directly rather than through the project service: this module already holds the
        // context, and the key is one column on a row it has the id for.
        var projectKey = await _context.Projects
            .Where(p => p.Id == workItem.ProjectId)
            .Select(p => p.Key)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        return new SprintWorkItemResponse(
            workItem.Id, $"{projectKey}-{workItem.Number}", workItem.Title, workItem.Type,
            workItem.State, workItem.Priority,
            workItem.AssigneeId, workItem.StoryPoints, position);
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

    public async Task<MoveWorkItemCommandResponse> MoveWorkItemWithStateAsync(
        Guid sprintId,
        Guid workItemId,
        MoveWorkItemCommandRequest request,
        Guid changedBy,
        CancellationToken ct = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            () => MoveWorkItemWithStateInTransactionAsync(sprintId, workItemId, request, changedBy, ct));
    }

    private async Task<MoveWorkItemCommandResponse> MoveWorkItemWithStateInTransactionAsync(
        Guid sprintId,
        Guid workItemId,
        MoveWorkItemCommandRequest request,
        Guid changedBy,
        CancellationToken ct)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);
        await _repository.LockSprintAsync(sprintId, ct);
        var sprint = await GetOrThrowAsync(sprintId, ct);
        if (!await _rbac.HasPermissionAsync(
            changedBy, Permissions.SprintOrder, RoleScope.Project, sprint.TeamId, ct))
            throw new ForbiddenException("You do not have permission to reorder this sprint.");

        var entry = await _repository.GetBacklogEntryAsync(sprintId, workItemId, ct)
            ?? throw new NotFoundException("SprintWorkItem", workItemId);

        if (request.AfterWorkItemId is null && request.BeforeWorkItemId is null &&
            (await _repository.GetBacklogEntriesAsync(sprintId, ct)).Any(item => item.WorkItemId != workItemId))
            throw new BusinessRuleException(
                "Both neighbours may be null only when the sprint contains no other work items.");

        ValidateNeighbourIds(workItemId, request.AfterWorkItemId, request.BeforeWorkItemId);

        var neighbourIds = new[] { request.AfterWorkItemId, request.BeforeWorkItemId }
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        var neighbours = await _repository.GetBacklogEntriesByIdsAsync(sprintId, neighbourIds, ct);
        if (neighbours.Count != neighbourIds.Length)
            throw new NotFoundException("Sprint neighbour", neighbourIds.First(id => neighbours.All(n => n.WorkItemId != id)));

        foreach (var neighbourId in neighbourIds)
        {
            var neighbour = await _workItems.GetActiveAsync(neighbourId, ct)
                ?? throw new NotFoundException("WorkItem", neighbourId);
            if (neighbour.ProjectId != sprint.TeamId)
                throw new NotFoundException("WorkItem", neighbourId);
        }

        var (before, after) = await _repository.GetNeighbourRanksAsync(
            sprintId, request.AfterWorkItemId, request.BeforeWorkItemId, ct);
        if (before.HasValue && after.HasValue && after.Value <= before.Value)
            throw new BusinessRuleException("The requested neighbours do not define a valid insertion point.");

        var originalRanks = new Dictionary<Guid, decimal>();
        if (Ranking.NeedsRebalance(before, after))
        {
            foreach (var item in await _repository.GetBacklogEntriesAsync(sprintId, ct))
                originalRanks[item.WorkItemId] = item.Rank;

            await RebalanceAsync(sprintId, ct);
            (before, after) = await _repository.GetNeighbourRanksAsync(
                sprintId, request.AfterWorkItemId, request.BeforeWorkItemId, ct);
        }

        var staged = await _workItemService.StageStateTransitionAsync(
            workItemId, request.State, changedBy, request.ExpectedVersion, ct, allowSameState: true);

        entry.Rank = Ranking.Between(before, after);
        while (await _repository.RankExistsAsync(sprintId, entry.Rank, workItemId, ct))
            entry.Rank = after.HasValue
                ? Ranking.Between(entry.Rank, after)
                : entry.Rank + Ranking.Step;
        entry.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _repository.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(
                "This work item was changed by someone else after you loaded it. Re-read it and apply your movement to the current version.");
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is Npgsql.PostgresException postgresException &&
            postgresException.SqlState == "23505")
        {
            throw new ConflictException(
                "Another movement claimed the requested rank. Re-read the sprint ordering and retry your movement.");
        }

        var rankChanges = originalRanks.Count == 0
            ? [new WorkItemRankChange(workItemId, entry.Rank)]
            : (await _repository.GetBacklogEntriesAsync(sprintId, ct))
                .Where(item => !originalRanks.TryGetValue(item.WorkItemId, out var oldRank) || oldRank != item.Rank)
                .Select(item => new WorkItemRankChange(item.WorkItemId, item.Rank)).ToList();

        _eventBus.Enqueue(new WorkItemMoved(
            workItemId, sprint.TeamId, sprintId, staged.Item.State, entry.Rank,
            staged.Item.Version, changedBy, rankChanges));
        await _repository.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);

        return new MoveWorkItemCommandResponse(workItemId, staged.Item.State, entry.Rank, staged.Item.Version);
    }

    private async Task RebalanceAsync(Guid sprintId, CancellationToken ct)
    {
        var entries = await _repository.GetBacklogEntriesAsync(sprintId, ct);
        var ordered = entries.OrderBy(e => e.Rank).ToList();

        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Rank = -(i + 1);

        await _repository.SaveChangesAsync(ct);

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
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(ct);
            await _repository.ReorderRanksAsync(sprintId, request.WorkItemIds, ct);
            await transaction.CommitAsync(ct);
        });
    }

    private static void ValidateNeighbourIds(Guid workItemId, Guid? afterId, Guid? beforeId)
    {
        if (afterId == workItemId || beforeId == workItemId)
            throw new BusinessRuleException("The moved work item cannot be used as its own neighbour.");
        if (afterId.HasValue && beforeId.HasValue && afterId == beforeId)
            throw new BusinessRuleException("The before and after neighbours must be different.");
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
                // Only the backlog entries this sprint held; an item that also sits in another
                // sprint keeps that membership. The sprint-side rows are dropped below.
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