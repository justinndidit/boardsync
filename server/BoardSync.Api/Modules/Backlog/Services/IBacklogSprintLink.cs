using BoardSync.Api.Modules.Backlog.Models;
using BoardSync.Api.Modules.Sprints.Domain;
using BoardSync.Api.Modules.Backlog.Repositories;

namespace BoardSync.Api.Modules.Backlog.Services;

/// <summary>
/// The one thing the Sprints module needs from the backlog: releasing entries when their sprint
/// lets them go.
/// </summary>
/// <remarks>
/// <para>
/// Exists to break a dependency cycle. The backlog needs the Sprints module, because putting an item
/// into a sprint carries a rule the Sprints module owns. Closing a sprint needs the backlog, because
/// incomplete items go back to it. Wiring both as whole services makes
/// <c>SprintService → BacklogService → SprintService</c>, which the container refuses to resolve.
/// </para>
/// <para>
/// Narrowing one direction to this fixes it honestly rather than by indirection: sprint close does
/// not want the backlog *service*, it wants one field cleared. This depends on the backlog
/// repository and nothing else, so nothing points back.
/// </para>
/// </remarks>
public interface IBacklogSprintLink
{
    /// <summary>
    /// Clears the sprint assignment on the named entries, returning them to the unscheduled backlog.
    /// </summary>
    /// <remarks>
    /// Scoped to <paramref name="sprintId"/>, so an item that also sits in another sprint keeps that
    /// membership. Entries not in this sprint are left alone rather than treated as an error — the
    /// caller is releasing a sprint, not asserting what was in it.
    /// </remarks>
    /// <returns>How many entries were released.</returns>
    Task<int> ClearSprintAsync(
        Guid sprintId,
        IReadOnlyCollection<Guid> workItemIds,
        CancellationToken ct = default);

    /// <summary>
    /// Points these entries at the sprint they have just joined, so they leave the unscheduled
    /// backlog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The backlog is "everything with no sprint", and there are two doors into a sprint:
    /// <c>POST /projects/{id}/backlog/move-to-sprint</c>, which set this, and
    /// <c>POST /sprints/{id}/workitems</c>, which did not. An item committed through the second
    /// stayed listed as unscheduled — visible in the backlog and in the sprint at once, and
    /// available to be planned into a second sprint by somebody reading the backlog.
    /// </para>
    /// <para>
    /// Called directly rather than through an event: the backlog is usually read immediately after,
    /// and eventual consistency here would show the item as still unscheduled for exactly as long
    /// as it takes somebody to look.
    /// </para>
    /// </remarks>
    /// <returns>How many entries were pointed at the sprint.</returns>
    Task<int> AssignSprintAsync(
        Guid sprintId,
        IReadOnlyCollection<Guid> workItemIds,
        CancellationToken ct = default);

    /// <summary>
    /// Makes sure a work item has a backlog entry, appended at the end. Idempotent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>BacklogItem</c> is "one row per work item", and the row is what carries the rank — kept
    /// even while the item sits in a sprint, so an item returning to the backlog lands where it
    /// left rather than at the bottom. An item that never had a row has no position to return to.
    /// </para>
    /// <para>
    /// Called synchronously from creation rather than through a <c>WorkItemCreated</c> subscriber.
    /// A subscriber is tidier for module boundaries and wrong here: creating an item and putting it
    /// straight into a sprint is one gesture on a board, and the sprint write would arrive before
    /// the outbox had made the row to point at.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Re-ranks entries the caller already owns into a given order, below everything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a decomposition acceptance, which produces a batch of items whose order carries meaning
    /// — the model's delivery phases — but which has no claim on the top of a backlog somebody else
    /// prioritised. <c>ReorderAsync</c> is the wrong tool: it takes a complete sequence and puts
    /// everything named at the top, so passing it a PRD's output would push the team's existing
    /// plan underneath work nobody has agreed to yet.
    /// </para>
    /// <para>
    /// Ids that are not in this project's backlog are skipped rather than rejected. The caller is
    /// stating a preference about order, not asserting the contents of the backlog.
    /// </para>
    /// </remarks>
    Task RankBelowAsync(
        Guid projectId,
        IReadOnlyList<Guid> workItemIdsInOrder,
        CancellationToken ct = default);

    Task EnsureEntryAsync(
        Guid projectId,
        Guid workItemId,
        Guid addedBy,
        CancellationToken ct = default);
}

/// <inheritdoc />
public class BacklogSprintLink : IBacklogSprintLink
{
    private readonly IBacklogRepository _repository;

    public BacklogSprintLink(IBacklogRepository repository)
    {
        _repository = repository;
    }

    public async Task<int> ClearSprintAsync(
        Guid sprintId,
        IReadOnlyCollection<Guid> workItemIds,
        CancellationToken ct = default)
    {
        if (workItemIds.Count == 0) return 0;

        var entries = await _repository.GetEntriesForSprintAsync(sprintId, workItemIds, ct);

        foreach (var entry in entries)
            entry.SprintId = null;

        await _repository.SaveChangesAsync(ct);

        return entries.Count;
    }

    public async Task RankBelowAsync(
        Guid projectId,
        IReadOnlyList<Guid> workItemIdsInOrder,
        CancellationToken ct = default)
    {
        if (workItemIdsInOrder.Count == 0) return;

        var entries = await _repository.GetEntriesAsync(projectId, workItemIdsInOrder, ct);

        if (entries.Count == 0) return;

        var byWorkItem = entries.ToDictionary(entry => entry.WorkItemId);

        /*
         * Measured once, before anything moves.
         *
         * The items being ranked are themselves in the backlog and usually hold the current
         * maximum — they were appended moments ago by `EnsureEntryAsync`. Reading the max inside
         * the loop would chase their own new ranks upward.
         */
        var rank = await _repository.GetMaxRankAsync(projectId, ct);

        foreach (var workItemId in workItemIdsInOrder)
        {
            if (!byWorkItem.TryGetValue(workItemId, out var entry)) continue;

            entry.Rank = Ranking.Between(rank, null);
            rank = entry.Rank;
        }

        await _repository.SaveChangesAsync(ct);
    }

    public async Task EnsureEntryAsync(
        Guid projectId,
        Guid workItemId,
        Guid addedBy,
        CancellationToken ct = default)
    {
        if (await _repository.GetEntryAsync(projectId, workItemId, ct) is not null) return;

        _repository.Add(new BacklogItem
        {
            ProjectId = projectId,
            WorkItemId = workItemId,
            Rank = Ranking.Between(
                await _repository.GetMaxRankAsync(projectId, ct), null),
            CreatedBy = addedBy
        });

        await _repository.SaveChangesAsync(ct);
    }

    public async Task<int> AssignSprintAsync(
        Guid sprintId,
        IReadOnlyCollection<Guid> workItemIds,
        CancellationToken ct = default)
    {
        if (workItemIds.Count == 0) return 0;

        var entries =
            await _repository.GetEntriesByWorkItemsAsync(workItemIds, ct);

        foreach (var entry in entries)
            entry.SprintId = sprintId;

        await _repository.SaveChangesAsync(ct);

        return entries.Count;
    }
}
