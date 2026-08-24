using BoardSync.Api.Modules.Notifications.Models;

namespace BoardSync.Api.Modules.Notifications.Repositories.Interfaces;

/// <summary>
/// The read side of the notification bell.
/// </summary>
/// <remarks>
/// Every query is keyed on the recipient, so a notification is addressed rather than filtered — the
/// previous design read everyone's work item history and narrowed it, which is how it managed to
/// leak across the permission boundary while also returning nothing to anybody.
/// </remarks>
public interface INotificationRepository
{
    /// <summary>The recipient's notifications, newest first.</summary>
    Task<IReadOnlyList<Notification>> GetForRecipientAsync(
        Guid recipientId, bool unreadOnly, int take, CancellationToken ct = default);

    /// <summary>How many the recipient has not read.</summary>
    Task<int> CountUnreadAsync(Guid recipientId, CancellationToken ct = default);

    /// <summary>
    /// Marks one as read. Returns false when it is not the caller's to mark.
    /// </summary>
    /// <remarks>
    /// The recipient is part of the predicate rather than checked afterwards, so marking somebody
    /// else's notification read cannot happen even by mistake — and it answers the same way as an id
    /// that does not exist.
    /// </remarks>
    Task<bool> MarkReadAsync(Guid notificationId, Guid recipientId, CancellationToken ct = default);

    /// <summary>Marks everything the recipient has unread as read. Returns how many.</summary>
    Task<int> MarkAllReadAsync(Guid recipientId, CancellationToken ct = default);
}
