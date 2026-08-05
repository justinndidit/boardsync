using BoardSync.Api.Data;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services;
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
/// Kanban board management scoped to a team.
/// The board is auto-created with four default columns on first access.
/// Read:             Reader+
/// Column management: ProjectAdmin+
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

    public BoardsController(
        IBoardService boardService,
        IRbacService rbac,
        ICurrentUserContext currentUser,
        BoardSyncDbContext context)
    {
        _boardService = boardService;
        _rbac = rbac;
        _currentUser = currentUser;
        _context = context;
    }

    // ── Board ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Get (or auto-create) the board for a team, populated with active-sprint cards.
    /// Requires Reader.
    /// </summary>
    [HttpGet("api/project/{projectId:guid}/board")]
    [ProducesResponseType(typeof(ApiResponse<BoardResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetForTeam(Guid projectId, CancellationToken ct)
    {
        await RequireTeamRoleAsync(projectId, RoleType.Reader, ct);
        var board = await _boardService.GetOrCreateForTeamAsync(projectId, _currentUser.UserId, ct);
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
        await RequireTeamRoleAsync(board.TeamId, RoleType.Reader, ct);
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
        await RequireTeamRoleAsync(board.TeamId, RoleType.ProjectAdmin, ct);
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
        await RequireTeamRoleAsync(board.TeamId, RoleType.ProjectAdmin, ct);
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
        await RequireColumnTeamRoleAsync(columnId, RoleType.ProjectAdmin, ct);
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
        await RequireColumnTeamRoleAsync(columnId, RoleType.ProjectAdmin, ct);
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
        await RequireTeamRoleAsync(board.TeamId, RoleType.ProjectAdmin, ct);
        await _boardService.ReorderColumnsAsync(boardId, request, ct);
        return Ok(new ApiResponse(true, "Columns reordered."));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task RequireTeamRoleAsync(Guid teamId, RoleType minimum, CancellationToken ct)
    {
        if (!await _rbac.HasRoleAsync(_currentUser.UserId, minimum, RoleScope.Team, teamId, ct))
            throw new ForbiddenException();
    }

    /// <summary>
    /// Resolves the teamId for a column (column → board → teamId) then checks the role.
    /// Avoids an extra service call by querying the DB directly.
    /// </summary>
    private async Task RequireColumnTeamRoleAsync(Guid columnId, RoleType minimum, CancellationToken ct)
    {
        var teamId = await _context.BoardColumns
            .Where(c => c.Id == columnId)
            .Select(c => (Guid?)c.Board.TeamId)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("BoardColumn", columnId);

        await RequireTeamRoleAsync(teamId, minimum, ct);
    }
}
