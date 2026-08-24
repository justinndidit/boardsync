using BoardSync.Api.Modules.Notifications.DTOs;

namespace BoardSync.Api.Modules.Notifications.Services;

/// <summary>The notification bell.</summary>
public interface INotificationService
{
    /// <summary>The caller's notifications and their unread count.</summary>
    Task<NotificationFeedResponse> GetFeedAsync(
        Guid userId, bool unreadOnly, int limit, CancellationToken ct = default);

    /// <summary>Marks one read. False when it is not theirs, already read, or gone.</summary>
    Task<bool> MarkReadAsync(Guid notificationId, Guid userId, CancellationToken ct = default);

    /// <summary>Marks everything unread as read. Returns how many.</summary>
    Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>Bell sizing. The bell is a glance, not a page — it does not paginate.</summary>
public static class NotificationDefaults
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
}
