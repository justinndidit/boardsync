using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Services;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.Sprints.Controllers;

/// <summary>
/// Sprint lifecycle and backlog management scoped to a team.
/// Read operations:      sprint:read
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
    [RequirePermission(Permissions.SprintRead, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<SprintSummaryResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetForTeam(
        Guid teamId,
        [FromQuery] PaginationQuery pagination,
        CancellationToken ct)
    {
        var result = await _sprintService.GetForTeamAsync(teamId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<SprintSummaryResponse>>(true, "Sprints retrieved.", result));
    }

    /// <summary>Get the currently active sprint for a team. Returns null data if none is active.</summary>
    [HttpGet("api/teams/{teamId:guid}/sprints/active")]
    [RequirePermission(Permissions.SprintRead, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse<SprintResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActive(Guid teamId, CancellationToken ct)
    {
        var sprint = await _sprintService.GetActiveForTeamAsync(teamId, ct);
        return Ok(new ApiResponse<SprintResponse?>(true,
            sprint is null ? "No active sprint." : "Active sprint retrieved.", sprint));
    }

    /// <summary>Get a sprint by ID.</summary>
    [HttpGet("api/sprints/{sprintId:guid}")]
    [RequirePermission(Permissions.SprintRead, From = "sprintId")]
    [ProducesResponseType(typeof(ApiResponse<SprintResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid sprintId, CancellationToken ct)
    {
        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);
        return Ok(new ApiResponse<SprintResponse>(true, "Sprint retrieved.", sprint));
    }

    /// <summary>Create a new sprint for a team. Requires <c>sprint:manage</c>.</summary>
    [HttpPost("api/teams/{teamId:guid}/sprints")]
    [RequirePermission(Permissions.SprintManage, From = "teamId")]
    [ProducesResponseType(typeof(ApiResponse<SprintResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(
        Guid teamId,
        [FromBody] CreateSprintRequest request,
        CancellationToken ct)
    {
        var sprint = await _sprintService.CreateAsync(teamId, request, _currentUser.UserId, ct);
        return CreatedAtAction(nameof(GetById), new { sprintId = sprint.Id },
            new ApiResponse<SprintResponse>(true, "Sprint created.", sprint));
    }

    /// <summary>Update a sprint's goal and dates. Only allowed while Planning. Requires <c>sprint:manage</c>.</summary>
    [HttpPut("api/sprints/{sprintId:guid}")]
    [RequirePermission(Permissions.SprintManage, From = "sprintId")]
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
        var updated = await _sprintService.UpdateAsync(sprintId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<SprintResponse>(true, "Sprint updated.", updated));
    }

    /// <summary>
    /// Transition sprint status: Planning → Active → Completed.
    /// Only one Active sprint per team is allowed at a time.
    /// Requires <c>sprint:manage</c>.
    /// </summary>
    [HttpPatch("api/sprints/{sprintId:guid}/status")]
    [RequirePermission(Permissions.SprintManage, From = "sprintId")]
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
        var updated = await _sprintService.UpdateStatusAsync(sprintId, request.Status, _currentUser.UserId, ct);
        return Ok(new ApiResponse<SprintResponse>(true, $"Sprint status updated to {request.Status}.", updated));
    }

    /// <summary>Delete a Planning sprint with no work items. Requires <c>sprint:manage</c>.</summary>
    [HttpDelete("api/sprints/{sprintId:guid}")]
    [RequirePermission(Permissions.SprintManage, From = "sprintId")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid sprintId, CancellationToken ct)
    {
        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);
        await _sprintService.DeleteAsync(sprintId, _currentUser.UserId, ct);
        return NoContent();
    }

    /// <summary>
    /// Close an active sprint. Incomplete items (not Resolved or Closed) are either
    /// returned to the project backlog or moved to a specified next sprint.
    /// Requires <c>sprint:manage</c>.
    /// </summary>
    [HttpPost("api/sprints/{sprintId:guid}/close")]
    [RequirePermission(Permissions.SprintManage, From = "sprintId")]
    [ProducesResponseType(typeof(ApiResponse<CloseSprintResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Close(
        Guid sprintId,
        [FromBody] CloseSprintRequest request,
        CancellationToken ct)
    {
        var result = await _sprintService.CloseAsync(sprintId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<CloseSprintResponse>(true,
            $"Sprint closed. {result.CompletedItemCount} completed, {result.IncompleteItemCount} returned.", result));
    }

    // ── Backlog ───────────────────────────────────────────────────────────────

    /// <summary>List work items in a sprint ordered by position.</summary>
    [HttpGet("api/sprints/{sprintId:guid}/workitems")]
    [RequirePermission(Permissions.SprintRead, From = "sprintId")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<SprintWorkItemResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWorkItems(
        Guid sprintId,
        [FromQuery] PaginationQuery pagination,
        CancellationToken ct)
    {
        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);
        var result = await _sprintService.GetWorkItemsAsync(sprintId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<SprintWorkItemResponse>>(true, "Sprint backlog retrieved.", result));
    }

    /// <summary>Add a work item to the sprint backlog. Requires <c>sprint:scope</c>.</summary>
    [HttpPost("api/sprints/{sprintId:guid}/workitems")]
    [PermissionCheckedInAction(
        "sprint:scope, unless the item decomposes work already in the sprint — depends on the item, not the caller alone.")]
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
        await RequireSprintScopeAsync(sprint.TeamId, sprintId, request.WorkItemId, ct);
        var item = await _sprintService.AddWorkItemAsync(sprintId, request, _currentUser.UserId, ct);
        return StatusCode(StatusCodes.Status201Created,
            new ApiResponse<SprintWorkItemResponse>(true, "Work item added to sprint.", item));
    }

    /// <summary>Remove a work item from the sprint backlog. Requires <c>sprint:scope</c>.</summary>
    [HttpDelete("api/sprints/{sprintId:guid}/workitems/{workItemId:guid}")]
    [PermissionCheckedInAction(
        "sprint:scope, unless the item decomposes work already in the sprint.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveWorkItem(
        Guid sprintId,
        Guid workItemId,
        CancellationToken ct)
    {
        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);
        await RequireSprintScopeAsync(sprint.TeamId, sprintId, workItemId, ct);
        await _sprintService.RemoveWorkItemAsync(sprintId, workItemId, _currentUser.UserId, ct);
        return NoContent();
    }

    /// <summary>
    /// Move one backlog item between two neighbours. Requires <c>sprint:order</c>.
    /// </summary>
    /// <remarks>
    /// The drag-and-drop endpoint. Names only the card that moved and where it landed, so two
    /// people rearranging different cards write different rows and cannot revert each other —
    /// unlike the whole-list reorder below, which submits an entire ordering.
    /// Omit <c>afterWorkItemId</c> to move to the top, or <c>beforeWorkItemId</c> to move to the end.
    /// </remarks>
    [HttpPatch("api/sprints/{sprintId:guid}/workitems/{workItemId:guid}/move")]
    [RequirePermission(Permissions.SprintOrder, From = "sprintId")]
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

        var rank = await _sprintService.MoveWorkItemAsync(sprintId, workItemId, request, ct);

        return Ok(new ApiResponse<MoveSprintWorkItemResponse>(
            true, "Work item moved.", new MoveSprintWorkItemResponse(workItemId, rank)));
    }

    /// <summary>
    /// Reorder the whole sprint backlog. Requires <c>sprint:order</c>.
    /// </summary>
    /// <remarks>
    /// Last-writer-wins across every item: it submits an ordering computed before any concurrent
    /// move existed, so a second caller silently reverts the first. Fine for a single editor;
    /// prefer the move endpoint above wherever more than one person can drag at once.
    /// </remarks>
    [HttpPatch("api/sprints/{sprintId:guid}/workitems/reorder")]
    [RequirePermission(Permissions.SprintOrder, From = "sprintId")]
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
        await _sprintService.ReorderWorkItemsAsync(sprintId, request, ct);
        return Ok(new ApiResponse(true, "Sprint backlog reordered."));
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Guards changing what a sprint contains, allowing a team member to decompose work that is
    /// already committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is in a sprint is a commitment, so it belongs to <see cref="Permissions.SprintScope"/> —
    /// the Product Owner, Scrum Master, Team Lead or an org admin. Breaking committed work down is
    /// not a commitment, though: if the parent is already in the sprint, adding a child changes
    /// nothing about what the team promised, and gating that on the Product Owner would put them in
    /// the middle of ordinary task breakdown.
    /// </para>
    /// <para>
    /// A work item with no parent, or whose parent is not in this sprint, is new scope and needs the
    /// permission. That includes a bug found mid-sprint — which is deliberate, if debatable: an
    /// unplanned bug genuinely does change the commitment. Exempting a type is easy to add later and
    /// hard to remove once people rely on it.
    /// </para>
    /// </remarks>
    private async Task RequireSprintScopeAsync(
        Guid teamId, Guid sprintId, Guid workItemId, CancellationToken ct)
    {
        if (await _rbac.HasPermissionAsync(
                _currentUser.UserId, Permissions.SprintScope, RoleScope.Team, teamId, ct))
            return;

        if (!await _rbac.HasPermissionAsync(
                _currentUser.UserId, Permissions.SprintOrder, RoleScope.Team, teamId, ct))
            throw new ForbiddenException();

        if (!await _sprintService.IsDecompositionOfSprintWorkAsync(sprintId, workItemId, ct))
            throw new ForbiddenException(
                "Changing what a sprint commits to requires the Product Owner, Scrum Master or Team Lead. " +
                "Breaking down work already in the sprint does not.");
    }

    private async Task RequireTeamAsync(Guid teamId, string permission, CancellationToken ct)
    {
        if (!await _rbac.HasPermissionAsync(_currentUser.UserId, permission, RoleScope.Team, teamId, ct))
            throw new ForbiddenException();
    }
}
