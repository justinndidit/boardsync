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
        // The active sprint is a subquery of the project row, so the team and its sprint come back
        // together rather than as a second round trip waiting on the first one's answer.
        var context = await _context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new BoardSprintContext(
                p.AssignedTeamId,
                _context.Sprints
                    .Where(s => s.ProjectId == p.Id && s.Status == SprintStatus.Active)
                    .Select(s => (Guid?)s.Id)
                    .FirstOrDefault()))
            .FirstOrDefaultAsync(ct);

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
