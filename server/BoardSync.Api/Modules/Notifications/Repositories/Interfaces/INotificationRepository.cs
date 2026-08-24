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
    /// <summary>The recipient's notifications, newest first, each with its organization's slug.</summary>
    Task<IReadOnlyList<NotificationWithContext>> GetForRecipientAsync(
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

/// <summary>A notification and the routing context a client needs to link to it.</summary>
/// <remarks>
/// Joined on read rather than stored on the row. A slug is renameable, and a copy written at
/// notification time would send people to a URL that stopped existing — the id is the fact, the slug
/// is how it is spelled today.
/// </remarks>
/// <param name="Notification">The row.</param>
/// <param name="OrganizationSlug">The slug of the organization owning its project.</param>
public sealed record NotificationWithContext(
    Notification Notification, string OrganizationSlug);
