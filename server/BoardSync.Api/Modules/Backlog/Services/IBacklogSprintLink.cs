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
}
