using BoardSync.Api.Data;
using BoardSync.Api.Modules.Activity.DTOs;
using BoardSync.Api.Modules.Activity.Services;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Kernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.OrgProject.Controllers;

/// <summary>
/// Workspace-level aggregates: dashboard summary, notification bell, and activity feed.
/// All data is scoped to the organizations the calling user belongs to.
/// </summary>
[ApiController]
[Route("api/workspace")]
[Authorize]
[Produces("application/json")]
public class WorkspaceController : ControllerBase
{
    private readonly BoardSyncDbContext _context;
    private readonly ICurrentUserContext _currentUser;
    private readonly IActivityQueryService _activity;

    public WorkspaceController(
        BoardSyncDbContext context,
        ICurrentUserContext currentUser,
        IActivityQueryService activity)
    {
        _context = context;
        _currentUser = currentUser;
        _activity = activity;
    }

    /// <summary>
    /// Aggregate counts for the current user's workspace dashboard.
    /// Returns organization count, project count, total member count across all orgs,
    /// and the number of active (non-closed) work items across all projects.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(ApiResponse<WorkspaceSummaryResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var userId = _currentUser.UserId;

        // Orgs the user belongs to
        var orgIds = await _context.OrganizationMemberships
            .Where(m => m.UserId == userId)
            .Select(m => m.OrganizationId)
            .ToListAsync(ct);

        var organizations = orgIds.Count;

        // Active projects inside those orgs
        var projects = await _context.Projects
            .Where(p => orgIds.Contains(p.OrganizationId) && p.IsActive)
            .CountAsync(ct);

        // Total unique members across those orgs
        var members = await _context.OrganizationMemberships
            .Where(m => orgIds.Contains(m.OrganizationId))
            .Select(m => m.UserId)
            .Distinct()
            .CountAsync(ct);

        // Active work items across all projects in those orgs (excludes Closed/Resolved)
        var projectIds = await _context.Projects
            .Where(p => orgIds.Contains(p.OrganizationId) && p.IsActive)
            .Select(p => p.Id)
            .ToListAsync(ct);

        var activeWorkItems = await _context.WorkItems
            .Where(w => projectIds.Contains(w.ProjectId)
                        && w.IsActive
                        && w.State != WorkItemState.Closed
                        && w.State != WorkItemState.Resolved)
            .CountAsync(ct);

        var summary = new WorkspaceSummaryResponse(organizations, projects, members, activeWorkItems);
        return Ok(new ApiResponse<WorkspaceSummaryResponse>(true, "Workspace summary retrieved.", summary));
    }

    /// <summary>
    /// Recent notifications for the current user's workspace bell.
    /// Returns the 20 most recent work item changes across all projects the user has access to,
    /// ordered by most recent first.
    /// </summary>
    [HttpGet("notifications")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<WorkspaceNotificationResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNotifications(CancellationToken ct)
    {
        var userId = _currentUser.UserId;

        var orgIds = await _context.OrganizationMemberships
            .Where(m => m.UserId == userId)
            .Select(m => m.OrganizationId)
            .ToListAsync(ct);

        var projectIds = await _context.Projects
            .Where(p => orgIds.Contains(p.OrganizationId) && p.IsActive)
            .Select(p => p.Id)
            .ToListAsync(ct);

        // Build a notification from each recent work item history entry
        var notifications = await _context.WorkItemHistory
            .Where(h => projectIds.Contains(h.WorkItem.ProjectId))
            .OrderByDescending(h => h.CreatedAt)
            .Take(20)
            .Select(h => new
            {
                h.Id,
                h.FieldName,
                h.NewValue,
                WorkItemTitle = h.WorkItem.Title,
                WorkItemProjectId = h.WorkItem.ProjectId,
                h.CreatedAt
            })
            .ToListAsync(ct);

        // Resolve org names in memory (avoid complex join)
        var notifProjectIds = notifications.Select(n => n.WorkItemProjectId).Distinct().ToList();
        var orgNameMap = await _context.Projects
            .Where(p => notifProjectIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Organization.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var result = notifications.Select(n =>
        {
            var type = n.FieldName == "State" ? $"WorkItem{n.NewValue}" : "WorkItemUpdated";
            var title = $"{n.WorkItemTitle} — {n.FieldName} changed to {n.NewValue}";
            orgNameMap.TryGetValue(n.WorkItemProjectId, out var orgName);
            return new WorkspaceNotificationResponse(n.Id, type, title, orgName ?? string.Empty, n.CreatedAt);
        }).ToList();

        return Ok(new ApiResponse<IReadOnlyList<WorkspaceNotificationResponse>>(true, "Notifications retrieved.", result));
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
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ActivityResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActivity([FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        var orgIds = await _context.OrganizationMemberships
            .Where(m => m.UserId == _currentUser.UserId)
            .Select(m => m.OrganizationId)
            .ToListAsync(ct);

        var result = await _activity.GetForOrganizationsAsync(orgIds, pagination, ct);

        return Ok(new ApiResponse<PagedResult<ActivityResponse>>(true, "Activity retrieved.", result));
    }
}
