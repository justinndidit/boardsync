using BoardSync.Api.Modules.Backlog.Models;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.Backlog.Repositories;

/// <summary>
/// Data access for the product backlog — the <c>plan.BacklogItems</c> table.
/// </summary>
/// <remarks>
/// Pure unit of work: <c>Add</c>/<c>Remove</c> stage changes in memory and nothing is written until
/// <see cref="SaveChangesAsync"/>, so a service can compose several writes into one transaction.
/// <para>
/// Deliberately owns no sprint state. Backlog entries record <em>rank</em> and which sprint an item
/// was pulled into; membership of the sprint itself belongs to the Sprints module, which is the only
/// place that validates a work item may join a given sprint.
/// </para>
/// </remarks>
public interface IBacklogRepository
{
    /// <summary>Whether an active project exists, without loading it.</summary>
    Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// One page of the unscheduled backlog — entries with no sprint — ordered by rank.
    /// </summary>
    /// <param name="projectId">The project whose backlog to read.</param>
    /// <param name="teamId">
    /// When given, narrows to entries scoped to that team plus entries scoped to none. Entries with
    /// no team belong to every team's view of the project.
    /// </param>
    /// <param name="skip">Rows to skip.</param>
    /// <param name="take">Rows to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<(IReadOnlyList<BacklogItem> Items, int TotalCount)> GetUnscheduledPageAsync(
        Guid projectId, Guid? teamId, int skip, int take, CancellationToken ct = default);

    /// <summary>The work items behind a page of backlog entries, with their tags.</summary>
    Task<IReadOnlyDictionary<Guid, WorkItem>> GetWorkItemsAsync(
        IReadOnlyCollection<Guid> workItemIds, CancellationToken ct = default);

    /// <summary>How many active children each of these work items has.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetChildCountsAsync(
        IReadOnlyCollection<Guid> parentIds, CancellationToken ct = default);

    /// <summary>An active work item, if it belongs to this project.</summary>
    Task<WorkItem?> GetWorkItemInProjectAsync(
        Guid workItemId, Guid projectId, CancellationToken ct = default);

    /// <summary>One backlog entry, tracked for mutation, or null.</summary>
    Task<BacklogItem?> GetEntryAsync(Guid projectId, Guid workItemId, CancellationToken ct = default);

    /// <summary>The named backlog entries of a project, tracked for mutation.</summary>
    Task<IReadOnlyList<BacklogItem>> GetEntriesAsync(
        Guid projectId, IReadOnlyCollection<Guid> workItemIds, CancellationToken ct = default);

    /// <summary>
    /// The named entries that are currently assigned to one sprint, tracked for mutation.
    /// </summary>
    Task<IReadOnlyList<BacklogItem>> GetEntriesForSprintAsync(
        Guid sprintId, IReadOnlyCollection<Guid> workItemIds, CancellationToken ct = default);

    /// <summary>Every backlog entry of a project, tracked for mutation.</summary>
    Task<IReadOnlyList<BacklogItem>> GetAllEntriesAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>The lowest-priority rank currently in use, or null when the backlog is empty.</summary>
    Task<decimal?> GetMaxRankAsync(Guid projectId, CancellationToken ct = default);

    void Add(BacklogItem entry);
    void Remove(BacklogItem entry);

    Task SaveChangesAsync(CancellationToken ct = default);
}
