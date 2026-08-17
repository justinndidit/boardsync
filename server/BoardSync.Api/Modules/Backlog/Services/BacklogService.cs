using BoardSync.Api.Data;
using BoardSync.Api.Modules.Backlog.DTOs;
using BoardSync.Api.Modules.Backlog.Models;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Backlog.Services;

public class BacklogService : IBacklogService
{
    private readonly BoardSyncDbContext _context;
    private readonly ILogger<BacklogService> _logger;

    public BacklogService(BoardSyncDbContext context, ILogger<BacklogService> logger)
    {
        _context = context;
        _logger = logger;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<PagedResult<BacklogItemResponse>> GetForProjectAsync(
        Guid projectId,
        Guid? teamId,
        PaginationQuery pagination,
        CancellationToken ct = default)
    {
        var query = _context.BacklogItems
            .Where(b => b.ProjectId == projectId && b.SprintId == null);

        if (teamId.HasValue)
            query = query.Where(b => b.TeamId == null || b.TeamId == teamId.Value);

        var total = await query.CountAsync(ct);

        var pageSize = Math.Clamp(pagination.PageSize, 1, 100);
        var page     = Math.Max(pagination.Page, 1);

        var backlogEntries = await query
            .OrderBy(b => b.Rank)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var workItemIds = backlogEntries.Select(b => b.WorkItemId).ToList();

        var workItems = await _context.WorkItems
            .Include(w => w.Tags)
            .Where(w => workItemIds.Contains(w.Id) && w.IsActive)
            .ToDictionaryAsync(w => w.Id, ct);

        var childCounts = await _context.WorkItems
            .Where(w => w.ParentId != null && w.IsActive && workItemIds.Contains(w.ParentId.Value))
            .GroupBy(w => w.ParentId!.Value)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ParentId, x => x.Count, ct);

        var items = backlogEntries
            .Where(b => workItems.ContainsKey(b.WorkItemId))
            .Select(b =>
            {
                var w = workItems[b.WorkItemId];
                return new BacklogItemResponse(
                    b.Id, b.WorkItemId, b.ProjectId, b.TeamId, b.SprintId, b.Rank,
                    w.Title, w.Type, w.State, w.Priority, w.AssigneeId, w.StoryPoints,
                    w.Tags.Select(t => t.Name).ToList(),
                    childCounts.GetValueOrDefault(w.Id, 0),
                    w.CreatedAt);
            }).ToList();

        return new PagedResult<BacklogItemResponse>(items, total, page, pageSize);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task<BacklogItemResponse> AddAsync(
        Guid projectId,
        AddToBacklogRequest request,
        Guid addedBy,
        CancellationToken ct = default)
    {
        if (!await _context.Projects.AnyAsync(p => p.Id == projectId && p.IsActive, ct))
            throw new NotFoundException("Project", projectId);

        var workItem = await _context.WorkItems
            .Include(w => w.Tags)
            .FirstOrDefaultAsync(w => w.Id == request.WorkItemId && w.ProjectId == projectId && w.IsActive, ct)
            ?? throw new NotFoundException("WorkItem", request.WorkItemId);

        // Idempotent — return existing entry unchanged
        var existing = await _context.BacklogItems
            .FirstOrDefaultAsync(b => b.ProjectId == projectId && b.WorkItemId == request.WorkItemId, ct);

        if (existing is not null)
            return await BuildResponseAsync(existing, workItem, ct);

        var rank = request.Rank ?? (
            await _context.BacklogItems
                .Where(b => b.ProjectId == projectId)
                .MaxAsync(b => (int?)b.Rank, ct) ?? -1) + 1;

        var entry = new BacklogItem
        {
            ProjectId  = projectId,
            WorkItemId = request.WorkItemId,
            TeamId     = request.TeamId,
            Rank       = rank,
            CreatedBy  = addedBy
        };

        _context.BacklogItems.Add(entry);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("WorkItem {WorkItemId} added to backlog of project {ProjectId}", request.WorkItemId, projectId);

        return await BuildResponseAsync(entry, workItem, ct);
    }

    public async Task RemoveAsync(Guid projectId, Guid workItemId, CancellationToken ct = default)
    {
        var entry = await _context.BacklogItems
            .FirstOrDefaultAsync(b => b.ProjectId == projectId && b.WorkItemId == workItemId, ct)
            ?? throw new NotFoundException("BacklogItem", workItemId);

        _context.BacklogItems.Remove(entry);
        await _context.SaveChangesAsync(ct);
    }

    public async Task ReorderAsync(
        Guid projectId,
        ReorderBacklogRequest request,
        CancellationToken ct = default)
    {
        var entries = await _context.BacklogItems
            .Where(b => b.ProjectId == projectId)
            .ToListAsync(ct);

        // Apply explicit ranks for the provided IDs
        for (int i = 0; i < request.WorkItemIds.Count; i++)
        {
            var entry = entries.FirstOrDefault(b => b.WorkItemId == request.WorkItemIds[i]);
            if (entry is not null)
                entry.Rank = i;
        }

        // Items not in the list get pushed to the bottom, preserving their relative order
        var ranked    = request.WorkItemIds.ToHashSet();
        var remaining = entries
            .Where(b => !ranked.Contains(b.WorkItemId))
            .OrderBy(b => b.Rank)
            .ToList();

        for (int i = 0; i < remaining.Count; i++)
            remaining[i].Rank = request.WorkItemIds.Count + i;

        await _context.SaveChangesAsync(ct);
    }

    public async Task<BacklogBulkOperationResponse> MoveToSprintAsync(
        Guid projectId,
        MoveToSprintRequest request,
        Guid movedBy,
        CancellationToken ct = default)
    {
        var sprint = await _context.Sprints
            .FirstOrDefaultAsync(s => s.Id == request.SprintId && s.Status != SprintStatus.Completed, ct)
            ?? throw new NotFoundException("Sprint", request.SprintId);

        var entries = await _context.BacklogItems
            .Where(b => b.ProjectId == projectId && request.WorkItemIds.Contains(b.WorkItemId))
            .ToListAsync(ct);

        if (entries.Count == 0)
            return new BacklogBulkOperationResponse(0, "No matching backlog items found.");

        // Determine next position in sprint
        var nextPosition = (await _context.SprintWorkItems
            .Where(sw => sw.SprintId == request.SprintId)
            .MaxAsync(sw => (int?)sw.Position, ct) ?? -1) + 1;

        foreach (var entry in entries)
        {
            entry.SprintId = request.SprintId;

            // Add to sprint if not already there
            var alreadyInSprint = await _context.SprintWorkItems
                .AnyAsync(sw => sw.SprintId == request.SprintId && sw.WorkItemId == entry.WorkItemId, ct);

            if (!alreadyInSprint)
            {
                _context.SprintWorkItems.Add(new SprintWorkItem
                {
                    SprintId   = request.SprintId,
                    WorkItemId = entry.WorkItemId,
                    Position   = nextPosition++,
                    CreatedBy  = movedBy
                });
            }
        }

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("{Count} items moved to sprint {SprintId} from project {ProjectId}",
            entries.Count, request.SprintId, projectId);

        return new BacklogBulkOperationResponse(entries.Count,
            $"{entries.Count} item(s) moved to sprint.");
    }

    public async Task<BacklogBulkOperationResponse> ReturnToBacklogAsync(
        Guid projectId,
        ReturnToBacklogRequest request,
        CancellationToken ct = default)
    {
        var entries = await _context.BacklogItems
            .Where(b => b.ProjectId == projectId
                && request.WorkItemIds.Contains(b.WorkItemId)
                && b.SprintId != null)
            .ToListAsync(ct);

        if (entries.Count == 0)
            return new BacklogBulkOperationResponse(0, "No matching sprint items found.");

        var workItemIds = entries.Select(b => b.WorkItemId).ToList();
        var sprintItems = await _context.SprintWorkItems
            .Where(sw => workItemIds.Contains(sw.WorkItemId))
            .ToListAsync(ct);

        foreach (var entry in entries)
            entry.SprintId = null;

        _context.SprintWorkItems.RemoveRange(sprintItems);

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("{Count} items returned to backlog in project {ProjectId}",
            entries.Count, projectId);

        return new BacklogBulkOperationResponse(entries.Count,
            $"{entries.Count} item(s) returned to backlog.");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<BacklogItemResponse> BuildResponseAsync(
        BacklogItem entry, WorkItem w, CancellationToken ct)
    {
        var childCount = await _context.WorkItems
            .CountAsync(c => c.ParentId == w.Id && c.IsActive, ct);

        return new BacklogItemResponse(
            entry.Id, entry.WorkItemId, entry.ProjectId, entry.TeamId, entry.SprintId, entry.Rank,
            w.Title, w.Type, w.State, w.Priority, w.AssigneeId, w.StoryPoints,
            w.Tags.Select(t => t.Name).ToList(),
            childCount, w.CreatedAt);
    }
}
