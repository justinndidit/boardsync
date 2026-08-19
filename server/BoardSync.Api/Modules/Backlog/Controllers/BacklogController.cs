using BoardSync.Api.Modules.Backlog.DTOs;
using BoardSync.Api.Modules.Backlog.Services;
using BoardSync.Api.Modules.Sprints.Services;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.Backlog.Controllers;

/// <summary>
/// Product backlog management scoped to a project.
///
/// Reading and reordering are project-scope questions. Moving items into or out of a sprint is not:
/// that changes what a team has committed to, so it is checked against the sprint's team with
/// <c>sprint:scope</c>, exactly as the equivalent endpoints on SprintsController are.
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
public class BacklogController : ControllerBase
{
    private readonly IBacklogService _backlogService;
    private readonly ISprintService _sprintService;
    private readonly IRbacService _rbac;
    private readonly ICurrentUserContext _currentUser;

    public BacklogController(
        IBacklogService backlogService,
        ISprintService sprintService,
        IRbacService rbac,
        ICurrentUserContext currentUser)
    {
        _backlogService = backlogService;
        _sprintService = sprintService;
        _rbac = rbac;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Get the unscheduled product backlog for a project, ordered by rank.
    /// Optionally filter to a specific team with ?teamId=.
    /// Requires <c>workitem:read</c>.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/backlog")]
    [RequirePermission(Permissions.WorkItemRead, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<BacklogItemResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetForProject(
        Guid projectId,
        [FromQuery] Guid? teamId,
        [FromQuery] PaginationQuery pagination,
        CancellationToken ct)
    {
        var result = await _backlogService.GetForProjectAsync(projectId, teamId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<BacklogItemResponse>>(true, "Backlog retrieved.", result));
    }

    /// <summary>
    /// Add a work item to the project backlog.
    /// Idempotent — returns the existing entry if the item is already tracked.
    /// Requires <c>workitem:write</c>.
    /// </summary>
    [HttpPost("api/projects/{projectId:guid}/backlog")]
    [RequirePermission(Permissions.WorkItemWrite, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<BacklogItemResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Add(
        Guid projectId,
        [FromBody] AddToBacklogRequest request,
        CancellationToken ct)
    {
        var item = await _backlogService.AddAsync(projectId, request, _currentUser.UserId, ct);
        return StatusCode(StatusCodes.Status201Created,
            new ApiResponse<BacklogItemResponse>(true, "Item added to backlog.", item));
    }

    /// <summary>
    /// Remove a work item from the project backlog.
    /// Requires <c>workitem:write</c>.
    /// </summary>
    [HttpDelete("api/projects/{projectId:guid}/backlog/{workItemId:guid}")]
    [RequirePermission(Permissions.WorkItemWrite, From = "projectId")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(
        Guid projectId,
        Guid workItemId,
        CancellationToken ct)
    {
        await _backlogService.RemoveAsync(projectId, workItemId, ct);
        return NoContent();
    }

    /// <summary>
    /// Reorder the backlog by providing the desired work item ID sequence.
    /// Items not included are pushed to the bottom in their original relative order.
    /// Requires <c>workitem:write</c>.
    /// </summary>
    [HttpPatch("api/projects/{projectId:guid}/backlog/reorder")]
    [RequirePermission(Permissions.WorkItemWrite, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reorder(
        Guid projectId,
        [FromBody] ReorderBacklogRequest request,
        CancellationToken ct)
    {
        await _backlogService.ReorderAsync(projectId, request, ct);
        return Ok(new ApiResponse(true, "Backlog reordered."));
    }

    /// <summary>
    /// Move one or more backlog items into a sprint.
    /// Creates SprintWorkItem entries so the sprint board reflects the change immediately.
    /// Requires <c>workitem:write</c>.
    /// </summary>
    [HttpPost("api/projects/{projectId:guid}/backlog/move-to-sprint")]
    [PermissionCheckedInAction(
        "sprint:scope against the target sprint's team, which is not the project in the route.")]
    [ProducesResponseType(typeof(ApiResponse<BacklogBulkOperationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> MoveToSprint(
        Guid projectId,
        [FromBody] MoveToSprintRequest request,
        CancellationToken ct)
    {
        await RequireSprintScopeAsync(projectId, request.SprintId, ct);
        var result = await _backlogService.MoveToSprintAsync(projectId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<BacklogBulkOperationResponse>(true, result.Message, result));
    }

    /// <summary>
    /// Return sprint items back to the unscheduled backlog.
    /// Removes SprintWorkItem rows and clears the sprint assignment.
    /// Requires <c>workitem:write</c>.
    /// </summary>
    [HttpPost("api/projects/{projectId:guid}/backlog/return-from-sprint")]
    [PermissionCheckedInAction(
        "sprint:scope against the target sprint's team, which is not the project in the route.")]
    [ProducesResponseType(typeof(ApiResponse<BacklogBulkOperationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReturnToBacklog(
        Guid projectId,
        [FromBody] ReturnToBacklogRequest request,
        CancellationToken ct)
    {
        await RequireSprintScopeAsync(projectId, request.SprintId, ct);
        var result = await _backlogService.ReturnToBacklogAsync(projectId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<BacklogBulkOperationResponse>(true, result.Message, result));
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Guards changing what a sprint contains, from the backlog side.
    /// </summary>
    /// <remarks>
    /// The route names a project, but committing work is a team-level decision, so the sprint is
    /// resolved first and its team is what the permission is checked against. Without this the
    /// backlog would be a way around the rule that the sprint's own endpoints enforce — the same
    /// authority, reachable through a different door.
    /// </remarks>
    private async Task RequireSprintScopeAsync(Guid projectId, Guid sprintId, CancellationToken ct)
    {
        // The route's project is checked too. These endpoints carry no [RequirePermission] because
        // the sprint is the real subject, which would otherwise leave the project id in the route
        // entirely unverified.
        if (!await _rbac.HasPermissionAsync(
                _currentUser.UserId, Permissions.WorkItemWrite, RoleScope.Project, projectId, ct))
            throw new ForbiddenException();

        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);

        if (await _rbac.HasPermissionAsync(
                _currentUser.UserId, Permissions.SprintScope, RoleScope.Team, sprint.ProjectId, ct))
            return;

        // Same split the endpoint filter applies: a caller who cannot even see this sprint's team
        // gets the answer they would get for a sprint that does not exist, so the status code does
        // not confirm one belonging to somebody else is real.
        if (!await _rbac.HasPermissionAsync(
                _currentUser.UserId, Permissions.SprintRead, RoleScope.Team, sprint.ProjectId, ct))
            throw new NotFoundException("Sprint", sprintId);

        throw new ForbiddenException(
            "Changing what a sprint commits to requires the Product Owner, Scrum Master or Team Lead.");
    }
}
