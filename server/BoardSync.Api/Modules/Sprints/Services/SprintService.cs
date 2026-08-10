using BoardSync.Api.Data;
using BoardSync.Api.Modules.Backlog.Services;
using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Events;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Sprints.Services;

public class SprintService : ISprintService
{
    private readonly BoardSyncDbContext _context;
    private readonly IEventBus _eventBus;
    private readonly IBacklogService _backlogService;
    private readonly ILogger<SprintService> _logger;

    public SprintService(
        BoardSyncDbContext context, IEventBus eventBus,
        IBacklogService backlogService,
        ILogger<SprintService> logger)
    {
        _context = context;
        _eventBus = eventBus;
        _backlogService = backlogService;
        _logger = logger;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<SprintResponse> CreateAsync(
        Guid teamId,
        CreateSprintRequest request,
        Guid createdBy,
        CancellationToken ct = default)
    {
        if (!await _context.Teams.AnyAsync(t => t.Id == teamId && t.IsActive, ct))
            throw new NotFoundException("Team", teamId);

        if (request.EndDate <= request.StartDate)
            throw new BusinessRuleException("End date must be after start date.");

        // Prevent overlapping active/planning sprints for the same team
        var overlaps = await _context.Sprints.AnyAsync(s =>
            s.TeamId == teamId
            && s.Status != SprintStatus.Completed
            && s.StartDate < request.EndDate
            && s.EndDate > request.StartDate, ct);

        if (overlaps)
            throw new ConflictException("Sprint dates overlap with an existing sprint for this team.");

        // Auto-increment sprint number within the team
        var nextNumber = (await _context.Sprints
            .Where(s => s.TeamId == teamId)
            .MaxAsync(s => (int?)s.Number, ct) ?? 0) + 1;

        var sprint = new Sprint
        {
            TeamId = teamId,
            Number = nextNumber,
            Goal = request.Goal?.Trim(),
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Status = SprintStatus.Planning,
            CreatedBy = createdBy
        };

        _context.Sprints.Add(sprint);
        await _context.SaveChangesAsync(ct);

        await PublishAsync(sprint, orgId => new SprintCreated(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint), createdBy), ct);

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
        var query = _context.Sprints
            .Where(s => s.TeamId == teamId)
            .OrderByDescending(s => s.Number);

        var total = await query.CountAsync(ct);
        var sprints = await query
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .ToListAsync(ct);

        // Batch-load item counts in one query
        var ids = sprints.Select(s => s.Id).ToList();
        var countMap = await _context.SprintWorkItems
            .Where(sw => ids.Contains(sw.SprintId))
            .GroupBy(sw => sw.SprintId)
            .Select(g => new { SprintId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SprintId, x => x.Count, ct);

        var items = sprints.Select(s => new SprintSummaryResponse(
            s.Id, s.Number, s.Goal,
            s.StartDate, s.EndDate, s.Status,
            countMap.GetValueOrDefault(s.Id, 0)
        )).ToList();

        return new PagedResult<SprintSummaryResponse>(items, total, pagination.Page, pagination.PageSize);
    }

    public async Task<SprintResponse?> GetActiveForTeamAsync(Guid teamId, CancellationToken ct = default)
    {
        var sprint = await _context.Sprints
            .FirstOrDefaultAsync(s => s.TeamId == teamId && s.Status == SprintStatus.Active, ct);

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

        await _context.SaveChangesAsync(ct);

        foreach (var (field, oldValue, newValue) in changes)
        {
            await PublishAsync(sprint, orgId => new SprintUpdated(
                sprint.Id, sprint.TeamId, orgId, SprintName(sprint), field, oldValue, newValue, updatedBy), ct);
        }

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

        if (newStatus == SprintStatus.Active)
        {
            var anotherActive = await _context.Sprints.AnyAsync(s =>
                s.TeamId == sprint.TeamId
                && s.Status == SprintStatus.Active
                && s.Id != sprintId, ct);

            if (anotherActive)
                throw new ConflictException(
                    "Another sprint is already active for this team. Complete it before starting a new one.");
        }

        var oldStatus = sprint.Status;
        sprint.Status = newStatus;
        sprint.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        await PublishAsync(sprint, orgId => new SprintStatusChanged(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint), oldStatus, newStatus, updatedBy), ct);

        _logger.LogInformation("Sprint {SprintId} → {Status} by {UserId}", sprintId, newStatus, updatedBy);

        return await BuildResponseAsync(sprintId, ct);
    }

