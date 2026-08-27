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
/// Sprint lifecycle and backlog management. A sprint belongs to a project, so every check here is
/// against that project.
/// </summary>
/// <remarks>
/// Read:                 <c>sprint:read</c>
/// Lifecycle:            <c>sprint:manage</c>
/// What the sprint holds: <c>sprint:scope</c>, with the decomposition exception below
/// Ordering:             <c>sprint:order</c>
/// </remarks>
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

    /// <summary>List all sprints for a project, newest first.</summary>
    [HttpGet("api/projects/{projectId:guid}/sprints")]
    [RequirePermission(Permissions.SprintRead, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<SprintSummaryResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetForProject(
        Guid projectId,
        [FromQuery] PaginationQuery pagination,
        CancellationToken ct)
    {
        var result = await _sprintService.GetForProjectAsync(projectId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<SprintSummaryResponse>>(true, "Sprints retrieved.", result));
    }

    /// <summary>Get the currently active sprint for a project. Returns null data if none is active.</summary>
    [HttpGet("api/projects/{projectId:guid}/sprints/active")]
    [RequirePermission(Permissions.SprintRead, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<SprintResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActive(Guid projectId, CancellationToken ct)
    {
        var sprint = await _sprintService.GetActiveForProjectAsync(projectId, ct);
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

    /// <summary>Create a new sprint for a project. Requires <c>sprint:manage</c>.</summary>
    [HttpPost("api/projects/{projectId:guid}/sprints")]
    [RequirePermission(Permissions.SprintManage, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<SprintResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(
        Guid projectId,
        [FromBody] CreateSprintRequest request,
        CancellationToken ct)
    {
        var sprint = await _sprintService.CreateAsync(projectId, request, _currentUser.UserId, ct);
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
    /// Only one Active sprint per project is allowed at a time.
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
    /// Close an active sprint. Incomplete items are either returned to the backlog
    /// or moved to a specified next sprint. Requires ProjectAdmin.
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
        var sprint = await _sprintService.GetByIdAsync(sprintId, ct);
        var result = await _sprintService.CloseAsync(sprintId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<CloseSprintResponse>(true,
            $"Sprint closed. {result.CompletedItemCount} completed, {result.IncompleteItemCount} returned.", result));
    }

    // ── Backlog ───────────────────────────────────────────────────────────────

    /// <summary>List work items in a sprint ordered by rank.</summary>
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
        await RequireSprintScopeAsync(sprint.ProjectId, sprintId, request.WorkItemId, ct);
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
        await RequireSprintScopeAsync(sprint.ProjectId, sprintId, workItemId, ct);
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

    /// <summary>Atomically move a sprint work item to a state and rank position.</summary>
    /// <remarks>
    /// <para>Use <c>afterWorkItemId</c> for the item immediately above the destination and
    /// <c>beforeWorkItemId</c> for the item immediately below it. Omit one for the top or bottom.
    /// Both may be null only when this is the sprint's only work item; otherwise the request is
    /// rejected because no unique destination rank can be inferred.</para>
    /// <para>The returned state, rank, and version are authoritative. A stale version or a rank
    /// collision returns a conflict and rolls back the entire command.</para>
    /// </remarks>
    [HttpPatch("api/sprints/{sprintId:guid}/workitems/{workItemId:guid}/move-with-state")]
    [RequirePermission(Permissions.WorkItemWrite, From = "workItemId")]
    [ProducesResponseType(typeof(ApiResponse<MoveWorkItemCommandResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> MoveWorkItemWithState(
        Guid sprintId,
        Guid workItemId,
        [FromBody] MoveWorkItemCommandRequest request,
        CancellationToken ct)
    {
        var result = await _sprintService.MoveWorkItemWithStateAsync(
            sprintId, workItemId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<MoveWorkItemCommandResponse>(true, "Work item moved.", result));
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
    /// Guards changing what a sprint contains, allowing a contributor to decompose work that is
    /// already committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is in a sprint is a commitment, so it belongs to <see cref="Permissions.SprintScope"/> —
    /// a project administrator, or an org admin. Breaking committed work down is not a commitment,
    /// though: if the parent is already in the sprint, adding a child changes nothing about what was
    /// promised, and gating that would put an administrator in the middle of ordinary task
    /// breakdown.
    /// </para>
    /// <para>
    /// A work item with no parent, or whose parent is not in this sprint, is new scope and needs the
    /// permission. That includes a bug found mid-sprint — deliberate, if debatable: an unplanned bug
    /// genuinely does change the commitment.
    /// </para>
    /// </remarks>
    private async Task RequireSprintScopeAsync(Guid projectId, Guid sprintId, Guid workItemId, CancellationToken ct)
    {
        if (await _rbac.HasPermissionAsync(
                _currentUser.UserId, Permissions.SprintScope, RoleScope.Project, projectId, ct))
            return;

        if (!await _rbac.HasPermissionAsync(
                _currentUser.UserId, Permissions.SprintOrder, RoleScope.Project, projectId, ct))
            throw new ForbiddenException();

        if (!await _sprintService.IsDecompositionOfSprintWorkAsync(sprintId, workItemId, ct))
            throw new ForbiddenException(
                "Changing what a sprint commits to requires project administration. " +
                "Breaking down work already in the sprint does not.");
    }
}
