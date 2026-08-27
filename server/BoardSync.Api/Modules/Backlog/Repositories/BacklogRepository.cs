using BoardSync.Api.Data;
using BoardSync.Api.Modules.Backlog.Models;
using BoardSync.Api.Modules.WorkItems.Models;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Backlog.Repositories;

/// <inheritdoc />
public class BacklogRepository : IBacklogRepository
{
    private readonly BoardSyncDbContext _context;

    public BacklogRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    public Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct = default) =>
        _context.Projects.AnyAsync(p => p.Id == projectId && p.IsActive, ct);

    public async Task<string> GetProjectKeyAsync(
        Guid projectId, CancellationToken ct = default) =>
        await _context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => p.Key)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

    public async Task<(IReadOnlyList<BacklogItem> Items, int TotalCount)> GetUnscheduledPageAsync(
        Guid projectId, Guid? teamId, int skip, int take, CancellationToken ct = default)
    {
        var query = _context.BacklogItems
            .Where(b => b.ProjectId == projectId && b.SprintId == null);

        if (teamId.HasValue)
            query = query.Where(b => b.TeamId == null || b.TeamId == teamId.Value);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(b => b.Rank)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<IReadOnlyDictionary<Guid, WorkItem>> GetWorkItemsAsync(
        IReadOnlyCollection<Guid> workItemIds, CancellationToken ct = default)
    {
        if (workItemIds.Count == 0) return new Dictionary<Guid, WorkItem>();

        return await _context.WorkItems
            .Include(w => w.Tags)
            .Where(w => workItemIds.Contains(w.Id) && w.IsActive)
            .ToDictionaryAsync(w => w.Id, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetChildCountsAsync(
        IReadOnlyCollection<Guid> parentIds, CancellationToken ct = default)
    {
        if (parentIds.Count == 0) return new Dictionary<Guid, int>();

        return await _context.WorkItems
            .Where(w => w.ParentId != null && w.IsActive && parentIds.Contains(w.ParentId!.Value))
            .GroupBy(w => w.ParentId!.Value)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ParentId, x => x.Count, ct);
    }

    public Task<WorkItem?> GetWorkItemInProjectAsync(
        Guid workItemId, Guid projectId, CancellationToken ct = default) =>
        _context.WorkItems
            .Include(w => w.Tags)
            .FirstOrDefaultAsync(w => w.Id == workItemId && w.ProjectId == projectId && w.IsActive, ct);

    public Task<BacklogItem?> GetEntryAsync(
        Guid projectId, Guid workItemId, CancellationToken ct = default) =>
        _context.BacklogItems
            .FirstOrDefaultAsync(b => b.ProjectId == projectId && b.WorkItemId == workItemId, ct);

    public async Task<IReadOnlyList<BacklogItem>> GetEntriesAsync(
        Guid projectId, IReadOnlyCollection<Guid> workItemIds, CancellationToken ct = default)
    {
        if (workItemIds.Count == 0) return [];

        return await _context.BacklogItems
            .Where(b => b.ProjectId == projectId && workItemIds.Contains(b.WorkItemId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BacklogItem>> GetEntriesForSprintAsync(
        Guid sprintId, IReadOnlyCollection<Guid> workItemIds, CancellationToken ct = default)
    {
        if (workItemIds.Count == 0) return [];

        return await _context.BacklogItems
            .Where(b => b.SprintId == sprintId && workItemIds.Contains(b.WorkItemId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BacklogItem>> GetAllEntriesAsync(
        Guid projectId, CancellationToken ct = default) =>
        await _context.BacklogItems
            .Where(b => b.ProjectId == projectId)
            .OrderBy(b => b.Rank)
            .ToListAsync(ct);

    public Task<decimal?> GetMaxRankAsync(Guid projectId, CancellationToken ct = default) =>
        _context.BacklogItems
            .Where(b => b.ProjectId == projectId)
            .MaxAsync(b => (decimal?)b.Rank, ct);

    public void Add(BacklogItem entry) => _context.BacklogItems.Add(entry);

    public void Remove(BacklogItem entry) => _context.BacklogItems.Remove(entry);

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);
}
