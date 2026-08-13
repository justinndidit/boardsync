using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Services;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.Sprints.Controllers;

/// <summary>
/// Sprint lifecycle and backlog management scoped to a team.
/// Read operations:      Reader+
/// Sprint management:    ProjectAdmin+
/// Backlog management:   TeamMember+
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
public class SprintsController : ControllerBase
{
    private readonly ISprintService _sprintService;
    private readonly IRbacService _rbac;
    private readonly ICurrentUserContext _currentUser;

    public SprintsController(
        ISprintService sprintService,
        IRbacService rbac,
        ICurrentUserContext currentUser)
    {
        _sprintService = sprintService;
        _rbac = rbac;
        _currentUser = currentUser;
    }

    // ── Sprint CRUD ───────────────────────────────────────────────────────────

    /// <summary>List all sprints for a team, newest first.</summary>
    [HttpGet("api/teams/{teamId:guid}/sprints")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<SprintSummaryResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetForTeam(
        Guid teamId,
        [FromQuery] PaginationQuery pagination,
        CancellationToken ct)
    {
        await RequireTeamRoleAsync(teamId, RoleType.Reader, ct);
        var result = await _sprintService.GetForTeamAsync(teamId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<SprintSummaryResponse>>(true, "Sprints retrieved.", result));
    }

    /// <summary>Get the currently active sprint for a team. Returns null data if none is active.</summary>
    [HttpGet("api/teams/{teamId:guid}/sprints/active")]
    [ProducesResponseType(typeof(ApiResponse<SprintResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActive(Guid teamId, CancellationToken ct)
    {
        await RequireTeamRoleAsync(teamId, RoleType.Reader, ct);
        var sprint = await _sprintService.GetActiveForTeamAsync(teamId, ct);
        return Ok(new ApiResponse<SprintResponse?>(true,
            sprint is null ? "No active sprint." : "Active sprint retrieved.", sprint));
    }

    /// <summary>Get a sprint by ID.</summary>
    [HttpGet("api/sprints/{sprintId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<SprintResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid sprintId, CancellationToken ct)
    {
        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);
        await RequireTeamRoleAsync(sprint.TeamId, RoleType.Reader, ct);
        return Ok(new ApiResponse<SprintResponse>(true, "Sprint retrieved.", sprint));
    }

    /// <summary>Create a new sprint for a team. Requires ProjectAdmin.</summary>
    [HttpPost("api/teams/{teamId:guid}/sprints")]
    [ProducesResponseType(typeof(ApiResponse<SprintResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(
        Guid teamId,
        [FromBody] CreateSprintRequest request,
        CancellationToken ct)
    {
        await RequireTeamRoleAsync(teamId, RoleType.ProjectAdmin, ct);
        var sprint = await _sprintService.CreateAsync(teamId, request, _currentUser.UserId, ct);
        return CreatedAtAction(nameof(GetById), new { sprintId = sprint.Id },
            new ApiResponse<SprintResponse>(true, "Sprint created.", sprint));
    }

    /// <summary>Update a sprint's goal and dates. Only allowed while Planning. Requires ProjectAdmin.</summary>
    [HttpPut("api/sprints/{sprintId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<SprintResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid sprintId,
        [FromBody] UpdateSprintRequest request,
        CancellationToken ct)
    {
        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);
        await RequireTeamRoleAsync(sprint.TeamId, RoleType.ProjectAdmin, ct);
        var updated = await _sprintService.UpdateAsync(sprintId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<SprintResponse>(true, "Sprint updated.", updated));
    }

    /// <summary>
    /// Transition sprint status: Planning → Active → Completed.
    /// Only one Active sprint per team is allowed at a time.
    /// Requires ProjectAdmin.
    /// </summary>
    [HttpPatch("api/sprints/{sprintId:guid}/status")]
    [ProducesResponseType(typeof(ApiResponse<SprintResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStatus(
        Guid sprintId,
        [FromBody] UpdateSprintStatusRequest request,
        CancellationToken ct)
    {
        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);
        await RequireTeamRoleAsync(sprint.TeamId, RoleType.ProjectAdmin, ct);
        var updated = await _sprintService.UpdateStatusAsync(sprintId, request.Status, _currentUser.UserId, ct);
        return Ok(new ApiResponse<SprintResponse>(true, $"Sprint status updated to {request.Status}.", updated));
    }

    /// <summary>Delete a Planning sprint with no work items. Requires ProjectAdmin.</summary>
    [HttpDelete("api/sprints/{sprintId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid sprintId, CancellationToken ct)
    {
        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);
        await RequireTeamRoleAsync(sprint.TeamId, RoleType.ProjectAdmin, ct);
        await _sprintService.DeleteAsync(sprintId, _currentUser.UserId, ct);
        return NoContent();
    }

    // ── Backlog ───────────────────────────────────────────────────────────────

    /// <summary>List work items in a sprint ordered by position.</summary>
    [HttpGet("api/sprints/{sprintId:guid}/workitems")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<SprintWorkItemResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWorkItems(
        Guid sprintId,
        [FromQuery] PaginationQuery pagination,
        CancellationToken ct)
    {
        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);
        await RequireTeamRoleAsync(sprint.TeamId, RoleType.Reader, ct);
        var result = await _sprintService.GetWorkItemsAsync(sprintId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<SprintWorkItemResponse>>(true, "Sprint backlog retrieved.", result));
    }

    /// <summary>Add a work item to the sprint backlog. Requires TeamMember.</summary>
    [HttpPost("api/sprints/{sprintId:guid}/workitems")]
    [ProducesResponseType(typeof(ApiResponse<SprintWorkItemResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddWorkItem(
        Guid sprintId,
        [FromBody] AddSprintWorkItemRequest request,
        CancellationToken ct)
    {
        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);
        await RequireTeamRoleAsync(sprint.TeamId, RoleType.TeamMember, ct);
        var item = await _sprintService.AddWorkItemAsync(sprintId, request, _currentUser.UserId, ct);
        return StatusCode(StatusCodes.Status201Created,
            new ApiResponse<SprintWorkItemResponse>(true, "Work item added to sprint.", item));
    }

    /// <summary>Remove a work item from the sprint backlog. Requires TeamMember.</summary>
    [HttpDelete("api/sprints/{sprintId:guid}/workitems/{workItemId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveWorkItem(
        Guid sprintId,
        Guid workItemId,
        CancellationToken ct)
    {
        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);
        await RequireTeamRoleAsync(sprint.TeamId, RoleType.TeamMember, ct);
        await _sprintService.RemoveWorkItemAsync(sprintId, workItemId, _currentUser.UserId, ct);
        return NoContent();
    }

    /// <summary>
    /// Move one backlog item between two neighbours. Requires TeamMember.
    /// </summary>
    /// <remarks>
    /// The drag-and-drop endpoint. Names only the card that moved and where it landed, so two
    /// people rearranging different cards write different rows and cannot revert each other —
    /// unlike the whole-list reorder below, which submits an entire ordering.
    /// Omit <c>afterWorkItemId</c> to move to the top, or <c>beforeWorkItemId</c> to move to the end.
    /// </remarks>
    [HttpPatch("api/sprints/{sprintId:guid}/workitems/{workItemId:guid}/move")]
    [ProducesResponseType(typeof(ApiResponse<MoveSprintWorkItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MoveWorkItem(
        Guid sprintId,
        Guid workItemId,
        [FromBody] MoveSprintWorkItemRequest request,
        CancellationToken ct)
    {
        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);
        await RequireTeamRoleAsync(sprint.TeamId, RoleType.TeamMember, ct);

        var rank = await _sprintService.MoveWorkItemAsync(sprintId, workItemId, request, ct);

        return Ok(new ApiResponse<MoveSprintWorkItemResponse>(
            true, "Work item moved.", new MoveSprintWorkItemResponse(workItemId, rank)));
    }

    /// <summary>
    /// Reorder the whole sprint backlog. Requires TeamMember.
    /// </summary>
    /// <remarks>
    /// Last-writer-wins across every item: it submits an ordering computed before any concurrent
    /// move existed, so a second caller silently reverts the first. Fine for a single editor;
    /// prefer the move endpoint above wherever more than one person can drag at once.
    /// </remarks>
    [HttpPatch("api/sprints/{sprintId:guid}/workitems/reorder")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReorderWorkItems(
        Guid sprintId,
        [FromBody] ReorderSprintWorkItemsRequest request,
        CancellationToken ct)
    {
        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);
        await RequireTeamRoleAsync(sprint.TeamId, RoleType.TeamMember, ct);
        await _sprintService.ReorderWorkItemsAsync(sprintId, request, ct);
        return Ok(new ApiResponse(true, "Sprint backlog reordered."));
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task RequireTeamRoleAsync(Guid teamId, RoleType minimum, CancellationToken ct)
    {
        if (!await _rbac.HasRoleAsync(_currentUser.UserId, minimum, RoleScope.Team, teamId, ct))
            throw new ForbiddenException();
    }
}
