using BoardSync.Api.Modules.Notifications.DTOs;
using BoardSync.Api.Modules.Notifications.Repositories.Interfaces;
using BoardSync.Api.Modules.Notifications.Services;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.Notifications.Controllers;

/// <summary>
/// The notification bell, and who is watching what.
/// </summary>
/// <remarks>
/// Every notification is addressed to one person when it is written, so these endpoints are keyed on
/// the caller and never take a recipient. There is deliberately no way to read anybody else's bell.
/// </remarks>
[ApiController]
[Authorize]
[Produces("application/json")]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notifications;
    private readonly INotificationWriter _watching;
    private readonly ICurrentUserContext _currentUser;

    public NotificationsController(
        INotificationService notifications,
        INotificationWriter watching,
        ICurrentUserContext currentUser)
    {
        _notifications = notifications;
        _watching = watching;
        _currentUser = currentUser;
    }

    /// <summary>
    /// The caller's notifications, newest first, with their unread count.
    /// </summary>
    /// <remarks>
    /// Also served at the original <c>GET /api/workspace/notifications</c>, which stays because
    /// clients were written against it.
    /// </remarks>
    /// <param name="unreadOnly">Only what has not been read. Default false.</param>
    /// <param name="limit">Maximum entries, default 20, clamped to 50 rather than rejected.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("api/notifications")]
    [HttpGet("api/workspace/notifications")]
    [NoPermissionRequired(
        "Returns only the caller's own notifications — every query is keyed on their id, and a " +
        "notification exists because they were entitled to it when it was raised.")]
    [ProducesResponseType(typeof(ApiResponse<NotificationFeedResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyNotifications(
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int limit = NotificationDefaults.DefaultLimit,
        CancellationToken ct = default)
    {
        var feed = await _notifications.GetFeedAsync(_currentUser.UserId, unreadOnly, limit, ct);

        return Ok(new ApiResponse<NotificationFeedResponse>(true, "Notifications retrieved.", feed));
    }

    /// <summary>Mark one notification read.</summary>
    /// <remarks>
    /// 404 when it is not the caller's, already read, or does not exist. The three are
    /// deliberately indistinguishable: all mean "nothing to do", and separating them would confirm
    /// that somebody else's notification id is real.
    /// </remarks>
    [HttpPost("api/notifications/{notificationId:guid}/read")]
    [NoPermissionRequired(
        "Marks one of the caller's own notifications read; the recipient is part of the update's " +
        "predicate, so another person's cannot be touched.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(Guid notificationId, CancellationToken ct)
    {
        var marked = await _notifications.MarkReadAsync(notificationId, _currentUser.UserId, ct);

        return marked ? NoContent() : NotFound();
    }

    /// <summary>Mark everything read.</summary>
    [HttpPost("api/notifications/read-all")]
    [NoPermissionRequired("Marks the caller's own notifications read.")]
    [ProducesResponseType(typeof(ApiResponse<int>), StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        var count = await _notifications.MarkAllReadAsync(_currentUser.UserId, ct);

        return Ok(new ApiResponse<int>(true, $"Marked {count} notification(s) read.", count));
    }

    // ── Watching ──────────────────────────────────────────────────────────────

    /// <summary>Whether the caller is watching a work item. Requires <c>workitem:read</c>.</summary>
    [HttpGet("api/workitems/{workItemId:guid}/watch")]
    [RequirePermission(Permissions.WorkItemRead, From = "workItemId")]
    [ProducesResponseType(typeof(ApiResponse<WatchStateResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWatchState(Guid workItemId, CancellationToken ct)
    {
        var watching = await _watching.IsWatchingAsync(workItemId, _currentUser.UserId, ct);

        return Ok(new ApiResponse<WatchStateResponse>(
            true, "Watch state retrieved.", new WatchStateResponse(workItemId, watching)));
    }

    /// <summary>
    /// Start watching a work item. Requires <c>workitem:read</c>.
    /// </summary>
    /// <remarks>
    /// Most watching is implicit — being assigned an item or commenting on it starts you watching —
    /// so this is for the case that misses: following work somebody else is doing.
    /// </remarks>
    [HttpPost("api/workitems/{workItemId:guid}/watch")]
    [RequirePermission(Permissions.WorkItemRead, From = "workItemId")]
    [ProducesResponseType(typeof(ApiResponse<WatchStateResponse>), StatusCodes.Status200OK)]
    public Task<IActionResult> Watch(Guid workItemId, CancellationToken ct) =>
        SetWatchAsync(workItemId, watching: true, ct);

    /// <summary>
    /// Stop watching a work item. Requires <c>workitem:read</c>.
    /// </summary>
    /// <remarks>
    /// Remembered rather than forgotten: a later comment will not quietly re-subscribe somebody who
    /// deliberately opted out.
    /// </remarks>
    [HttpDelete("api/workitems/{workItemId:guid}/watch")]
    [RequirePermission(Permissions.WorkItemRead, From = "workItemId")]
    [ProducesResponseType(typeof(ApiResponse<WatchStateResponse>), StatusCodes.Status200OK)]
    public Task<IActionResult> Unwatch(Guid workItemId, CancellationToken ct) =>
        SetWatchAsync(workItemId, watching: false, ct);

    private async Task<IActionResult> SetWatchAsync(Guid workItemId, bool watching, CancellationToken ct)
    {
        var item = await _watching.DescribeAsync(workItemId, ct);

        if (item is not { } found) return NotFound();

        await _watching.SetWatchingAsync(workItemId, found.ProjectId, _currentUser.UserId, watching, ct);

        return Ok(new ApiResponse<WatchStateResponse>(
            true, watching ? "Watching." : "No longer watching.",
            new WatchStateResponse(workItemId, watching)));
    }
}
