using BoardSync.Api.Modules.Backlog.DTOs;
using BoardSync.Api.Modules.Backlog.Services;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.Backlog.Controllers;

/// <summary>
/// Product backlog management scoped to a project.
///
/// Read:            Reader+
/// Reorder / move:  TeamMember+
/// Add / remove:    TeamMember+
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
public class BacklogController : ControllerBase
{
    private readonly IBacklogService _backlogService;
    private readonly IRbacService _rbac;
    private readonly ICurrentUserContext _currentUser;

    public BacklogController(
        IBacklogService backlogService,
        IRbacService rbac,
        ICurrentUserContext currentUser)
    {
        _backlogService = backlogService;
        _rbac = rbac;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Get the unscheduled product backlog for a project, ordered by rank.
    /// Optionally filter to a specific team with ?teamId=.
    /// Requires Reader.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/backlog")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<BacklogItemResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetForProject(
        Guid projectId,
        [FromQuery] Guid? teamId,
        [FromQuery] PaginationQuery pagination,
        CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.Reader, ct);
        var result = await _backlogService.GetForProjectAsync(projectId, teamId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<BacklogItemResponse>>(true, "Backlog retrieved.", result));
    }

    /// <summary>
    /// Add a work item to the project backlog.
    /// Idempotent — returns the existing entry if the item is already tracked.
    /// Requires TeamMember.
    /// </summary>
    [HttpPost("api/projects/{projectId:guid}/backlog")]
    [ProducesResponseType(typeof(ApiResponse<BacklogItemResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Add(
        Guid projectId,
        [FromBody] AddToBacklogRequest request,
        CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.TeamMember, ct);
        var item = await _backlogService.AddAsync(projectId, request, _currentUser.UserId, ct);
        return StatusCode(StatusCodes.Status201Created,
            new ApiResponse<BacklogItemResponse>(true, "Item added to backlog.", item));
    }

    /// <summary>
    /// Remove a work item from the project backlog.
    /// Requires TeamMember.
    /// </summary>
    [HttpDelete("api/projects/{projectId:guid}/backlog/{workItemId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(
        Guid projectId,
        Guid workItemId,
        CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.TeamMember, ct);
        await _backlogService.RemoveAsync(projectId, workItemId, ct);
        return NoContent();
    }

    /// <summary>
    /// Reorder the backlog by providing the desired work item ID sequence.
    /// Items not included are pushed to the bottom in their original relative order.
    /// Requires TeamMember.
    /// </summary>
    [HttpPatch("api/projects/{projectId:guid}/backlog/reorder")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reorder(
        Guid projectId,
        [FromBody] ReorderBacklogRequest request,
        CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.TeamMember, ct);
        await _backlogService.ReorderAsync(projectId, request, ct);
        return Ok(new ApiResponse(true, "Backlog reordered."));
    }

    /// <summary>
    /// Move one or more backlog items into a sprint.
    /// Creates SprintWorkItem entries so the sprint board reflects the change immediately.
    /// Requires TeamMember.
    /// </summary>
    [HttpPost("api/projects/{projectId:guid}/backlog/move-to-sprint")]
    [ProducesResponseType(typeof(ApiResponse<BacklogBulkOperationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> MoveToSprint(
        Guid projectId,
        [FromBody] MoveToSprintRequest request,
        CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.TeamMember, ct);
        var result = await _backlogService.MoveToSprintAsync(projectId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<BacklogBulkOperationResponse>(true, result.Message, result));
    }

    /// <summary>
    /// Return sprint items back to the unscheduled backlog.
    /// Removes SprintWorkItem rows and clears the sprint assignment.
    /// Requires TeamMember.
    /// </summary>
    [HttpPost("api/projects/{projectId:guid}/backlog/return-from-sprint")]
    [ProducesResponseType(typeof(ApiResponse<BacklogBulkOperationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReturnToBacklog(
        Guid projectId,
        [FromBody] ReturnToBacklogRequest request,
        CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.TeamMember, ct);
        var result = await _backlogService.ReturnToBacklogAsync(projectId, request, ct);
        return Ok(new ApiResponse<BacklogBulkOperationResponse>(true, result.Message, result));
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task RequireProjectRoleAsync(Guid projectId, RoleType minimum, CancellationToken ct)
    {
        if (!await _rbac.HasRoleAsync(_currentUser.UserId, minimum, RoleScope.Project, projectId, ct))
            throw new ForbiddenException();
    }
}
