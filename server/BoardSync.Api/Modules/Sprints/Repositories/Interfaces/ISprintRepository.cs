using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Models;

namespace BoardSync.Api.Modules.Sprints.Repositories.Interfaces;

/// <summary>
/// Data access for the Sprint aggregate and its backlog — the <c>plan.Sprints</c> and
/// <c>plan.SprintWorkItems</c> tables.
/// </summary>
/// <remarks>
/// <para>
/// Mutations are staged and only persisted by <see cref="SaveChangesAsync"/>, so the service keeps
/// control of the transaction boundary.
/// </para>
/// <para>
/// A few methods read across into <c>work.WorkItems</c> and <c>org.Teams</c>. That is deliberate and
/// confined to read-side projections: a sprint backlog is meaningless without the work item titles
/// and states beside it, and resolving those one aggregate at a time would trade one query for one
/// per row. Nothing here writes outside the <c>plan</c> schema.
/// </para>
/// </remarks>
public interface ISprintRepository
{
    // ── Sprints ───────────────────────────────────────────────────────────────

    /// <summary>Sprint by ID, tracked for mutation, or null.</summary>
    Task<Sprint?> GetByIdAsync(Guid sprintId, CancellationToken ct = default);
    Task LockSprintAsync(Guid sprintId, CancellationToken ct = default);

    /// <summary>The team's active sprint, or null. At most one exists.</summary>
    Task<Sprint?> GetActiveForTeamAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// The active sprint of the team that owns a project.
    /// </summary>
    /// <remarks>
    /// A convenience for the board, which asks "what is this project's current sprint" on every
    /// load. Resolving the team client-side first would be a round trip for something one join
    /// answers.
    /// </remarks>
    Task<Sprint?> GetActiveForProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Paginated sprint summaries for a team, newest sprint number first.</summary>
    Task<(IReadOnlyList<SprintSummaryResponse> Items, int TotalCount)> GetForTeamAsync(
        Guid teamId,
        int skip,
        int take,
        CancellationToken ct = default);

    /// <summary>Whether an active project with this ID exists.</summary>
    Task<bool> ProjectExistsAsync(Guid ProjectId, CancellationToken ct = default);

    /// <summary>Whether an active team exists, without loading it.</summary>
    Task<bool> TeamExistsAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>The organization a team belongs to, or null when the team does not exist.</summary>
    Task<Guid?> GetOrganizationIdForTeamAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>The team a project is assigned to, or null when the project does not exist.</summary>
    Task<Guid?> GetTeamIdForProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// Whether a project is one this team serves.
    /// </summary>
    /// <remarks>
    /// The boundary for what may go in a sprint. A sprint holds work from any project its team is
    /// assigned to, and nothing else — which is what stops a team member naming a work item id from
    /// another organization and reading it back off their own board.
    /// </remarks>
    Task<bool> TeamServesProjectAsync(Guid teamId, Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// The project a project is assigned to, or null if the project does not exist.
    /// </summary>
    /// <remarks>
    /// Used to check that a work item belongs to the same project as the sprint it is being added to.
    /// A sprint is project-scoped and a project can hold several projects, so items from any of the
    /// project's projects are legitimate — items from anywhere else are not.
    /// </remarks>

    /// <summary>
    /// Whether the project already has a non-completed sprint covering any part of the given range.
    /// Completed sprints are excluded — history is allowed to overlap, only live plans are not.
    /// </summary>
    Task<bool> HasOverlappingSprintAsync(
        Guid teamId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken ct = default);

    /// <summary>
    /// Whether the project has an active sprint other than <paramref name="excludingSprintId"/>.
    /// Guards the one-active-sprint-per-project rule when starting a sprint.
    /// </summary>
    Task<bool> HasAnotherActiveSprintAsync(Guid teamId, Guid excludingSprintId, CancellationToken ct = default);

    /// <summary>Next sprint number for a project. Numbers are sequential per project, starting at 1.</summary>
    Task<int> GetNextNumberAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// The organization owning a sprint's project, or null if the project is gone. Sprints hang off a
    /// project but activity is filed by organization, so events need this before they can be published.
    /// </summary>
    Task<Guid?> GetOrganizationIdForProjectAsync(Guid projectId, CancellationToken ct = default);

    void Add(Sprint sprint);
    void Remove(Sprint sprint);

    // ── Backlog ───────────────────────────────────────────────────────────────

    /// <summary>A single backlog entry, tracked for mutation, or null.</summary>
    Task<SprintWorkItem?> GetBacklogEntryAsync(Guid sprintId, Guid workItemId, CancellationToken ct = default);

    /// <summary>Every backlog entry for a sprint, tracked — used when reordering.</summary>
    Task<IReadOnlyList<SprintWorkItem>> GetBacklogEntriesAsync(Guid sprintId, CancellationToken ct = default);

    /// <summary>Whether the work item is already on this sprint's backlog.</summary>
    Task<bool> BacklogContainsAsync(Guid sprintId, Guid workItemId, CancellationToken ct = default);

    /// <summary>Whether the sprint has any backlog entries at all.</summary>
    Task<bool> HasBacklogEntriesAsync(Guid sprintId, CancellationToken ct = default);

    /// <summary>Position that appends to the end of the backlog.</summary>
    Task<int> GetNextPositionAsync(Guid sprintId, CancellationToken ct = default);

    /// <summary>Highest rank currently in the backlog, or null when it is empty.</summary>
    Task<decimal?> GetMaxRankAsync(Guid sprintId, CancellationToken ct = default);

    /// <summary>
    /// The ranks of two specific backlog entries, used to compute a midpoint for a move.
    /// A null id yields a null rank, meaning "the end of the list in that direction".
    /// </summary>
    Task<(decimal? Before, decimal? After)> GetNeighbourRanksAsync(
        Guid sprintId,
        Guid? beforeWorkItemId,
        Guid? afterWorkItemId,
        CancellationToken ct = default);

    Task<IReadOnlyList<SprintWorkItem>> GetBacklogEntriesByIdsAsync(
        Guid sprintId,
        IReadOnlyCollection<Guid> workItemIds,
        CancellationToken ct = default);
    Task<bool> RankExistsAsync(Guid sprintId, decimal rank, Guid excludingWorkItemId, CancellationToken ct = default);
    Task ReorderRanksAsync(Guid sprintId, IReadOnlyList<Guid> workItemIds, CancellationToken ct = default);

    /// <summary>Paginated backlog in display order, joined to the work items it points at.</summary>
    Task<(IReadOnlyList<SprintWorkItemResponse> Items, int TotalCount)> GetWorkItemsAsync(
        Guid sprintId,
        int skip,
        int take,
        CancellationToken ct = default);

    /// <summary>Item and story-point totals for a sprint, computed in the database.</summary>
    Task<SprintProgress> GetProgressAsync(Guid sprintId, CancellationToken ct = default);

    void AddBacklogEntry(SprintWorkItem entry);
    void RemoveBacklogEntry(SprintWorkItem entry);

    // ── Unit of work ──────────────────────────────────────────────────────────

    /// <summary>Persists everything staged since the last save.</summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>
/// Rolled-up sprint burn figures. "Completed" means a work item in <c>Closed</c> or
/// <c>Resolved</c> — the two states the board treats as done.
/// </summary>
public readonly record struct SprintProgress(
    int TotalItems,
    int CompletedItems,
    int TotalPoints,
    int CompletedPoints);


