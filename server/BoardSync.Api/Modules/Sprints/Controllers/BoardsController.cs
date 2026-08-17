using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Modules.Sprints.Domain.Helpers;
using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Services;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.Sprints.Controllers;

/// <summary>
/// Kanban board management scoped to a project — one board per project,
/// auto-created with four default columns on first access.
/// Cards are drawn from the active sprint of the project's assigned team.
/// Read:              Reader+ on the project
/// Column management: ProjectAdmin+ on the project
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
public class BoardsController : ControllerBase
{
    private readonly IBoardService _boardService;
    private readonly IRbacService _rbac;
    private readonly ICurrentUserContext _currentUser;
    private readonly IAuthHelpers _authHelpers;

    public BoardsController(
        IBoardService boardService,
        IRbacService rbac,
        ICurrentUserContext currentUser,
        IAuthHelpers authHelpers)
    {
        _boardService = boardService;
        _rbac = rbac;
        _currentUser = currentUser;
        _authHelpers = authHelpers;
    }

    // ── Board ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Get (or auto-create) the board for a project, populated with active-sprint cards.
    /// Requires Reader.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/board")]
    [RequirePermission(Permissions.BoardRead, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<BoardResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetForProject(Guid projectId, CancellationToken ct)
    {
        var board = await _boardService.GetOrCreateForProjectAsync(projectId, _currentUser.UserId, ct);
        return Ok(new ApiResponse<BoardResponse>(true, "Board retrieved.", board));
    }

    /// <summary>Get a board by ID with all columns and cards. Requires Reader.</summary>
    [HttpGet("api/boards/{boardId:guid}")]
    [RequirePermission(Permissions.BoardRead, From = "boardId")]
    [ProducesResponseType(typeof(ApiResponse<BoardResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid boardId, CancellationToken ct)
    {
        var board = await _boardService.GetByIdAsync(boardId, ct);
        return Ok(new ApiResponse<BoardResponse>(true, "Board retrieved.", board));
    }

    /// <summary>Rename a board. Requires ProjectAdmin.</summary>
    [HttpPut("api/boards/{boardId:guid}")]
    [RequirePermission(Permissions.BoardConfigure, From = "boardId")]
    [ProducesResponseType(typeof(ApiResponse<BoardResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid boardId,
        [FromBody] UpdateBoardRequest request,
        CancellationToken ct)
    {
        var board = await _boardService.GetByIdAsync(boardId, ct);
        var updated = await _boardService.UpdateAsync(boardId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<BoardResponse>(true, "Board updated.", updated));
    }

    // ── Columns ───────────────────────────────────────────────────────────────

    /// <summary>Add a column to a board. Requires ProjectAdmin.</summary>
    [HttpPost("api/boards/{boardId:guid}/columns")]
    [RequirePermission(Permissions.BoardConfigure, From = "boardId")]
    [ProducesResponseType(typeof(ApiResponse<BoardColumnDetailResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddColumn(
        Guid boardId,
        [FromBody] CreateBoardColumnRequest request,
        CancellationToken ct)
    {
        var board = await _boardService.GetByIdAsync(boardId, ct);
        var column = await _boardService.AddColumnAsync(boardId, request, _currentUser.UserId, ct);
        return StatusCode(StatusCodes.Status201Created,
            new ApiResponse<BoardColumnDetailResponse>(true, "Column added.", column));
    }

    /// <summary>Update a column's name, mapped state, WIP limit, or position. Requires ProjectAdmin.</summary>
    [HttpPut("api/boards/columns/{columnId:guid}")]
    [RequirePermission(Permissions.BoardConfigure, From = "columnId")]
    [ProducesResponseType(typeof(ApiResponse<BoardColumnDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateColumn(
        Guid columnId,
        [FromBody] UpdateBoardColumnRequest request,
        CancellationToken ct)
    {
        var updated = await _boardService.UpdateColumnAsync(columnId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<BoardColumnDetailResponse>(true, "Column updated.", updated));
    }

    /// <summary>Delete a column from a board. Requires ProjectAdmin.</summary>
    [HttpDelete("api/boards/columns/{columnId:guid}")]
    [RequirePermission(Permissions.BoardConfigure, From = "columnId")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteColumn(Guid columnId, CancellationToken ct)
    {
        await _boardService.DeleteColumnAsync(columnId, _currentUser.UserId, ct);
        return NoContent();
    }

    /// <summary>Reorder columns by providing the desired left-to-right column ID sequence. Requires ProjectAdmin.</summary>
    [HttpPatch("api/boards/{boardId:guid}/columns/reorder")]
    [RequirePermission(Permissions.BoardConfigure, From = "boardId")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReorderColumns(
        Guid boardId,
        [FromBody] ReorderBoardColumnsRequest request,
        CancellationToken ct)
    {
        var board = await _boardService.GetByIdAsync(boardId, ct);
        await _boardService.ReorderColumnsAsync(boardId, request, ct);
        return Ok(new ApiResponse(true, "Columns reordered."));
    }



    /// <summary>
    /// Resolves the project owning a column (column → board → project) then checks the role.
    /// </summary>
    private async Task RequireColumnProjectAsync(Guid columnId, string permission, CancellationToken ct)
    {
        var projectId = await _boardService.GetProjectIdForColumnAsync(columnId, ct);
        await _authHelpers.RequireProjectAsync(projectId, permission, ct);
    }
}
