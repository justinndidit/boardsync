using BoardSync.Api.Data;
using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.Sprints.Repositories.Interfaces;
using BoardSync.Api.Modules.WorkItems.Models;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Sprints.Repositories.Implementations;

/// <inheritdoc />
public class SprintRepository : ISprintRepository
{
    private readonly BoardSyncDbContext _context;

    public SprintRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    // ── Sprints ───────────────────────────────────────────────────────────────

    public Task<Sprint?> GetByIdAsync(Guid sprintId, CancellationToken ct = default) =>
        _context.Sprints.FirstOrDefaultAsync(s => s.Id == sprintId, ct);

    public Task<Sprint?> GetActiveForTeamAsync(Guid teamId, CancellationToken ct = default) =>
        _context.Sprints.FirstOrDefaultAsync(
            s => s.TeamId == teamId && s.Status == SprintStatus.Active, ct);

    public async Task<(IReadOnlyList<SprintSummaryResponse> Items, int TotalCount)> GetForTeamAsync(
        Guid teamId,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        var query = _context.Sprints.Where(s => s.TeamId == teamId);

        var total = await query.CountAsync(ct);

        // The backlog count is a correlated subquery rather than a second round trip plus an
        // in-memory dictionary join — same result, one query.
        var items = await query
            .OrderByDescending(s => s.Number)
            .Skip(skip)
            .Take(take)
            .Select(s => new SprintSummaryResponse(
                s.Id,
                s.Number,
                s.Goal,
                s.StartDate,
                s.EndDate,
                s.Status,
                _context.SprintWorkItems.Count(sw => sw.SprintId == s.Id)))
            .ToListAsync(ct);

        return (items, total);
    }

    public Task<bool> TeamExistsAsync(Guid teamId, CancellationToken ct = default) =>
        _context.Teams.AnyAsync(t => t.Id == teamId && t.IsActive, ct);

