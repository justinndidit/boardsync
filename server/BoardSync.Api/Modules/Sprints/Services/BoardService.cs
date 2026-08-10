using BoardSync.Api.Data;
using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Events;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Sprints.Services;

public class BoardService : IBoardService
{
    private readonly BoardSyncDbContext _context;
    private readonly IEventBus _eventBus;
    private readonly ILogger<BoardService> _logger;

    public BoardService(BoardSyncDbContext context, IEventBus eventBus, ILogger<BoardService> logger)
    {
        _context = context;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<BoardResponse> GetOrCreateForProjectAsync(
        Guid projectId,
        Guid createdBy,
        CancellationToken ct = default)
    {
        if (!await _context.Projects.AnyAsync(p => p.Id == projectId && p.IsActive, ct))
            throw new NotFoundException("Project", projectId);

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
        var previousName = board.Name;

        board.Name = request.Name.Trim();
        board.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        if (previousName != board.Name)
            await PublishAsync(board, "Name", previousName, board.Name, updatedBy, ct);

        return await BuildBoardResponseAsync(board, ct);
    }

    public async Task<BoardColumnDetailResponse> AddColumnAsync(
        Guid boardId,
        CreateBoardColumnRequest request,
        Guid createdBy,
        CancellationToken ct = default)
    {
        var board = await GetBoardOrThrowAsync(boardId, ct);

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

        await PublishAsync(board, "Column added", null, column.Name, createdBy, ct);

        return MapColumnDetail(column);
    }

    public async Task<BoardColumnDetailResponse> UpdateColumnAsync(
        Guid columnId,
        UpdateBoardColumnRequest request,
        Guid updatedBy,
        CancellationToken ct = default)
    {
        var column = await GetColumnOrThrowAsync(columnId, ct);
        var previousName = column.Name;

        column.Name        = request.Name.Trim();
        column.MappedState = request.MappedState.Trim();
        column.WipLimit    = request.WipLimit;
        column.Position    = request.Position;
        column.UpdatedAt   = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        var board = await GetBoardOrThrowAsync(column.BoardId, ct);
        await PublishAsync(board, "Column updated", previousName, column.Name, updatedBy, ct);

        return MapColumnDetail(column);
    }

    public async Task DeleteColumnAsync(Guid columnId, Guid deletedBy, CancellationToken ct = default)
    {
        var column = await GetColumnOrThrowAsync(columnId, ct);
        var board = await GetBoardOrThrowAsync(column.BoardId, ct);
        var name = column.Name;

        _context.BoardColumns.Remove(column);
        await _context.SaveChangesAsync(ct);

        await PublishAsync(board, "Column removed", name, null, deletedBy, ct);
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

    /// <summary>
    /// Publishes a board change against the project's owning organization, which is what the
    /// activity log files entries under. Skipped if the project has gone — there would be no
    /// organization to attribute the change to.
    /// </summary>
    private async Task PublishAsync(
        Board board,
        string change,
        string? oldValue,
        string? newValue,
        Guid changedBy,
        CancellationToken ct)
    {
        var orgId = await _context.Projects
            .Where(p => p.Id == board.ProjectId)
            .Select(p => (Guid?)p.OrganizationId)
            .FirstOrDefaultAsync(ct);

        if (orgId is null) return;

        await _eventBus.PublishAsync(new BoardChanged(
            board.Id, board.ProjectId, orgId.Value, board.Name, change, oldValue, newValue, changedBy), ct);
    }

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
        // A board is scoped to a project, but sprints are scoped to a team, so the board's
        // cards come from the active sprint of the project's *assigned* team. Resolving that
        // team here is what makes the project-scoped board and the team-scoped sprint meet.
        var teamId = await _context.Projects
            .Where(p => p.Id == board.ProjectId)
            .Select(p => p.AssignedTeamId)
            .FirstOrDefaultAsync(ct);

        var activeSprint = await _context.Sprints
            .Where(s => s.TeamId == teamId && s.Status == SprintStatus.Active)
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
            board.Id, board.ProjectId, teamId, board.Name,
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
