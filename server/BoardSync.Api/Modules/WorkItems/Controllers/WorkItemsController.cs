using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services;
using BoardSync.Api.Modules.WorkItems.DTOs;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Modules.WorkItems.Services;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.WorkItems.Controllers;

/// <summary>
/// Work item CRUD, state transitions, comments, history, and links.
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
public class WorkItemsController : ControllerBase
{
    private readonly IWorkItemService _workItemService;
    private readonly IRbacService _rbac;
    private readonly ICurrentUserContext _currentUser;

    public WorkItemsController(
        IWorkItemService workItemService,
        IRbacService rbac,
        ICurrentUserContext currentUser)
    {
        _workItemService = workItemService;
        _rbac = rbac;
        _currentUser = currentUser;
    }

    // ── Work Item CRUD ────────────────────────────────────────────────────────

    /// <summary>List work items in a project with optional filters.</summary>
    [HttpGet("api/projects/{projectId:guid}/workitems")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<WorkItemSummaryResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetForProject(
        Guid projectId,
        [FromQuery] WorkItemFilterQuery filter,
        CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.Reader, ct);
        var result = await _workItemService.GetForProjectAsync(projectId, filter, ct);
        return Ok(new ApiResponse<PagedResult<WorkItemSummaryResponse>>(true, "Work items retrieved.", result));
    }

