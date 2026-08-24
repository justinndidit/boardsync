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

    public async Task<IReadOnlyList<Notification>> GetForRecipientAsync(
        Guid recipientId, bool unreadOnly, int take, CancellationToken ct = default)
    {
        var query = _context.Notifications.Where(n => n.RecipientId == recipientId);

        if (unreadOnly) query = query.Where(n => n.ReadAt == null);

        // Id breaks ties: notifications written in one transaction share a CreatedAt, and without a
        // total order the rows returned are not deterministic between calls.
        return await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Take(take)
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
