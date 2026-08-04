using BoardSync.Api.Data;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.DTOs;
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

    public WorkspaceController(BoardSyncDbContext context, ICurrentUserContext currentUser)
    {
        _context = context;
        _currentUser = currentUser;
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
    /// Recent activity feed for the current user's workspace cards.
    /// Returns the 30 most recent work item history entries across all accessible projects,
    /// formatted as human-readable activity entries.
    /// </summary>
    [HttpGet("activity")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<WorkspaceActivityResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActivity(CancellationToken ct)
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

        var history = await _context.WorkItemHistory
            .Where(h => projectIds.Contains(h.WorkItem.ProjectId))
            .OrderByDescending(h => h.CreatedAt)
            .Take(30)
            .Select(h => new
            {
                h.Id,
                h.FieldName,
                h.OldValue,
                h.NewValue,
                h.ChangedBy,
                h.CreatedAt,
                WorkItemTitle = h.WorkItem.Title,
                WorkItemProjectId = h.WorkItem.ProjectId
            })
            .ToListAsync(ct);

        // Resolve actor display names and project/org names in memory
        var actorIds = history.Select(h => h.ChangedBy).Distinct().ToList();
        var actorMap = await _context.Users
            .Where(u => actorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var histProjectIds = history.Select(h => h.WorkItemProjectId).Distinct().ToList();
        var projectMap = await _context.Projects
            .Where(p => histProjectIds.Contains(p.Id))
            .Select(p => new { p.Id, ProjectName = p.Name, OrgName = p.Organization.Name })
            .ToDictionaryAsync(p => p.Id, ct);

        var result = history.Select(h =>
        {
            actorMap.TryGetValue(h.ChangedBy, out var actor);
            projectMap.TryGetValue(h.WorkItemProjectId, out var proj);

            var type = h.FieldName == "State" ? $"WorkItem{h.NewValue}" : "WorkItemUpdated";
            var detail = h.OldValue != null
                ? $"{h.FieldName}: {h.OldValue} → {h.NewValue}"
                : $"{h.FieldName} set to {h.NewValue}";

            return new WorkspaceActivityResponse(
                h.Id,
                type,
                h.WorkItemTitle,
                detail,
                actor ?? "Unknown",
                proj?.OrgName ?? string.Empty,
                proj?.ProjectName ?? string.Empty,
                h.CreatedAt
            );
        }).ToList();

        return Ok(new ApiResponse<IReadOnlyList<WorkspaceActivityResponse>>(true, "Activity retrieved.", result));
    }
}
