using BoardSync.Api.Data;
using BoardSync.Api.Modules.Notifications.DTOs;
using BoardSync.Api.Modules.Notifications.Repositories.Interfaces;
using BoardSync.Api.Modules.Rbac.Models;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Notifications.Repositories.Implementations;

/// <inheritdoc />
public class NotificationRepository : INotificationRepository
{
    private readonly BoardSyncDbContext _context;

    public NotificationRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<NotificationSource>> GetRecentForVisibleProjectsAsync(
        ProjectVisibility visibility,
        int take,
        CancellationToken ct = default)
    {
        if (visibility.IsEmpty) return [];

        // Left unmaterialized so it becomes a subquery rather than a round trip whose result is
        // shipped straight back as an IN list.
        var projectIds = _context.Projects
            .Where(visibility.Predicate())
            .Where(p => p.IsActive)
            .Select(p => p.Id);

        // Id breaks ties: entries written in one transaction share a CreatedAt, and without a
        // total order the rows returned are not deterministic between calls.
        return await _context.WorkItemHistory
            .Where(h => projectIds.Contains(h.ProjectId))
            .OrderByDescending(h => h.CreatedAt)
            .ThenByDescending(h => h.Id)
            .Take(take)
            .Select(h => new NotificationSource(
                h.Id,
                h.FieldName,
                h.NewValue,
                h.WorkItem.Title,
                _context.Projects
                    .Where(p => p.Id == h.ProjectId)
                    .Select(p => p.Organization.Name)
                    .FirstOrDefault(),
                h.CreatedAt))
            .ToListAsync(ct);
    }
}