    public async Task DeleteAsync(Guid sprintId, Guid deletedBy, CancellationToken ct = default)
    {
        var sprint = await GetOrThrowAsync(sprintId, ct);

        if (sprint.Status != SprintStatus.Planning)
            throw new BusinessRuleException("Only Planning sprints can be deleted.");

        if (await _context.SprintWorkItems.AnyAsync(sw => sw.SprintId == sprintId, ct))
            throw new BusinessRuleException("Remove all work items from the sprint before deleting it.");

        _context.Sprints.Remove(sprint);
        await _context.SaveChangesAsync(ct);

        await PublishAsync(sprint, orgId => new SprintDeleted(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint), deletedBy), ct);

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

        var workItem = await _context.WorkItems
            .FirstOrDefaultAsync(w => w.Id == request.WorkItemId && w.IsActive, ct)
            ?? throw new NotFoundException("WorkItem", request.WorkItemId);

        if (await _context.SprintWorkItems.AnyAsync(
                sw => sw.SprintId == sprintId && sw.WorkItemId == request.WorkItemId, ct))
            throw new ConflictException("Work item is already in this sprint.");

        // Resolve position — append at end if not specified
        int position = request.Position ?? (
            await _context.SprintWorkItems
                .Where(sw => sw.SprintId == sprintId)
                .MaxAsync(sw => (int?)sw.Position, ct) ?? -1) + 1;

        var entry = new SprintWorkItem
        {
            SprintId = sprintId,
            WorkItemId = request.WorkItemId,
            Position = position,
            CreatedBy = addedBy
        };

        _context.SprintWorkItems.Add(entry);
        await _context.SaveChangesAsync(ct);

