using BoardSync.Api.Modules.Sprints.Models;

namespace BoardSync.Api.Modules.Sprints.Repositories.Interfaces;

/// <summary>
/// Data access for the Board aggregate — the <c>plan.Boards</c> and <c>plan.BoardColumns</c> tables.
/// </summary>
/// <remarks>
/// Like <see cref="ISprintRepository"/>, a handful of reads reach across into
/// <c>org.Projects</c> and <c>work.WorkItems</c>. A board is a view over another module's work
/// items, so rendering one without them is not possible; the writes stay inside <c>plan</c>.
/// </remarks>
public interface IBoardRepository
{
    // ── Boards ────────────────────────────────────────────────────────────────

    /// <summary>Board by ID with its columns loaded, tracked for mutation, or null.</summary>
    Task<Board?> GetWithColumnsAsync(Guid boardId, CancellationToken ct = default);

    /// <summary>A project's board with its columns loaded, or null when it has none yet.</summary>
    Task<Board?> GetForProjectWithColumnsAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Whether an active project with this ID exists.</summary>
    Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>The organization owning a board's project, or null if the project is gone.</summary>
    Task<Guid?> GetOrganizationIdForProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// The team assigned to a project and that team's active sprint, resolved together.
    /// A board is scoped to a project but its cards come from the assigned team's active sprint,
    /// so both are needed before any card can be loaded.
    /// </summary>
    Task<BoardSprintContext?> GetSprintContextAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Every card in a sprint, with its tags, ready to be dealt into columns.</summary>
    /// <summary>
    /// The cards a project's board should show from one sprint.
    /// </summary>
    /// <remarks>
    /// Filtered to <paramref name="projectId"/> because a sprint is team-scoped while a board is
    /// project-scoped, and a team can hold several projects — so one sprint legitimately contains
    /// items belonging to boards other than this one. Without the filter a project's board renders
    /// its sibling projects' cards, and anything wrongly added to the sprint is rendered too.
    /// </remarks>
    Task<IReadOnlyList<BoardCardRow>> GetCardsForSprintAsync(
        Guid sprintId,
        Guid projectId,
        CancellationToken ct = default);

    void Add(Board board);

    // ── Columns ───────────────────────────────────────────────────────────────

    /// <summary>Column by ID, tracked for mutation, or null.</summary>
    Task<BoardColumn?> GetColumnAsync(Guid columnId, CancellationToken ct = default);

    /// <summary>Every column on a board, tracked — used when reordering.</summary>
    Task<IReadOnlyList<BoardColumn>> GetColumnsAsync(Guid boardId, CancellationToken ct = default);

    /// <summary>Position that appends to the end of the column list.</summary>
    Task<int> GetNextColumnPositionAsync(Guid boardId, CancellationToken ct = default);

    /// <summary>
    /// Project owning a column, resolved column → board → project, or null if no such column.
    /// Columns are addressed by their own IDs, so authorization needs this before touching one.
    /// </summary>
    Task<Guid?> GetProjectIdForColumnAsync(Guid columnId, CancellationToken ct = default);

    void AddColumn(BoardColumn column);
    void RemoveColumn(BoardColumn column);

    // ── Unit of work ──────────────────────────────────────────────────────────

    /// <summary>Persists everything staged since the last save.</summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>The team a project's work belongs to, and that team's active sprint if it has one.</summary>
public readonly record struct BoardSprintContext(Guid TeamId, Guid? ActiveSprintId);

/// <summary>
/// A card as it comes out of the database. Enums stay typed the whole way through — stringifying
/// them in SQL only to parse them back per card is work for nothing.
/// </summary>
public sealed record BoardCardRow(
    Guid WorkItemId,
    string Reference,
    string Title,
    WorkItems.Models.WorkItemType Type,
    WorkItems.Models.WorkItemState State,
    WorkItems.Models.WorkItemPriority Priority,
    Guid? AssigneeId,
    int? StoryPoints,
    List<string> Tags);
