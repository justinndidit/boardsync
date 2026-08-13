using BoardSync.Api.Modules.Notifications.DTOs;
using BoardSync.Api.Modules.Notifications.Services;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.Notifications.Controllers;

/// <summary>
/// The notification bell — recent work item changes across the caller's organizations.
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notifications;
    private readonly ICurrentUserContext _currentUser;

    public NotificationsController(INotificationService notifications, ICurrentUserContext currentUser)
    {
        _notifications = notifications;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Recent notifications for the current user, newest first.
    /// </summary>
    /// <remarks>
    /// Also served at the original <c>GET /api/workspace/notifications</c>. Both routes run this
    /// same action and return the same body; the workspace path stays because clients were already
    /// written against it.
    /// </remarks>
    /// <param name="limit">
    /// Maximum entries, default 20, clamped to 50. Values outside the range are clamped rather
    /// than rejected.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("api/notifications")]
    [HttpGet("api/workspace/notifications")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<NotificationResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyNotifications(
        [FromQuery] int limit = NotificationDefaults.DefaultLimit,
        CancellationToken ct = default)
    {
        var notifications = await _notifications.GetForUserAsync(_currentUser.UserId, limit, ct);

        return Ok(new ApiResponse<IReadOnlyList<NotificationResponse>>(
            true, "Notifications retrieved.", notifications));
    }
}
