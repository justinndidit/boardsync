using BoardSync.Api.Modules.Notifications.DTOs;

namespace BoardSync.Api.Modules.Notifications.Services;

/// <summary>
/// The notification bell: recent changes worth surfacing to one user.
/// </summary>
/// <remarks>
/// <para>
/// Derived from work item history on read, not stored. Two consequences worth knowing before
/// building on this: there is no read/unread state, and the feed only knows about work item field
/// changes — project, team, sprint and membership activity do not appear here even though they do
/// appear in the activity feed.
/// </para>
/// <para>
/// It also keeps its own <c>type</c> vocabulary (<c>WorkItemUpdated</c>, <c>WorkItem{State}</c>)
/// rather than the activity feed's <c>EntityType.Verb</c> form. Rebuilding this on the activity log
/// would fix all three at once and is the obvious next step for this module; until then, do not
/// share a rendering component between the bell and the feed.
/// </para>
/// </remarks>
public interface INotificationService
{
    /// <summary>
    /// Recent notifications for one user, newest first, across every organization they belong to.
    /// </summary>
    /// <param name="userId">The recipient.</param>
    /// <param name="limit">Maximum entries to return. Clamped to a sane ceiling.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<NotificationResponse>> GetForUserAsync(
        Guid userId,
        int limit = NotificationDefaults.DefaultLimit,
        CancellationToken ct = default);
}

/// <summary>Bell sizing. The bell is a glance, not a page — it does not paginate.</summary>
public static class NotificationDefaults
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
}
