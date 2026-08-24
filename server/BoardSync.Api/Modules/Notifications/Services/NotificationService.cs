using BoardSync.Api.Modules.Notifications.DTOs;
using BoardSync.Api.Modules.Notifications.Models;
using BoardSync.Api.Modules.Notifications.Repositories.Interfaces;

namespace BoardSync.Api.Modules.Notifications.Services;

/// <inheritdoc />
/// <remarks>
/// <para>
/// Thin, and deliberately so. The bell used to derive its contents by querying work item history and
/// wording each row on read, which meant every reader recomputed the same sentences and the module
/// needed the permission model to decide what to hide. A notification is now addressed to one person
/// when it is written, so reading it is a lookup by recipient and nothing else.
/// </para>
/// <para>
/// <b>No permission filtering here, and that is not an omission.</b> A notification exists because
/// somebody was entitled to it at the moment it was raised — they were the assignee, they were
/// watching, they hold <c>workitem:verify</c>. Re-checking on read would be a second, weaker
/// implementation of the same decision.
/// </para>
/// </remarks>
public class NotificationService : INotificationService
{
    private readonly INotificationRepository _repository;

    public NotificationService(INotificationRepository repository)
    {
        _repository = repository;
    }

    public async Task<NotificationFeedResponse> GetFeedAsync(
        Guid userId, bool unreadOnly, int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, NotificationDefaults.MaxLimit);

        var items = await _repository.GetForRecipientAsync(userId, unreadOnly, take, ct);

        // Counted separately rather than taken from the page: the badge has to be right even when
        // the list is truncated, and "20+" is a worse answer than the number.
        var unread = await _repository.CountUnreadAsync(userId, ct);

        return new NotificationFeedResponse([.. items.Select(Describe)], unread);
    }

    public Task<bool> MarkReadAsync(Guid notificationId, Guid userId, CancellationToken ct = default) =>
        _repository.MarkReadAsync(notificationId, userId, ct);

    public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default) =>
        _repository.MarkAllReadAsync(userId, ct);

    private static NotificationResponse Describe(Notification n) =>
        new(n.Id, n.Type, n.Title, n.Detail, n.Reference, n.EntityId, n.ProjectId,
            n.ActorName, n.ReadAt is not null, n.CreatedAt);
}
