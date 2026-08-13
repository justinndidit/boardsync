using BoardSync.Api.Modules.Notifications.DTOs;

namespace BoardSync.Api.Modules.Notifications.Repositories.Interfaces;

/// <summary>
/// Data access for the notification bell.
/// </summary>
/// <remarks>
/// <para>
/// Notifications are not stored. They are derived on read from <c>work.WorkItemHistory</c>, which
/// means there is no per-user read state and no fan-out on write — and equally, no way to mark one
/// as read. That is a known limitation of the current design rather than an oversight; see the
/// module's service for what a stored notification would change.
/// </para>
/// <para>
/// The query filters on the history row's own <c>ProjectId</c> rather than reaching through the
/// work item, so one composite index serves both the filter and the ordering.
/// </para>
/// </remarks>
public interface INotificationRepository
{
    /// <summary>Organizations the user is a member of.</summary>
    Task<IReadOnlyList<Guid>> GetOrganizationIdsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The most recent work item changes across every active project in the given organizations,
    /// newest first, capped at <paramref name="take"/>.
    /// </summary>
    Task<IReadOnlyList<NotificationSource>> GetRecentForOrganizationsAsync(
        IReadOnlyCollection<Guid> organizationIds,
        int take,
        CancellationToken ct = default);
}
