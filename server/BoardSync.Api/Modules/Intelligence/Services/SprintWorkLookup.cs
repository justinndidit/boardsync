using BoardSync.Api.Data;
using BoardSync.Api.Modules.Intelligence.DTOs;
using BoardSync.Api.Modules.WorkItems.Models;

using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Intelligence.Services;

/// <summary>
/// What a sprint actually contained, in the shape the narrator is allowed to see.
/// </summary>
/// <remarks>
/// A port rather than a direct <see cref="BoardSyncDbContext"/> dependency because everything else
/// in <see cref="NarrativeService"/> — the allowance, the grounding check, what happens when either
/// says no — is deterministic and tested without a database. Reaching for the context inside the
/// service would drag a container into those tests to prove things that have nothing to do with
/// storage.
/// </remarks>
public interface ISprintWorkLookup
{
    /// <summary>Delivered and unfinished work in the sprint, each list capped.</summary>
    Task<SprintWork> ForSprintAsync(Guid sprintId, CancellationToken ct = default);
}

/// <summary>The work a sprint contained, split by whether it finished.</summary>
/// <remarks>
/// Delivered means <c>Closed</c> — through the QA gate, not merely merged. Everything else counts
/// as unfinished, <c>Resolved</c> included: work waiting on a tester has not landed from a reader's
/// point of view, and a report that calls it shipped is the one that makes people stop trusting the
/// QA figure.
/// </remarks>
public sealed record SprintWork(
    IReadOnlyList<NarratedItem> Delivered,
    IReadOnlyList<NarratedItem> Unfinished)
{
    /// <summary>A sprint with nothing in it, or one that could not be read.</summary>
    public static readonly SprintWork Empty = new([], []);

    /// <summary>Every reference the narrator may name, for the grounding check.</summary>
    public IReadOnlyList<string> References =>
        [.. Delivered.Concat(Unfinished).Select(item => item.Reference)];
}

/// <inheritdoc cref="ISprintWorkLookup"/>
public sealed class SprintWorkLookup : ISprintWorkLookup
{
    /// <summary>
    /// How many items of each kind the narrator is told about.
    /// </summary>
    /// <remarks>
    /// A sprint of two hundred items would otherwise put two hundred titles into a prompt to produce
    /// a few paragraphs — and a report that lists everything is not a report. The cap is per list,
    /// so a sprint that delivered forty things and dropped two still shows both.
    /// </remarks>
    private const int MaxItemsPerList = 40;

    private readonly BoardSyncDbContext _context;

    public SprintWorkLookup(BoardSyncDbContext context) => _context = context;

    public async Task<SprintWork> ForSprintAsync(
        Guid sprintId, CancellationToken ct = default)
    {
        var items = await _context.SprintWorkItems
            .Where(sw => sw.SprintId == sprintId)
            .Join(
                _context.WorkItems.Where(w => w.IsActive),
                sw => sw.WorkItemId,
                w => w.Id,
                (sw, w) => new ItemRow(w.ProjectId, w.Number, w.Title, w.State))
            .ToListAsync(ct);

        if (items.Count == 0) return SprintWork.Empty;

        // One lookup for the keys: every item in a project shares one, and joining it per row
        // would ship the same short string once for each item.
        var projectIds = items.Select(i => i.ProjectId).Distinct().ToList();

        var keys = await _context.Projects
            .Where(p => projectIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Key })
            .ToDictionaryAsync(p => p.Id, p => p.Key, ct);

        // Ordered by number so the same sprint reads the same way twice, and so a capped list is
        // a stable prefix rather than whatever the database happened to return.
        IReadOnlyList<NarratedItem> Narrate(IEnumerable<ItemRow> rows) =>
        [
            .. rows
                .OrderBy(row => row.Number)
                .Take(MaxItemsPerList)
                .Select(row => new NarratedItem(
                    $"{keys.GetValueOrDefault(row.ProjectId, "?")}-{row.Number}",
                    row.Title,
                    row.State.ToString()))
        ];

        return new SprintWork(
            Narrate(items.Where(i => i.State == WorkItemState.Closed)),
            Narrate(items.Where(i => i.State != WorkItemState.Closed)));
    }

    private sealed record ItemRow(
        Guid ProjectId, int Number, string Title, WorkItemState State);
}
