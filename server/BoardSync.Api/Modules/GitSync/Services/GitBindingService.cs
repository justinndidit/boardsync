using BoardSync.Api.Data;
using BoardSync.Api.Modules.GitSync.Domain;
using BoardSync.Api.Modules.GitSync.Providers;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.GitSync.Services;

/// <summary>A work item a git event referred to, resolved.</summary>
/// <param name="WorkItemId">The item.</param>
/// <param name="ProjectId">Its project — one the repository is linked to.</param>
/// <param name="Reference">What the developer actually typed, for the audit trail.</param>
public readonly record struct BoundWorkItem(Guid WorkItemId, Guid ProjectId, WorkItemReference Reference);

/// <summary>
/// Turns the references in a git event into work items it is allowed to move.
/// </summary>
public interface IGitBindingService
{
    /// <summary>
    /// The work items this event refers to that live in one of <paramref name="linkedProjectIds"/>.
    /// </summary>
    /// <remarks>
    /// References to anything else are dropped silently — see the note on the implementation.
    /// </remarks>
    Task<IReadOnlyList<BoundWorkItem>> ResolveAsync(
        NormalizedGitEvent gitEvent,
        IReadOnlyCollection<Guid> linkedProjectIds,
        CancellationToken ct = default);
}

/// <inheritdoc />
public class GitBindingService : IGitBindingService
{
    private readonly BoardSyncDbContext _context;
    private readonly ILogger<GitBindingService> _logger;

    public GitBindingService(BoardSyncDbContext context, ILogger<GitBindingService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Most references honoured from one event.
    /// </summary>
    /// <remarks>
    /// A 500-commit push is a real thing — a rebase, a squash-merge of a long branch, an import — and
    /// binding every reference in one would write hundreds of history rows for a single act that
    /// nobody wants to read. The cap is generous enough that no ordinary push reaches it.
    /// </remarks>
    private const int MaxReferencesPerEvent = 50;

    public async Task<IReadOnlyList<BoundWorkItem>> ResolveAsync(
        NormalizedGitEvent gitEvent,
        IReadOnlyCollection<Guid> linkedProjectIds,
        CancellationToken ct = default)
    {
        if (linkedProjectIds.Count == 0) return [];

        var references = WorkItemReferences.FromEvent(gitEvent);

        if (references.Count == 0) return [];

        if (references.Count > MaxReferencesPerEvent)
        {
            _logger.LogInformation(
                "Event on {Repository} referenced {Count} work items; honouring the first {Cap}.",
                gitEvent.RepositoryName, references.Count, MaxReferencesPerEvent);

            references = [.. references.Take(MaxReferencesPerEvent)];
        }

        var projectIds = linkedProjectIds as List<Guid> ?? [.. linkedProjectIds];
        var keys = references.Select(r => r.ProjectKey).Distinct().ToList();
        var numbers = references.Select(r => r.Number).Distinct().ToList();

        // One query for every reference rather than one per reference, and scoped to the linked
        // projects in the predicate rather than filtered afterwards — so a reference to a project
        // this repository does not feed never even loads.
        //
        // The (key, number) pairs are matched in memory below because SQL cannot express "these
        // specific pairs" without a VALUES join; the two IN lists over-fetch slightly and the
        // over-fetch is bounded by the reference cap.
        var candidates = await _context.WorkItems
            .Where(w => w.IsActive && numbers.Contains(w.Number))
            .Join(
                _context.Projects.Where(p => projectIds.Contains(p.Id) && keys.Contains(p.Key)),
                w => w.ProjectId,
                p => p.Id,
                (w, p) => new { w.Id, w.ProjectId, w.Number, p.Key })
            .ToListAsync(ct);

        var bound = new List<BoundWorkItem>();

        foreach (var reference in references)
        {
            var match = candidates.FirstOrDefault(
                c => c.Key == reference.ProjectKey && c.Number == reference.Number);

            if (match is null)
            {
                // Not an error, and deliberately not logged as one. A developer referencing a ticket
                // in another tool, a typo, or a project this repository does not feed all land here,
                // and all of them are things people do. What matters is that they are visible — the
                // delivery's outcome records how many references went unresolved.
                _logger.LogDebug(
                    "Reference {Reference} on {Repository} matched no work item in a linked project.",
                    reference, gitEvent.RepositoryName);

                continue;
            }

            bound.Add(new BoundWorkItem(match.Id, match.ProjectId, reference));
        }

        return bound;
    }
}
