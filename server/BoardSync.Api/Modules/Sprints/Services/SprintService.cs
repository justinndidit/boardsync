using BoardSync.Api.Modules.Sprints.Domain;
using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Events;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.Sprints.Repositories.Interfaces;
using BoardSync.Api.Modules.WorkItems.Repository;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;

namespace BoardSync.Api.Modules.Sprints.Services;

public class SprintService : ISprintService
{
    private readonly ISprintRepository _repository;
    private readonly IWorkItemRepository _workItems;
    private readonly IEventBus _eventBus;
    private readonly ILogger<SprintService> _logger;

    public SprintService(
        ISprintRepository repository,
        IWorkItemRepository workItems,
        IEventBus eventBus,
        ILogger<SprintService> logger)
    {
        _repository = repository;
        _workItems = workItems;
        _eventBus = eventBus;
        _logger = logger;
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

        if (request.EndDate <= request.StartDate)
            throw new BusinessRuleException("End date must be after start date.");

        if (await _repository.HasOverlappingSprintAsync(teamId, request.StartDate, request.EndDate, ct))
            throw new ConflictException("Sprint dates overlap with an existing sprint for this team.");

        var sprint = new Sprint
        {
            TeamId = teamId,
            Number = await _repository.GetNextNumberAsync(teamId, ct),
            Goal = request.Goal?.Trim(),
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Status = SprintStatus.Planning,
            CreatedBy = createdBy
        };

        _repository.Add(sprint);

        await EnqueueAsync(sprint, orgId => new SprintCreated(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint), createdBy), ct);

        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Sprint {Number} created for team {TeamId} by {UserId}",
            sprint.Number, teamId, createdBy);

        return await BuildResponseAsync(sprint.Id, ct);
    }

    public async Task<SprintResponse> GetByIdAsync(Guid sprintId, CancellationToken ct = default)
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

    public async Task<SprintResponse?> GetActiveForTeamAsync(Guid teamId, CancellationToken ct = default)
    {
        var sprint = await _repository.GetActiveForTeamAsync(teamId, ct);
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

        if (request.EndDate <= request.StartDate)
            throw new BusinessRuleException("End date must be after start date.");

        var changes = new List<(string Field, string? Old, string? New)>();
        var newGoal = request.Goal?.Trim();

        if (sprint.Goal != newGoal)
            changes.Add(("Goal", sprint.Goal, newGoal));
        if (sprint.StartDate != request.StartDate)
            changes.Add(("StartDate", sprint.StartDate.ToString("u"), request.StartDate.ToString("u")));
        if (sprint.EndDate != request.EndDate)
            changes.Add(("EndDate", sprint.EndDate.ToString("u"), request.EndDate.ToString("u")));

        sprint.Goal = newGoal;
        sprint.StartDate = request.StartDate;
        sprint.EndDate = request.EndDate;
        sprint.UpdatedAt = DateTime.UtcNow;

        foreach (var (field, oldValue, newValue) in changes)
        {
            await EnqueueAsync(sprint, orgId => new SprintUpdated(
                sprint.Id, sprint.TeamId, orgId, SprintName(sprint), field, oldValue, newValue, updatedBy), ct);
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

        var oldStatus = sprint.Status;
        sprint.Status = newStatus;
        sprint.UpdatedAt = DateTime.UtcNow;

        await EnqueueAsync(sprint, orgId => new SprintStatusChanged(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint), oldStatus, newStatus, updatedBy), ct);

        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Sprint {SprintId} → {Status} by {UserId}", sprintId, newStatus, updatedBy);

        return await BuildResponseAsync(sprintId, ct);
    }

    public async Task DeleteAsync(Guid sprintId, Guid deletedBy, CancellationToken ct = default)
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

        if (await _repository.BacklogContainsAsync(sprintId, request.WorkItemId, ct))
            throw new ConflictException("Work item is already in this sprint.");

        var position = request.Position ?? await _repository.GetNextPositionAsync(sprintId, ct);

        // Appended to the end by rank regardless of the legacy Position, which is only still
        // written so existing readers keep working.
        var maxRank = await _repository.GetMaxRankAsync(sprintId, ct);

        var entry = new SprintWorkItem
        {
            SprintId = sprintId,
            WorkItemId = request.WorkItemId,
            Position = position,
            Rank = Ranking.Between(maxRank, null),
            CreatedBy = addedBy
        };

        _repository.AddBacklogEntry(entry);

        await EnqueueAsync(sprint, orgId => new SprintWorkItemAdded(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint),
            workItem.Id, workItem.Title, addedBy), ct);

        await _repository.SaveChangesAsync(ct);

        return new SprintWorkItemResponse(
            workItem.Id, workItem.Title, workItem.Type,
            workItem.State, workItem.Priority,
            workItem.AssigneeId, workItem.StoryPoints, position);
    }

    public async Task RemoveWorkItemAsync(Guid sprintId, Guid workItemId, Guid removedBy, CancellationToken ct = default)
    {
        var entry = await _repository.GetBacklogEntryAsync(sprintId, workItemId, ct)
            ?? throw new NotFoundException("SprintWorkItem", workItemId);

        var sprint = await GetOrThrowAsync(sprintId, ct);
        var title = (await _workItems.GetActiveAsync(workItemId, ct))?.Title ?? string.Empty;

        _repository.RemoveBacklogEntry(entry);

        await EnqueueAsync(sprint, orgId => new SprintWorkItemRemoved(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint), workItemId, title, removedBy), ct);

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

        var (before, after) = await _repository.GetNeighbourRanksAsync(
            sprintId, request.AfterWorkItemId, request.BeforeWorkItemId, ct);

        if (Ranking.NeedsRebalance(before, after))
        {
            // The gap between these two has collapsed to where further midpoints would start
            // losing precision. Renumbering the backlog restores room; it is the one operation
            // that touches every row, and it is rare by construction.
            await RebalanceAsync(sprintId, ct);

            (before, after) = await _repository.GetNeighbourRanksAsync(
                sprintId, request.AfterWorkItemId, request.BeforeWorkItemId, ct);
        }

        entry.Rank = Ranking.Between(before, after);
        entry.UpdatedAt = DateTime.UtcNow;

        await _repository.SaveChangesAsync(ct);

        return entry.Rank;
    }

    /// <summary>
    /// Spreads the backlog back out over evenly spaced ranks, preserving current order.
    /// </summary>
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

        // Writes ranks as well as positions, so the two orderings cannot drift apart. This whole-
        // list form is still last-writer-wins across the entire backlog — see MoveWorkItemAsync for
        // the single-row alternative that concurrent editors should use.
        for (int i = 0; i < request.WorkItemIds.Count; i++)
        {
            var entry = entries.FirstOrDefault(sw => sw.WorkItemId == request.WorkItemIds[i]);
            if (entry is not null)
            {
                entry.Position = i;
                entry.Rank = Ranking.RankAt(i);
            }
        }

        await _repository.SaveChangesAsync(ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<Sprint> GetOrThrowAsync(Guid sprintId, CancellationToken ct)
        => await _repository.GetByIdAsync(sprintId, ct)
           ?? throw new NotFoundException("Sprint", sprintId);

    /// <summary>
    /// Stages a sprint event. Still async because sprints hang off a team while activity is filed
    /// by organization, so the team's owning organization has to be looked up first. If the team
    /// has vanished there is nothing to file the event under and it is skipped rather than queued
    /// half-formed.
    /// </summary>
    /// <remarks>
    /// Call this <b>before</b> saving — it stages the outbox row on the same unit of work, which is
    /// what makes the event and the sprint change commit together.
    /// </remarks>
    private async Task EnqueueAsync<TEvent>(
        Sprint sprint,
        Func<Guid, TEvent> build,
        CancellationToken ct) where TEvent : IDomainEvent
    {
        var orgId = await _repository.GetOrganizationIdForTeamAsync(sprint.TeamId, ct);

        if (orgId is null) return;

        _eventBus.Enqueue(build(orgId.Value));
    }

    /// <summary>Display name for a sprint — they are numbered per team, not named.</summary>
    private static string SprintName(Sprint sprint) => $"Sprint {sprint.Number}";

    private static void ValidateTransition(SprintStatus current, SprintStatus next)
    {
        var valid = (current, next) switch
        {
            (SprintStatus.Planning,  SprintStatus.Active)    => true,
            (SprintStatus.Active,    SprintStatus.Completed) => true,
            _ => false
        };

        if (!valid)
            throw new BusinessRuleException(
                $"Cannot transition sprint from '{current}' to '{next}'. " +
                "Allowed: Planning → Active → Completed.");
    }

    private async Task<SprintResponse> BuildResponseAsync(Guid sprintId, CancellationToken ct)
    {
        var sprint = await _repository.GetByIdAsync(sprintId, ct)
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
