using BoardSync.Api.Data;
using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Sprints.Services;

public class BoardService : IBoardService
{
    private readonly BoardSyncDbContext _context;
    private readonly ILogger<BoardService> _logger;

    public BoardService(BoardSyncDbContext context, ILogger<BoardService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<BoardResponse> GetOrCreateForProjectAsync(
        Guid projectId,
        Guid createdBy,
        CancellationToken ct = default)
    {
        if (!await _context.Projects.AnyAsync(t => t.Id == projectId && t.IsActive, ct))
            throw new NotFoundException("project", projectId);

        var board = await _context.Boards
            .Include(b => b.Columns)
            .FirstOrDefaultAsync(b => b.ProjectId == projectId, ct);

        if (board is null)
        {
            board = BuildDefaultBoard(projectId, createdBy);
            _context.Boards.Add(board);
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Board auto-created for project {ProjectId}", projectId);
        }

        return await BuildBoardResponseAsync(board, ct);
    }

    public async Task<BoardResponse> GetByIdAsync(Guid boardId, CancellationToken ct = default)
    {
        var board = await GetBoardOrThrowAsync(boardId, ct);
        return await BuildBoardResponseAsync(board, ct);
    }

    public async Task<BoardResponse> UpdateAsync(
        Guid boardId,
        UpdateBoardRequest request,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var board = await GetBoardOrThrowAsync(boardId, ct);
        board.Name = request.Name.Trim();
        board.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
        return await BuildBoardResponseAsync(board, ct);
    }

    public async Task<BoardColumnDetailResponse> AddColumnAsync(
        Guid boardId,
        CreateBoardColumnRequest request,
        Guid createdBy,
        CancellationToken ct = default)
    {
        _ = await GetBoardOrThrowAsync(boardId, ct);

        // Default to appending at the end
        var position = request.Position ?? (
            await _context.BoardColumns
                .Where(c => c.BoardId == boardId)
                .MaxAsync(c => (int?)c.Position, ct) ?? -1) + 1;

        var column = new BoardColumn
        {
            BoardId   = boardId,
            Name      = request.Name.Trim(),
            MappedState = request.MappedState.Trim(),
            Position  = position,
            WipLimit  = request.WipLimit,
            CreatedBy = createdBy
        };

        _context.BoardColumns.Add(column);
        await _context.SaveChangesAsync(ct);
        return MapColumnDetail(column);
    }

    public async Task<BoardColumnDetailResponse> UpdateColumnAsync(
        Guid columnId,
        UpdateBoardColumnRequest request,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var column = await GetColumnOrThrowAsync(columnId, ct);

        column.Name        = request.Name.Trim();
        column.MappedState = request.MappedState.Trim();
        column.WipLimit    = request.WipLimit;
        column.Position    = request.Position;
        column.UpdatedAt   = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return MapColumnDetail(column);
    }

    public async Task DeleteColumnAsync(Guid columnId, CancellationToken ct = default)
    {
        var column = await GetColumnOrThrowAsync(columnId, ct);
        _context.BoardColumns.Remove(column);
        await _context.SaveChangesAsync(ct);
    }

    public async Task ReorderColumnsAsync(
        Guid boardId,
        ReorderBoardColumnsRequest request,
        CancellationToken ct = default)
    {
        _ = await GetBoardOrThrowAsync(boardId, ct);

        var columns = await _context.BoardColumns
            .Where(c => c.BoardId == boardId)
            .ToListAsync(ct);

        for (int i = 0; i < request.ColumnIds.Count; i++)
        {
            var col = columns.FirstOrDefault(c => c.Id == request.ColumnIds[i]);
            if (col is not null)
                col.Position = i;
        }

        await _context.SaveChangesAsync(ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<Board> GetBoardOrThrowAsync(Guid boardId, CancellationToken ct)
        => await _context.Boards
               .Include(b => b.Columns)
               .FirstOrDefaultAsync(b => b.Id == boardId, ct)
           ?? throw new NotFoundException("Board", boardId);

    private async Task<BoardColumn> GetColumnOrThrowAsync(Guid columnId, CancellationToken ct)
        => await _context.BoardColumns.FirstOrDefaultAsync(c => c.Id == columnId, ct)
           ?? throw new NotFoundException("BoardColumn", columnId);

    private async Task<BoardResponse> BuildBoardResponseAsync(Board board, CancellationToken ct)
    {
        // Find active sprint for this project
        var activeSprint = await _context.Sprints
            .Where(s => s.  TeamId == board.ProjectId && s.Status == SprintStatus.Active)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);

        // Fetch cards from the active sprint (if any)
        var cards = new List<(Guid WorkItemId, string Title,
            string Type, string State, string Priority,
            Guid? AssigneeId, int? StoryPoints, List<string> Tags)>();

        if (activeSprint.HasValue)
        {
            var raw = await _context.SprintWorkItems
                .Where(sw => sw.SprintId == activeSprint.Value)
                .Join(_context.WorkItems,
                    sw => sw.WorkItemId,
                    w  => w.Id,
                    (sw, w) => new
                    {
                        w.Id, w.Title,
                        Type     = w.Type.ToString(),
                        State    = w.State.ToString(),
                        Priority = w.Priority.ToString(),
                        w.AssigneeId, w.StoryPoints
                    })
                .ToListAsync(ct);

            // Batch-load tags
            var wids   = raw.Select(w => w.Id).ToList();
            var tagMap = await _context.WorkItemTags
                .Where(t => wids.Contains(t.WorkItemId))
                .GroupBy(t => t.WorkItemId)
                .ToDictionaryAsync(g => g.Key, g => g.Select(t => t.Name).ToList(), ct);

            cards = raw.Select(w => (
                w.Id, w.Title, w.Type, w.State, w.Priority,
                w.AssigneeId, w.StoryPoints,
                tagMap.GetValueOrDefault(w.Id, new List<string>())
            )).ToList();
        }

        var columns = board.Columns
            .OrderBy(c => c.Position)
            .Select(col =>
            {
                var colCards = cards
                    .Where(w => w.State == col.MappedState)
                    .Select(w => new BoardCardResponse(
                        w.WorkItemId, w.Title,
                        Enum.Parse<WorkItems.Models.WorkItemType>(w.Type),
                        Enum.Parse<WorkItems.Models.WorkItemPriority>(w.Priority),
                        w.AssigneeId, w.StoryPoints, w.Tags))
                    .ToList();

                return new BoardColumnResponse(
                    col.Id, col.Name, col.MappedState,
                    col.Position, col.WipLimit, colCards);
            })
            .ToList();

        return new BoardResponse(
            board.Id, board.ProjectId, board.Name,
            activeSprint, columns, board.CreatedAt);
    }

    /// <summary>Creates a board with the four default columns mapped to WorkItemState values.</summary>
    private static Board BuildDefaultBoard(Guid projectId, Guid createdBy) => new()
    {
        ProjectId    = projectId,
        Name      = "Board",
        CreatedBy = createdBy,
        Columns   = new List<BoardColumn>
        {
            new() { Name = "To Do",       MappedState = "New",      Position = 0, CreatedBy = createdBy },
            new() { Name = "In Progress", MappedState = "Active",   Position = 1, CreatedBy = createdBy },
            new() { Name = "In Review",   MappedState = "Resolved", Position = 2, CreatedBy = createdBy },
            new() { Name = "Done",        MappedState = "Closed",   Position = 3, CreatedBy = createdBy }
        }
    };

    private static BoardColumnDetailResponse MapColumnDetail(BoardColumn c) =>
        new(c.Id, c.BoardId, c.Name, c.MappedState, c.Position, c.WipLimit, c.CreatedAt);
}
  //  public Task<BoardResponse> GetOrCreateForPAsync(Guid projectId, Guid createdBy, CancellationToken ct = default)
   // {
   //     throw new NotImplementedException();
 //   }
//