    /// <summary>Create a new work item in a project.</summary>
    [HttpPost("api/projects/{projectId:guid}/workitems")]
    [ProducesResponseType(typeof(ApiResponse<WorkItemResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(
        Guid projectId,
        [FromBody] CreateWorkItemRequest request,
        CancellationToken ct)
    {
        await RequireProjectRoleAsync(projectId, RoleType.TeamMember, ct);
        var item = await _workItemService.CreateAsync(projectId, request, _currentUser.UserId, ct);
        return CreatedAtAction(nameof(GetById), new { workItemId = item.Id },
            new ApiResponse<WorkItemResponse>(true, "Work item created.", item));
    }

    /// <summary>Get a work item by ID.</summary>
    [HttpGet("api/workitems/{workItemId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<WorkItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid workItemId, CancellationToken ct)
    {
        var item = await _workItemService.GetByIdAsync(workItemId, ct);
        await RequireProjectRoleAsync(item.ProjectId, RoleType.Reader, ct);
        return Ok(new ApiResponse<WorkItemResponse>(true, "Work item retrieved.", item));
    }

    /// <summary>Update work item fields (title, description, priority, assignee, tags, story points).</summary>
    [HttpPut("api/workitems/{workItemId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<WorkItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid workItemId,
        [FromBody] UpdateWorkItemRequest request,
        CancellationToken ct)
    {
        var item = await _workItemService.GetByIdAsync(workItemId, ct);
        await RequireProjectRoleAsync(item.ProjectId, RoleType.TeamMember, ct);
        var updated = await _workItemService.UpdateAsync(workItemId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<WorkItemResponse>(true, "Work item updated.", updated));
    }

    /// <summary>Transition work item state (New → Active → Resolved → Closed).</summary>
    [HttpPatch("api/workitems/{workItemId:guid}/state")]
    [ProducesResponseType(typeof(ApiResponse<WorkItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateState(
        Guid workItemId,
        [FromBody] UpdateWorkItemStateRequest request,
        CancellationToken ct)
    {
        var item = await _workItemService.GetByIdAsync(workItemId, ct);
        await RequireProjectRoleAsync(item.ProjectId, RoleType.TeamMember, ct);
        var updated = await _workItemService.UpdateStateAsync(workItemId, request.State, _currentUser.UserId, ct);
        return Ok(new ApiResponse<WorkItemResponse>(true, "Work item state updated.", updated));
    }

    /// <summary>Soft-delete a work item. Requires ProjectAdmin.</summary>
    [HttpDelete("api/workitems/{workItemId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid workItemId, CancellationToken ct)
    {
        var item = await _workItemService.GetByIdAsync(workItemId, ct);
        await RequireProjectRoleAsync(item.ProjectId, RoleType.ProjectAdmin, ct);
        await _workItemService.DeleteAsync(workItemId, _currentUser.UserId, ct);
        return NoContent();
    }

    // ── Comments ──────────────────────────────────────────────────────────────

    /// <summary>List comments on a work item.</summary>
    [HttpGet("api/workitems/{workItemId:guid}/comments")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<WorkItemCommentResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetComments(
        Guid workItemId,
        [FromQuery] PaginationQuery pagination,
        CancellationToken ct)
    {
        var item = await _workItemService.GetByIdAsync(workItemId, ct);
        await RequireProjectRoleAsync(item.ProjectId, RoleType.Reader, ct);
        var result = await _workItemService.GetCommentsAsync(workItemId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<WorkItemCommentResponse>>(true, "Comments retrieved.", result));
    }

    /// <summary>Add a comment to a work item.</summary>
    [HttpPost("api/workitems/{workItemId:guid}/comments")]
    [ProducesResponseType(typeof(ApiResponse<WorkItemCommentResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AddComment(
        Guid workItemId,
        [FromBody] AddWorkItemCommentRequest request,
        CancellationToken ct)
    {
        var item = await _workItemService.GetByIdAsync(workItemId, ct);
        await RequireProjectRoleAsync(item.ProjectId, RoleType.TeamMember, ct);
        var comment = await _workItemService.AddCommentAsync(workItemId, request, _currentUser.UserId, ct);
        return StatusCode(StatusCodes.Status201Created,
            new ApiResponse<WorkItemCommentResponse>(true, "Comment added.", comment));
    }

    /// <summary>Edit your own comment.</summary>
    [HttpPut("api/workitems/comments/{commentId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<WorkItemCommentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateComment(
        Guid commentId,
        [FromBody] UpdateWorkItemCommentRequest request,
        CancellationToken ct)
    {
        var comment = await _workItemService.UpdateCommentAsync(commentId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<WorkItemCommentResponse>(true, "Comment updated.", comment));
    }

    /// <summary>Delete your own comment.</summary>
    [HttpDelete("api/workitems/comments/{commentId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteComment(Guid commentId, CancellationToken ct)
    {
        await _workItemService.DeleteCommentAsync(commentId, _currentUser.UserId, ct);
        return NoContent();
    }

    // ── History ───────────────────────────────────────────────────────────────

    /// <summary>Get the audit history for a work item.</summary>
    [HttpGet("api/workitems/{workItemId:guid}/history")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<WorkItemHistoryResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistory(
        Guid workItemId,
        [FromQuery] PaginationQuery pagination,
        CancellationToken ct)
    {
        var item = await _workItemService.GetByIdAsync(workItemId, ct);
        await RequireProjectRoleAsync(item.ProjectId, RoleType.Reader, ct);
        var result = await _workItemService.GetHistoryAsync(workItemId, pagination, ct);
        return Ok(new ApiResponse<PagedResult<WorkItemHistoryResponse>>(true, "History retrieved.", result));
    }

    // ── Links ─────────────────────────────────────────────────────────────────

    /// <summary>Get all links for a work item.</summary>
    [HttpGet("api/workitems/{workItemId:guid}/links")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<WorkItemLinkResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLinks(Guid workItemId, CancellationToken ct)
    {
        var item = await _workItemService.GetByIdAsync(workItemId, ct);
        await RequireProjectRoleAsync(item.ProjectId, RoleType.Reader, ct);
        var links = await _workItemService.GetLinksAsync(workItemId, ct);
        return Ok(new ApiResponse<IReadOnlyList<WorkItemLinkResponse>>(true, "Links retrieved.", links));
    }

    /// <summary>Create a link between two work items.</summary>
    [HttpPost("api/workitems/{workItemId:guid}/links")]
    [ProducesResponseType(typeof(ApiResponse<WorkItemLinkResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddLink(
        Guid workItemId,
        [FromBody] AddWorkItemLinkRequest request,
        CancellationToken ct)
    {
        var item = await _workItemService.GetByIdAsync(workItemId, ct);
        await RequireProjectRoleAsync(item.ProjectId, RoleType.TeamMember, ct);
        var link = await _workItemService.AddLinkAsync(workItemId, request, _currentUser.UserId, ct);
        return StatusCode(StatusCodes.Status201Created,
            new ApiResponse<WorkItemLinkResponse>(true, "Link created.", link));
    }

    /// <summary>Remove a link between work items.</summary>
    [HttpDelete("api/workitems/links/{linkId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveLink(Guid linkId, CancellationToken ct)
    {
        await _workItemService.RemoveLinkAsync(linkId, _currentUser.UserId, ct);
        return NoContent();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task RequireProjectRoleAsync(Guid projectId, RoleType minimum, CancellationToken ct)
    {
        if (!await _rbac.HasRoleAsync(_currentUser.UserId, minimum, RoleScope.Project, projectId, ct))
            throw new ForbiddenException();
    }
}
