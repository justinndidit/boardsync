using BoardSync.Api.Data;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Modules.Sprints.Domain.Helpers;
using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Services;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    private readonly BoardSyncDbContext _context;
    private readonly IAuthHelpers _authHelpers;

    public BoardsController(
        IBoardService boardService,
        IRbacService rbac,
        ICurrentUserContext currentUser,
        IAuthHelpers authHelpers,
        BoardSyncDbContext context)
    {
        _boardService = boardService;
        _rbac = rbac;
        _currentUser = currentUser;
        _context = context;
        _authHelpers = authHelpers;
    }

    // ── Board ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Get (or auto-create) the board for a project, populated with active-sprint cards.
    /// Requires Reader.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/board")]
    [ProducesResponseType(typeof(ApiResponse<BoardResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetForProject(Guid projectId, CancellationToken ct)
    {
        await _authHelpers.RequireProjectRoleAsync(projectId, RoleType.Reader, ct);
        var board = await _boardService.GetOrCreateForProjectAsync(projectId, _currentUser.UserId, ct);
        return Ok(new ApiResponse<BoardResponse>(true, "Board retrieved.", board));
    }

    /// <summary>Get a board by ID with all columns and cards. Requires Reader.</summary>
    [HttpGet("api/boards/{boardId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<BoardResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid boardId, CancellationToken ct)
    {
        var board = await _boardService.GetByIdAsync(boardId, ct);
        await _authHelpers.RequireProjectRoleAsync(board.ProjectId, RoleType.Reader, ct);
        return Ok(new ApiResponse<BoardResponse>(true, "Board retrieved.", board));
    }

    /// <summary>Rename a board. Requires ProjectAdmin.</summary>
    [HttpPut("api/boards/{boardId:guid}")]
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
        await _authHelpers.RequireProjectRoleAsync(board.ProjectId, RoleType.ProjectAdmin, ct);
        var updated = await _boardService.UpdateAsync(boardId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<BoardResponse>(true, "Board updated.", updated));
    }

    // ── Columns ───────────────────────────────────────────────────────────────

    /// <summary>Add a column to a board. Requires ProjectAdmin.</summary>
    [HttpPost("api/boards/{boardId:guid}/columns")]
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
        await _authHelpers.RequireProjectRoleAsync(board.ProjectId, RoleType.ProjectAdmin, ct);
        var column = await _boardService.AddColumnAsync(boardId, request, _currentUser.UserId, ct);
        return StatusCode(StatusCodes.Status201Created,
            new ApiResponse<BoardColumnDetailResponse>(true, "Column added.", column));
    }

    /// <summary>Update a column's name, mapped state, WIP limit, or position. Requires ProjectAdmin.</summary>
    [HttpPut("api/boards/columns/{columnId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<BoardColumnDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateColumn(
        Guid columnId,
        [FromBody] UpdateBoardColumnRequest request,
        CancellationToken ct)
    {
        await RequireColumnProjectRoleAsync(columnId, RoleType.ProjectAdmin, ct);
        var updated = await _boardService.UpdateColumnAsync(columnId, request, _currentUser.UserId, ct);
        return Ok(new ApiResponse<BoardColumnDetailResponse>(true, "Column updated.", updated));
    }

    /// <summary>Delete a column from a board. Requires ProjectAdmin.</summary>
    [HttpDelete("api/boards/columns/{columnId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteColumn(Guid columnId, CancellationToken ct)
    {
        await RequireColumnProjectRoleAsync(columnId, RoleType.ProjectAdmin, ct);
        await _boardService.DeleteColumnAsync(columnId, ct);
        return NoContent();
    }

    /// <summary>Reorder columns by providing the desired left-to-right column ID sequence. Requires ProjectAdmin.</summary>
    [HttpPatch("api/boards/{boardId:guid}/columns/reorder")]
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
        await _authHelpers.RequireProjectRoleAsync(board.ProjectId, RoleType.ProjectAdmin, ct);
        await _boardService.ReorderColumnsAsync(boardId, request, ct);
        return Ok(new ApiResponse(true, "Columns reordered."));
    }



    /// <summary>
    /// Resolves the projectId for a column (column → board → projectId) then checks the role.
    /// Avoids an extra service call by querying the DB directly.
    /// </summary>
    private async Task RequireColumnProjectRoleAsync(Guid columnId, RoleType minimum, CancellationToken ct)
    {
        var projectId = await _context.BoardColumns
            .Where(c => c.Id == columnId)
            .Select(c => (Guid?)c.Board.ProjectId)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("BoardColumn", columnId);

        await _authHelpers.RequireProjectRoleAsync(projectId, minimum, ct);
    }
}
