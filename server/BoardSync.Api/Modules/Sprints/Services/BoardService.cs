using BoardSync.Api.Modules.Sprints.DTOs;
using Microsoft.Extensions.Caching.Hybrid;
using BoardSync.Api.Modules.Sprints.Events;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.Sprints.Repositories.Interfaces;
using BoardSync.Api.Shared.Kernel.Events;
using BoardSync.Api.Shared.Kernel.Exceptions;

namespace BoardSync.Api.Modules.Sprints.Services;

public class BoardService : IBoardService
{
    private readonly IBoardRepository _repository;
    private readonly IEventBus _eventBus;
    private readonly HybridCache _cache;
    private readonly IBoardCacheVersion? _version;
    private readonly ILogger<BoardService> _logger;

    /// <summary>
    /// Short on purpose. The version stamp is what makes a change visible immediately; this only
    /// bounds how long an untouched board can sit cached, and a board nobody is changing is a board
    /// nobody minds reading slightly late.
    /// </summary>
    private static readonly HybridCacheEntryOptions SnapshotOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(2),
        LocalCacheExpiration = TimeSpan.FromSeconds(20)
    };

    public BoardService(
        IBoardRepository repository,
        IEventBus eventBus,
        HybridCache cache,
        ILogger<BoardService> logger,
        IBoardCacheVersion? version = null)
    {
        _repository = repository;
        _eventBus = eventBus;
        _cache = cache;
        _version = version;
        _logger = logger;
    }

    public async Task<BoardResponse> GetOrCreateForProjectAsync(
        Guid projectId,
        Guid createdBy,
        CancellationToken ct = default)
    {
        if (!await _repository.ProjectExistsAsync(projectId, ct))
            throw new NotFoundException("Project", projectId);

        var board = await _repository.GetForProjectWithColumnsAsync(projectId, ct);

        if (board is null)
        {
            board = BuildDefaultBoard(projectId, createdBy);
            _repository.Add(board);
            await _repository.SaveChangesAsync(ct);
            _logger.LogInformation("Board auto-created for project {ProjectId}", projectId);
        }

        return await GetBoardResponseAsync(board, ct);
    }

    public async Task<BoardResponse> GetByIdAsync(Guid boardId, CancellationToken ct = default)
    {
        var board = await GetBoardOrThrowAsync(boardId, ct);
        return await GetBoardResponseAsync(board, ct);
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

        if (previousName != board.Name)
            await EnqueueAsync(board, "Name", previousName, board.Name, updatedBy, ct);

        await _repository.SaveChangesAsync(ct);

        return await BuildBoardResponseAsync(board, ct);
    }

    public async Task<BoardColumnDetailResponse> AddColumnAsync(
        Guid boardId,
        CreateBoardColumnRequest request,
        Guid createdBy,
        CancellationToken ct = default)
    {
        var board = await GetBoardOrThrowAsync(boardId, ct);

        var position = request.Position ?? await _repository.GetNextColumnPositionAsync(boardId, ct);

        var column = new BoardColumn
        {
            BoardId     = boardId,
            Name        = request.Name.Trim(),
            MappedState = request.MappedState.Trim(),
            Position    = position,
            WipLimit    = request.WipLimit,
            CreatedBy   = createdBy
        };

        _repository.AddColumn(column);

        await EnqueueAsync(board, "Column added", null, column.Name, createdBy, ct);

        await _repository.SaveChangesAsync(ct);

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

        var board = await GetBoardOrThrowAsync(column.BoardId, ct);
        await EnqueueAsync(board, "Column updated", previousName, column.Name, updatedBy, ct);

        await _repository.SaveChangesAsync(ct);

        return MapColumnDetail(column);
    }

    public async Task DeleteColumnAsync(Guid columnId, Guid deletedBy, CancellationToken ct = default)
    {
        var column = await GetColumnOrThrowAsync(columnId, ct);
        var board = await GetBoardOrThrowAsync(column.BoardId, ct);
        var name = column.Name;

        _repository.RemoveColumn(column);

        await EnqueueAsync(board, "Column removed", name, null, deletedBy, ct);

        await _repository.SaveChangesAsync(ct);
    }

    public async Task ReorderColumnsAsync(
        Guid boardId,
        ReorderBoardColumnsRequest request,
        CancellationToken ct = default)
    {
        _ = await GetBoardOrThrowAsync(boardId, ct);

        var columns = await _repository.GetColumnsAsync(boardId, ct);

        for (int i = 0; i < request.ColumnIds.Count; i++)
        {
            var col = columns.FirstOrDefault(c => c.Id == request.ColumnIds[i]);
            if (col is not null)
                col.Position = i;
        }

        await _repository.SaveChangesAsync(ct);
    }

    public async Task<Guid> GetProjectIdForColumnAsync(Guid columnId, CancellationToken ct = default)
        => await _repository.GetProjectIdForColumnAsync(columnId, ct)
           ?? throw new NotFoundException("BoardColumn", columnId);

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Publishes a board change against the project's owning organization, which is what the
    /// activity log files entries under. Skipped if the project has gone — there would be no
    /// organization to attribute the change to.
    /// </summary>
    private async Task EnqueueAsync(
        Board board,
        string change,
        string? oldValue,
        string? newValue,
        Guid changedBy,
        CancellationToken ct)
    {
        var orgId = await _repository.GetOrganizationIdForProjectAsync(board.ProjectId, ct);

        if (orgId is null) return;

        _eventBus.Enqueue(new BoardChanged(
            board.Id, board.ProjectId, orgId.Value, board.Name, change, oldValue, newValue, changedBy));
    }

    private async Task<Board> GetBoardOrThrowAsync(Guid boardId, CancellationToken ct)
        => await _repository.GetWithColumnsAsync(boardId, ct)
           ?? throw new NotFoundException("Board", boardId);

    private async Task<BoardColumn> GetColumnOrThrowAsync(Guid columnId, CancellationToken ct)
        => await _repository.GetColumnAsync(columnId, ct)
           ?? throw new NotFoundException("BoardColumn", columnId);

    /// <summary>
    /// Builds the board, from cache when one is available for the project's current generation.
    /// </summary>
    /// <remarks>
    /// Without Redis there is no generation to stamp keys with, and caching without one would serve
    /// boards that never notice a card moved — so it reads through instead.
    /// </remarks>
    private async Task<BoardResponse> GetBoardResponseAsync(Board board, CancellationToken ct)
    {
        if (_version is null)
            return await BuildBoardResponseAsync(board, ct);

        var version = await _version.GetAsync(board.ProjectId);
        var key = $"board:v1:{board.ProjectId}:{version}";

        return await _cache.GetOrCreateAsync(
            key,
            (Service: this, board),
            static (state, token) => new ValueTask<BoardResponse>(
                state.Service.BuildBoardResponseAsync(state.board, token)),
            SnapshotOptions,
            cancellationToken: ct);
    }

    private async Task<BoardResponse> BuildBoardResponseAsync(Board board, CancellationToken ct)
    {
        // Board and sprint are both scoped to the project now, so the board's cards come from
        // that project's own active sprint. The assigned team still comes back with it, because the
        // board response reports which team is working the project.
        var context = await _repository.GetSprintContextAsync(board.ProjectId, ct);

        var teamId = context?.TeamId ?? Guid.Empty;
        var activeSprint = context?.ActiveSprintId;

        var cards = activeSprint.HasValue
            ? await _repository.GetCardsForSprintAsync(activeSprint.Value, board.ProjectId, ct)
            : [];

        var columns = board.Columns
            .OrderBy(c => c.Position)
            .Select(col =>
            {
                var colCards = cards
                    .Where(w => w.State.ToString() == col.MappedState)
                    .Select(w => new BoardCardResponse(
                        w.WorkItemId, w.Reference, w.Title, w.Type, w.Priority,
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

    /// <summary>Creates a board with a default column per WorkItemState.</summary>
    /// <remarks>
    /// "In Review" used to map to <c>Resolved</c>, which conflated "a pull request is open" with
    /// "merged and waiting to be tested". Those are different places for work to sit and different
    /// people are waiting on them, so they are now separate lanes — and the QA lane is named for what
    /// it is waiting on rather than for the enum value behind it.
    /// </remarks>
    private static Board BuildDefaultBoard(Guid projectId, Guid createdBy) => new()
    {
        ProjectId = projectId,
        Name      = "Board",
        CreatedBy = createdBy,
        Columns   = new List<BoardColumn>
        {
            new() { Name = "To Do",       MappedState = "New",      Position = 0, CreatedBy = createdBy },
            new() { Name = "In Progress", MappedState = "Active",   Position = 1, CreatedBy = createdBy },
            new() { Name = "In Review",   MappedState = "InReview", Position = 2, CreatedBy = createdBy },
            new() { Name = "Awaiting QA", MappedState = "Resolved", Position = 3, CreatedBy = createdBy },
            new() { Name = "Done",        MappedState = "Closed",   Position = 4, CreatedBy = createdBy }
        }
    };

    private static BoardColumnDetailResponse MapColumnDetail(BoardColumn c) =>
        new(c.Id, c.BoardId, c.Name, c.MappedState, c.Position, c.WipLimit, c.CreatedAt);
}
