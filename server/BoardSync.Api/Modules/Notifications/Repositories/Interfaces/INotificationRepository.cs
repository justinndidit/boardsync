using BoardSync.Api.Modules.Notifications.DTOs;
using BoardSync.Api.Modules.Rbac.Models;

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
/// <para>
/// <b>Scoped by readable project, not by organization membership.</b> The feed used to span every
/// active project in every organization the caller belonged to, which handed an organization member
/// on no team the title, changed field and new value of every work item in the organization —
/// through a bell, without a permission check anywhere in the path. It is scoped by
/// <c>workitem:read</c> now, which is what a client needs to open any of these entries anyway.
/// </para>
/// </remarks>
public interface INotificationRepository
{
    /// <summary>
    /// The most recent work item changes in projects the caller may read, newest first, capped at
    /// <paramref name="take"/>.
    /// </summary>
    Task<IReadOnlyList<NotificationSource>> GetRecentForVisibleProjectsAsync(
        ProjectVisibility visibility,
        int take,
        CancellationToken ct = default);
}
