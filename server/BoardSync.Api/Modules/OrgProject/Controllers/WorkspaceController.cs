using BoardSync.Api.Modules.Activity.DTOs;
using BoardSync.Api.Modules.Activity.Services;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Kernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.OrgProject.Controllers;

/// <summary>
/// Workspace-level aggregates: dashboard summary and activity feed, both scoped to the
/// organizations the calling user belongs to.
/// </summary>
/// <remarks>
/// The notification bell used to live here too. It now belongs to the Notifications module and is
/// served by <c>NotificationsController</c>, which still answers on
/// <c>GET /api/workspace/notifications</c> as well as its own route.
/// </remarks>
[ApiController]
[Route("api/workspace")]
[Authorize]
[Produces("application/json")]
public class WorkspaceController : ControllerBase
{
    private readonly IWorkspaceService _workspace;
    private readonly ICurrentUserContext _currentUser;
    private readonly IActivityQueryService _activity;

    public WorkspaceController(
        IWorkspaceService workspace,
        ICurrentUserContext currentUser,
        IActivityQueryService activity)
    {
        _workspace = workspace;
        _currentUser = currentUser;
        _activity = activity;
    }

    /// <summary>
    /// Aggregate counts for the current user's workspace dashboard.
    /// Returns organization count, project count, total member count across all orgs,
    /// and the number of active (non-closed) work items across all projects.
    /// </summary>
    [HttpGet("summary")]
    [NoPermissionRequired(
        "Aggregates only over the organizations the caller belongs to; the query is scoped to their id.")]
    [ProducesResponseType(typeof(ApiResponse<WorkspaceSummaryResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var summary = await _workspace.GetSummaryAsync(_currentUser.UserId, ct);

        return Ok(new ApiResponse<WorkspaceSummaryResponse>(true, "Workspace summary retrieved.", summary));
    }

    /// <summary>
    /// Everything that has happened across every organization the caller belongs to, newest first:
    /// work item, project, team, sprint and board changes, plus membership and role changes.
    /// </summary>
    /// <remarks>
    /// Identical in shape to <c>/api/orgs/{orgId}/activity</c> — the only difference is that this
    /// one spans all the caller's organizations rather than one, so entries carry the organization
    /// they came from.
    /// </remarks>
    [HttpGet("activity")]
    [NoPermissionRequired(
        "Reads activity only for the organizations the caller belongs to; the query is scoped to their id.")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ActivityResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActivity([FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        var orgIds = await _workspace.GetOrganizationIdsAsync(_currentUser.UserId, ct);

        var result = await _activity.GetForOrganizationsAsync(orgIds, pagination, ct);

        return Ok(new ApiResponse<PagedResult<ActivityResponse>>(true, "Activity retrieved.", result));
    }
}