        await PublishAsync(sprint, orgId => new SprintWorkItemAdded(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint),
            workItem.Id, workItem.Title, addedBy), ct);

        return new SprintWorkItemResponse(
            workItem.Id, workItem.Title, workItem.Type,
            workItem.State, workItem.Priority,
            workItem.AssigneeId, workItem.StoryPoints, position);
    }

    public async Task RemoveWorkItemAsync(Guid sprintId, Guid workItemId, Guid removedBy, CancellationToken ct = default)
    {
        var entry = await _context.SprintWorkItems
            .FirstOrDefaultAsync(sw => sw.SprintId == sprintId && sw.WorkItemId == workItemId, ct)
            ?? throw new NotFoundException("SprintWorkItem", workItemId);

        var sprint = await GetOrThrowAsync(sprintId, ct);
        var title = await _context.WorkItems
            .Where(w => w.Id == workItemId)
            .Select(w => w.Title)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        _context.SprintWorkItems.Remove(entry);
        await _context.SaveChangesAsync(ct);

        await PublishAsync(sprint, orgId => new SprintWorkItemRemoved(
            sprint.Id, sprint.TeamId, orgId, SprintName(sprint), workItemId, title, removedBy), ct);
    }

    public async Task<PagedResult<SprintWorkItemResponse>> GetWorkItemsAsync(
        Guid sprintId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        _ = await GetOrThrowAsync(sprintId, ct);

        var query = _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId)
            .OrderBy(sw => sw.Position);

        var total = await query.CountAsync(ct);

        var items = await query
            .Skip(pagination.Skip)
            .Take(pagination.PageSize)
            .Join(_context.WorkItems,
                sw => sw.WorkItemId,
                w => w.Id,
                (sw, w) => new SprintWorkItemResponse(
                    w.Id, w.Title, w.Type, w.State,
                    w.Priority, w.AssigneeId, w.StoryPoints, sw.Position))
            .ToListAsync(ct);

        return new PagedResult<SprintWorkItemResponse>(items, total, pagination.Page, pagination.PageSize);
    }

    public async Task ReorderWorkItemsAsync(
        Guid sprintId,
        ReorderSprintWorkItemsRequest request,
        CancellationToken ct = default)
    {
        _ = await GetOrThrowAsync(sprintId, ct);

        var entries = await _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId)
            .ToListAsync(ct);

        for (int i = 0; i < request.WorkItemIds.Count; i++)
        {
            var entry = entries.FirstOrDefault(sw => sw.WorkItemId == request.WorkItemIds[i]);
            if (entry is not null)
                entry.Position = i;
        }

        await _context.SaveChangesAsync(ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<Sprint> GetOrThrowAsync(Guid sprintId, CancellationToken ct)
        => await _context.Sprints.FirstOrDefaultAsync(s => s.Id == sprintId, ct)
           ?? throw new NotFoundException("Sprint", sprintId);

    /// <summary>
    /// Sprints hang off a team, but activity is filed by organization, so every sprint event needs
    /// the team's owning organization looked up first. If the team has vanished there is nothing to
    /// file the event under and it is skipped rather than published half-formed.
    /// </summary>
    private async Task PublishAsync<TEvent>(
        Sprint sprint,
        Func<Guid, TEvent> build,
        CancellationToken ct) where TEvent : IDomainEvent
    {
        var orgId = await _context.Teams
            .Where(t => t.Id == sprint.TeamId)
            .Select(t => (Guid?)t.OrganizationId)
            .FirstOrDefaultAsync(ct);

        if (orgId is null) return;

        await _eventBus.PublishAsync(build(orgId.Value), ct);
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

        // Find the project that owns these work items (via the first work item in the sprint)
        var projectId = await _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId)
            .Join(_context.WorkItems, sw => sw.WorkItemId, w => w.Id, (sw, w) => w.ProjectId)
            .FirstOrDefaultAsync(ct);

        // Separate completed vs incomplete items
        var allSprintWorkItemIds = await _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId)
            .Select(sw => sw.WorkItemId)
            .ToListAsync(ct);

        var completedStates = new[] { WorkItemState.Resolved, WorkItemState.Closed };

        var completedIds = await _context.WorkItems
            .Where(w => allSprintWorkItemIds.Contains(w.Id) && completedStates.Contains(w.State))
            .Select(w => w.Id)
            .ToListAsync(ct);

        var incompleteIds = allSprintWorkItemIds.Except(completedIds).ToList();

        // Route incomplete items
        if (incompleteIds.Count > 0 && projectId != Guid.Empty)
        {
            if (request.IncompleteItemsDestination == IncompleteItemsDestination.ReturnToBacklog)
            {
                await _backlogService.ReturnToBacklogAsync(projectId,
                    new Backlog.DTOs.ReturnToBacklogRequest { WorkItemIds = incompleteIds }, ct);
            }
            else
            {
                // Move to next sprint — validate it exists and is not completed
                var nextSprint = await _context.Sprints
                    .FirstOrDefaultAsync(s => s.Id == request.NextSprintId!.Value
                        && s.Status != SprintStatus.Completed, ct)
                    ?? throw new NotFoundException("Next sprint", request.NextSprintId!.Value);

                var nextPosition = (await _context.SprintWorkItems
                    .Where(sw => sw.SprintId == nextSprint.Id)
                    .MaxAsync(sw => (int?)sw.Position, ct) ?? -1) + 1;

                foreach (var workItemId in incompleteIds)
                {
                    // Update backlog entry sprint reference
                    var backlogEntry = await _context.BacklogItems
                        .FirstOrDefaultAsync(b => b.ProjectId == projectId && b.WorkItemId == workItemId, ct);
                    if (backlogEntry is not null)
                        backlogEntry.SprintId = nextSprint.Id;

                    // Avoid duplicates in the next sprint
                    var alreadyThere = await _context.SprintWorkItems
                        .AnyAsync(sw => sw.SprintId == nextSprint.Id && sw.WorkItemId == workItemId, ct);

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

        // Mark the sprint completed
        sprint.Status    = SprintStatus.Completed;
        sprint.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Sprint {SprintId} closed by {UserId}. Completed: {CompletedCount}, Incomplete: {IncompleteCount}",
            sprintId, closedBy, completedIds.Count, incompleteIds.Count);

        var sprintResponse = await BuildResponseAsync(sprintId, ct);

        return new CloseSprintResponse(
            sprintResponse,
            completedIds.Count,
            incompleteIds.Count,
            request.IncompleteItemsDestination,
            request.NextSprintId);
    }

    private async Task<SprintResponse> BuildResponseAsync(Guid sprintId, CancellationToken ct)
    {
        var sprint = await _context.Sprints
            .FirstOrDefaultAsync(s => s.Id == sprintId, ct)
            ?? throw new NotFoundException("Sprint", sprintId);

        var workItems = await _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId)
            .Join(_context.WorkItems,
                sw => sw.WorkItemId,
                w => w.Id,
                (sw, w) => new { w.State, w.StoryPoints })
            .ToListAsync(ct);

        var totalPoints     = workItems.Sum(w => w.StoryPoints ?? 0);
        var completedPoints = workItems
            .Where(w => w.State == WorkItemState.Closed || w.State == WorkItemState.Resolved)
            .Sum(w => w.StoryPoints ?? 0);
        var completedCount = workItems
            .Count(w => w.State == WorkItemState.Closed || w.State == WorkItemState.Resolved);

        return new SprintResponse(
            sprint.Id, sprint.TeamId, sprint.Number, sprint.Goal,
            sprint.StartDate, sprint.EndDate, sprint.Status,
            workItems.Count, completedCount,
            totalPoints, completedPoints,
            sprint.CreatedAt);
    }
}
