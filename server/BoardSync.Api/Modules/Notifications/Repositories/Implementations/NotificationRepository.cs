using BoardSync.Api.Data;
using BoardSync.Api.Modules.Notifications.Models;
using BoardSync.Api.Modules.Notifications.Repositories.Interfaces;
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

    public async Task<IReadOnlyList<NotificationWithContext>> GetForRecipientAsync(
        Guid recipientId, bool unreadOnly, int take, CancellationToken ct = default)
    {
        var query = _context.Notifications.Where(n => n.RecipientId == recipientId);

        if (unreadOnly) query = query.Where(n => n.ReadAt == null);

        // Id breaks ties: notifications written in one transaction share a CreatedAt, and without a
        // total order the rows returned are not deterministic between calls.
        //
        // The organization is joined rather than stored on the row, and left-joined rather than
        // required: a project deleted after a notification was raised must not remove the entry
        // telling somebody what happened. Those rows come back with an empty slug and render
        // unlinked.
        return await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Take(take)
            .Select(n => new NotificationWithContext(
                n,
                _context.Projects
                    .Where(p => p.Id == n.ProjectId)
                    .Join(_context.Organizations, p => p.OrganizationId, o => o.Id, (p, o) => o.Slug)
                    .FirstOrDefault() ?? string.Empty))
            .ToListAsync(ct);
    }

    public Task<int> CountUnreadAsync(Guid recipientId, CancellationToken ct = default) =>
        _context.Notifications.CountAsync(n => n.RecipientId == recipientId && n.ReadAt == null, ct);

    public async Task<bool> MarkReadAsync(
        Guid notificationId, Guid recipientId, CancellationToken ct = default)
    {
        var updated = await _context.Notifications
            .Where(n => n.Id == notificationId && n.RecipientId == recipientId && n.ReadAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.ReadAt, DateTime.UtcNow), ct);

        // Zero means it was already read, is not theirs, or does not exist. All three are "nothing
        // to do", and telling them apart would say more than the caller is entitled to know.
        return updated > 0;
    }

    public Task<int> MarkAllReadAsync(Guid recipientId, CancellationToken ct = default) =>
        _context.Notifications
            .Where(n => n.RecipientId == recipientId && n.ReadAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.ReadAt, DateTime.UtcNow), ct);
}
