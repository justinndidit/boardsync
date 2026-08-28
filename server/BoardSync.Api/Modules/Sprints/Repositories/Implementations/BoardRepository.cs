using BoardSync.Api.Data;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.Sprints.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Sprints.Repositories.Implementations;

/// <inheritdoc />
public class BoardRepository : IBoardRepository
{
    private readonly BoardSyncDbContext _context;

    public BoardRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    // ── Boards ────────────────────────────────────────────────────────────────

    public Task<Board?> GetWithColumnsAsync(Guid boardId, CancellationToken ct = default) =>
        _context.Boards
            .Include(b => b.Columns)
            .FirstOrDefaultAsync(b => b.Id == boardId, ct);

    public Task<Board?> GetForProjectWithColumnsAsync(Guid projectId, CancellationToken ct = default) =>
        _context.Boards
            .Include(b => b.Columns)
            .FirstOrDefaultAsync(b => b.ProjectId == projectId, ct);

    public Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct = default) =>
        _context.Projects.AnyAsync(p => p.Id == projectId && p.IsActive, ct);

    public Task<Guid?> GetOrganizationIdForProjectAsync(Guid projectId, CancellationToken ct = default) =>
        _context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.OrganizationId)
            .FirstOrDefaultAsync(ct);

    public async Task<BoardSprintContext?> GetSprintContextAsync(Guid projectId, CancellationToken ct = default)
    {
        // The team's active sprint, not the project's — a sprint belongs to the team, and a board
        // shows that sprint filtered to this project's work.
        //
        // An explicit left join rather than a correlated subquery in the projection: the subquery
        // form has to reference the outer row's team from inside, which is one of the shapes EF
        // declines to translate, and it fails at run time rather than at build.
        var context = await (
            from p in _context.Projects
            where p.Id == projectId
            join s in _context.Sprints.Where(x => x.Status == SprintStatus.Active)
                on p.AssignedTeamId equals s.TeamId into active
            from s in active.DefaultIfEmpty()
            select new BoardSprintContext(p.AssignedTeamId, (Guid?)s.Id)
        ).FirstOrDefaultAsync(ct);

        return context.Equals(default(BoardSprintContext)) ? null : context;
    }

    public async Task<IReadOnlyList<BoardCardRow>> GetCardsForSprintAsync(
        Guid sprintId,
        Guid projectId,
        CancellationToken ct = default)
    {
        // One lookup for the whole board rather than a join per card. `Reference` is the key and
        // the number composed — the key is the project's and identical for every card here, so
        // joining it onto each row would ship the same string once per card.
        var key = await _context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => p.Key)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var rows = await _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId)
            .Join(_context.WorkItems.Where(w => w.ProjectId == projectId),
                sw => sw.WorkItemId,
                w => w.Id,
                (sw, w) => new CardRow(
                    w.Id,
                    w.Number,
                    w.Title,
                    w.Type,
                    w.State,
                    w.Priority,
                    w.AssigneeId,
                    w.StoryPoints,
                    // Correlated collection rather than a second query plus an in-memory join. The
                    // alternative shape — joining rows and regrouping — multiplies each card by its
                    // tag count over the wire.
                    _context.WorkItemTags
                        .Where(t => t.WorkItemId == w.Id)
                        .Select(t => t.Name)
                        .ToList()))
            .ToListAsync(ct);

        return [.. rows.Select(r => new BoardCardRow(
            r.WorkItemId, $"{key}-{r.Number}", r.Title, r.Type, r.State,
            r.Priority, r.AssigneeId, r.StoryPoints, r.Tags))];
    }

    /// <summary>The card as queried, before the project key is folded into a reference.</summary>
    private sealed record CardRow(
        Guid WorkItemId,
        int Number,
        string Title,
        WorkItems.Models.WorkItemType Type,
        WorkItems.Models.WorkItemState State,
        WorkItems.Models.WorkItemPriority Priority,
        Guid? AssigneeId,
        int? StoryPoints,
        List<string> Tags);

    public void Add(Board board) => _context.Boards.Add(board);

    // ── Columns ───────────────────────────────────────────────────────────────

    public Task<BoardColumn?> GetColumnAsync(Guid columnId, CancellationToken ct = default) =>
        _context.BoardColumns.FirstOrDefaultAsync(c => c.Id == columnId, ct);

    public async Task<IReadOnlyList<BoardColumn>> GetColumnsAsync(Guid boardId, CancellationToken ct = default) =>
        await _context.BoardColumns
            .Where(c => c.BoardId == boardId)
            .ToListAsync(ct);

    public async Task<int> GetNextColumnPositionAsync(Guid boardId, CancellationToken ct = default) =>
        (await _context.BoardColumns
            .Where(c => c.BoardId == boardId)
            .MaxAsync(c => (int?)c.Position, ct) ?? -1) + 1;

    public Task<Guid?> GetProjectIdForColumnAsync(Guid columnId, CancellationToken ct = default) =>
        _context.BoardColumns
            .Where(c => c.Id == columnId)
            .Select(c => (Guid?)c.Board.ProjectId)
            .FirstOrDefaultAsync(ct);

    public void AddColumn(BoardColumn column) => _context.BoardColumns.Add(column);

    public void RemoveColumn(BoardColumn column) => _context.BoardColumns.Remove(column);

    // ── Unit of work ──────────────────────────────────────────────────────────

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);
}
