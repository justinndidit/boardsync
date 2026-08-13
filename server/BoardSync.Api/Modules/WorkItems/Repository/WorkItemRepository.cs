using BoardSync.Api.Data;
using BoardSync.Api.Modules.WorkItems.DTOs;
using BoardSync.Api.Modules.WorkItems.Models;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.WorkItems.Repository;

/// <inheritdoc />
public class WorkItemRepository : IWorkItemRepository
{
    private readonly BoardSyncDbContext _context;

    public WorkItemRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    // ── Work items ────────────────────────────────────────────────────────────

    public Task<WorkItem?> GetActiveAsync(Guid workItemId, CancellationToken ct = default) =>
        _context.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId && w.IsActive, ct);

    public Task<WorkItem?> GetActiveWithTagsAsync(Guid workItemId, CancellationToken ct = default) =>
        _context.WorkItems
            .Include(w => w.Tags)
            .FirstOrDefaultAsync(w => w.Id == workItemId && w.IsActive, ct);

    public Task<WorkItem?> GetWithTagsAsync(Guid workItemId, CancellationToken ct = default) =>
        _context.WorkItems
            .Include(w => w.Tags)
            .FirstOrDefaultAsync(w => w.Id == workItemId, ct);

    public Task<WorkItem?> GetActiveInProjectAsync(Guid workItemId, Guid projectId, CancellationToken ct = default) =>
        _context.WorkItems
            .FirstOrDefaultAsync(w => w.Id == workItemId && w.ProjectId == projectId && w.IsActive, ct);

    public async Task<(IReadOnlyList<WorkItem> Items, int TotalCount)> GetForProjectAsync(
        Guid projectId,
        WorkItemFilterQuery filter,
        int skip,
        int take,
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
        {
            var tag = filter.Tag.Trim().ToLowerInvariant();
            query = query.Where(w => w.Tags.Any(t => t.Name == tag));
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(w => w.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetChildCountsAsync(
        IEnumerable<Guid> parentIds,
        CancellationToken ct = default)
    {
        var ids = parentIds as IReadOnlyList<Guid> ?? parentIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, int>();

        return await _context.WorkItems
            .Where(w => w.ParentId != null && w.IsActive && ids.Contains(w.ParentId.Value))
            .GroupBy(w => w.ParentId!.Value)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ParentId, x => x.Count, ct);
    }

    public Task<int> GetChildCountAsync(Guid workItemId, CancellationToken ct = default) =>
        _context.WorkItems.CountAsync(w => w.ParentId == workItemId && w.IsActive, ct);

    public void Add(WorkItem item) => _context.WorkItems.Add(item);

    // ── Tags ──────────────────────────────────────────────────────────────────

    public void AddTag(WorkItemTag tag) => _context.WorkItemTags.Add(tag);

    public void RemoveTag(WorkItemTag tag) => _context.WorkItemTags.Remove(tag);

    // ── Comments ──────────────────────────────────────────────────────────────

    public Task<WorkItemComment?> GetCommentAsync(Guid commentId, CancellationToken ct = default) =>
        _context.WorkItemComments.FirstOrDefaultAsync(c => c.Id == commentId, ct);

    public async Task<(IReadOnlyList<WorkItemComment> Items, int TotalCount)> GetCommentsAsync(
        Guid workItemId,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        var query = _context.WorkItemComments.Where(c => c.WorkItemId == workItemId);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(c => c.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return (items, total);
    }

    public Task<int> GetCommentCountAsync(Guid workItemId, CancellationToken ct = default) =>
        _context.WorkItemComments.CountAsync(c => c.WorkItemId == workItemId, ct);

    public void AddComment(WorkItemComment comment) => _context.WorkItemComments.Add(comment);

    public void RemoveComment(WorkItemComment comment) => _context.WorkItemComments.Remove(comment);

    // ── History ───────────────────────────────────────────────────────────────

    public async Task<(IReadOnlyList<WorkItemHistory> Items, int TotalCount)> GetHistoryAsync(
        Guid workItemId,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        var query = _context.WorkItemHistory.Where(h => h.WorkItemId == workItemId);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(h => h.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return (items, total);
    }

    public void AddHistory(WorkItemHistory entry) => _context.WorkItemHistory.Add(entry);

    // ── Links ─────────────────────────────────────────────────────────────────

    public Task<WorkItemLink?> GetLinkAsync(Guid linkId, CancellationToken ct = default) =>
        _context.WorkItemLinks.FirstOrDefaultAsync(l => l.Id == linkId, ct);

    public async Task<IReadOnlyList<WorkItemLink>> GetLinksWithTargetAsync(
        Guid workItemId,
        CancellationToken ct = default) =>
        await _context.WorkItemLinks
            .Include(l => l.Target)
            .Where(l => l.SourceId == workItemId)
            .ToListAsync(ct);

    public Task<bool> LinkExistsAsync(
        Guid sourceId,
        Guid targetId,
        WorkItemLinkType linkType,
        CancellationToken ct = default) =>
        _context.WorkItemLinks.AnyAsync(
            l => l.SourceId == sourceId && l.TargetId == targetId && l.LinkType == linkType, ct);

    public void AddLink(WorkItemLink link) => _context.WorkItemLinks.Add(link);

    public void RemoveLink(WorkItemLink link) => _context.WorkItemLinks.Remove(link);

    // ── Scope resolution ──────────────────────────────────────────────────────

    public Task<Guid?> GetProjectIdForLinkAsync(Guid linkId, CancellationToken ct = default) =>
        _context.WorkItemLinks
            .Where(l => l.Id == linkId)
            .Select(l => (Guid?)l.Source.ProjectId)
            .FirstOrDefaultAsync(ct);

    public Task<Guid?> GetProjectIdForCommentAsync(Guid commentId, CancellationToken ct = default) =>
        _context.WorkItemComments
            .Where(c => c.Id == commentId)
            .Select(c => (Guid?)c.WorkItem.ProjectId)
            .FirstOrDefaultAsync(ct);

    // ── Unit of work ──────────────────────────────────────────────────────────

    public void SetOriginalVersion(WorkItem item, uint version) =>
        _context.Entry(item).Property(w => w.Version).OriginalValue = version;

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);
}