    public async Task<Guid?> GetAssignedTeamForProjectAsync(Guid projectId, CancellationToken ct = default) =>
        await _context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.AssignedTeamId)
            .FirstOrDefaultAsync(ct);

    public Task<bool> HasOverlappingSprintAsync(
        Guid teamId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken ct = default) =>
        _context.Sprints.AnyAsync(s =>
            s.TeamId == teamId
            && s.Status != SprintStatus.Completed
            && s.StartDate < endDate
            && s.EndDate > startDate, ct);

    public Task<bool> HasAnotherActiveSprintAsync(
        Guid teamId,
        Guid excludingSprintId,
        CancellationToken ct = default) =>
        _context.Sprints.AnyAsync(s =>
            s.TeamId == teamId
            && s.Status == SprintStatus.Active
            && s.Id != excludingSprintId, ct);

    public async Task<int> GetNextNumberAsync(Guid teamId, CancellationToken ct = default) =>
        (await _context.Sprints
            .Where(s => s.TeamId == teamId)
            .MaxAsync(s => (int?)s.Number, ct) ?? 0) + 1;

    public Task<Guid?> GetOrganizationIdForTeamAsync(Guid teamId, CancellationToken ct = default) =>
        _context.Teams
            .Where(t => t.Id == teamId)
            .Select(t => (Guid?)t.OrganizationId)
            .FirstOrDefaultAsync(ct);

    public void Add(Sprint sprint) => _context.Sprints.Add(sprint);

    public void Remove(Sprint sprint) => _context.Sprints.Remove(sprint);

    // ── Backlog ───────────────────────────────────────────────────────────────

    public Task<SprintWorkItem?> GetBacklogEntryAsync(
        Guid sprintId,
        Guid workItemId,
        CancellationToken ct = default) =>
        _context.SprintWorkItems.FirstOrDefaultAsync(
            sw => sw.SprintId == sprintId && sw.WorkItemId == workItemId, ct);

    public async Task<IReadOnlyList<SprintWorkItem>> GetBacklogEntriesAsync(
        Guid sprintId,
        CancellationToken ct = default) =>
        await _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId)
            .ToListAsync(ct);

    public Task<bool> BacklogContainsAsync(Guid sprintId, Guid workItemId, CancellationToken ct = default) =>
        _context.SprintWorkItems.AnyAsync(
            sw => sw.SprintId == sprintId && sw.WorkItemId == workItemId, ct);

    public Task<bool> HasBacklogEntriesAsync(Guid sprintId, CancellationToken ct = default) =>
        _context.SprintWorkItems.AnyAsync(sw => sw.SprintId == sprintId, ct);

    public async Task<int> GetNextPositionAsync(Guid sprintId, CancellationToken ct = default) =>
        (await _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId)
            .MaxAsync(sw => (int?)sw.Position, ct) ?? -1) + 1;

    public async Task<decimal?> GetMaxRankAsync(Guid sprintId, CancellationToken ct = default) =>
        await _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId)
            .MaxAsync(sw => (decimal?)sw.Rank, ct);

    public async Task<(decimal? Before, decimal? After)> GetNeighbourRanksAsync(
        Guid sprintId,
        Guid? beforeWorkItemId,
        Guid? afterWorkItemId,
        CancellationToken ct = default)
    {
        // Both neighbours in one query — they are two rows of the same table, and asking twice
        // would let the backlog change between the two reads.
        var ids = new[] { beforeWorkItemId, afterWorkItemId }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();

        if (ids.Count == 0) return (null, null);

        var ranks = await _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId && ids.Contains(sw.WorkItemId))
            .Select(sw => new { sw.WorkItemId, sw.Rank })
            .ToDictionaryAsync(x => x.WorkItemId, x => x.Rank, ct);

        decimal? before = beforeWorkItemId.HasValue && ranks.TryGetValue(beforeWorkItemId.Value, out var b)
            ? b : null;

        decimal? after = afterWorkItemId.HasValue && ranks.TryGetValue(afterWorkItemId.Value, out var a)
            ? a : null;

        return (before, after);
    }

    public async Task<(IReadOnlyList<SprintWorkItemResponse> Items, int TotalCount)> GetWorkItemsAsync(
        Guid sprintId,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        var query = _context.SprintWorkItems.Where(sw => sw.SprintId == sprintId);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(sw => sw.Rank)
            .Skip(skip)
            .Take(take)
            .Join(_context.WorkItems,
                sw => sw.WorkItemId,
                w => w.Id,
                (sw, w) => new SprintWorkItemResponse(
                    w.Id, w.Title, w.Type, w.State,
                    w.Priority, w.AssigneeId, w.StoryPoints, sw.Position))
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<SprintProgress> GetProgressAsync(Guid sprintId, CancellationToken ct = default)
    {
        // Aggregated in the database rather than by pulling every row back and summing in memory,
        // which is what a long-running sprint would make expensive.
        var progress = await _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId)
            .Join(_context.WorkItems,
                sw => sw.WorkItemId,
                w => w.Id,
                (sw, w) => w)
            .GroupBy(_ => 1)
            .Select(g => new SprintProgress(
                g.Count(),
                g.Count(w => w.State == WorkItemState.Closed || w.State == WorkItemState.Resolved),
                g.Sum(w => w.StoryPoints ?? 0),
                g.Sum(w => w.State == WorkItemState.Closed || w.State == WorkItemState.Resolved
                    ? w.StoryPoints ?? 0
                    : 0)))
            .FirstOrDefaultAsync(ct);

        // An empty backlog groups to no rows at all, which is zero progress rather than no answer.
        return progress;
    }

    public void AddBacklogEntry(SprintWorkItem entry) => _context.SprintWorkItems.Add(entry);

    public void RemoveBacklogEntry(SprintWorkItem entry) => _context.SprintWorkItems.Remove(entry);

    // ── Unit of work ──────────────────────────────────────────────────────────

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);
}
