using BoardSync.Api.Modules.Intelligence.DTOs;
using BoardSync.Api.Modules.WorkItems.Domain;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.Intelligence.Domain;

/// <summary>
/// Checks a proposed hierarchy before a human ever sees it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The counterpart to <see cref="NarrativeGuard"/>, for the other thing a model produces here.</b>
/// A narrative can invent a figure; a decomposition can invent a <em>shape</em> — a task nested under
/// an epic, a title the column cannot hold, four hundred nodes from a two-page document, an estimate
/// of nine hundred points. None of that is caught by structured output, which constrains the JSON
/// schema and has no opinion about whether <c>Epic → Task</c> is a legal parenting in this domain.
/// </para>
/// <para>
/// It normalizes what it safely can and rejects what it cannot. The distinction is whether the fix
/// preserves the author's meaning: trimming trailing whitespace does, and silently reparenting a
/// task under a fabricated story does not — that would invent structure and present it as the
/// model's suggestion.
/// </para>
/// <para>
/// <b>What it does not catch.</b> It checks that a tree is well-formed, not that it is a good
/// decomposition. A structurally perfect breakdown that misreads the PRD, omits a requirement, or
/// splits work along the wrong seams passes every check here. That judgment is the human's, which
/// is the entire reason acceptance exists.
/// </para>
/// </remarks>
public static class DecompositionGuard
{
    /// <summary>
    /// The most nodes a single proposal may contain.
    /// </summary>
    /// <remarks>
    /// A cap on review cost, not on model output. Nobody meaningfully reviews three hundred
    /// proposed work items, and accepting them unreviewed is the failure mode §8.1 describes: a
    /// board that silently gains work nobody chose. A PRD that genuinely needs more than this
    /// wants splitting before it wants decomposing.
    /// </remarks>
    public const int MaxNodes = 150;

    /// <summary>Matches <c>CreateWorkItemRequest.Title</c>. Longer titles are truncated, not rejected.</summary>
    private const int MaxTitleLength = 255;

    /// <summary>Matches <c>CreateWorkItemRequest.Description</c>.</summary>
    private const int MaxDescriptionLength = 10_000;

    /// <summary>Matches the <c>[Range(0, 1000)]</c> on story points.</summary>
    private const int MaxStoryPoints = 1000;

    /// <summary>
    /// Most delivery phases a plan may propose.
    /// </summary>
    /// <remarks>
    /// Past this it is not a plan, it is the item list with headings. The prompt asks for two to
    /// six; this is the outer bound, not the target.
    /// </remarks>
    public const int MaxPhases = 8;

    /// <summary>A checked draft, or the reason there is not one.</summary>
    /// <param name="Draft">Normalized and safe to store. Null when <paramref name="Rejection"/> is set.</param>
    /// <param name="Rejection">Why the tree could not be used, phrased for the requester.</param>
    /// <param name="Repairs">
    /// What was normalized on the way through. Surfaced rather than silent, so a reviewer knows the
    /// draft is not verbatim.
    /// </param>
    public readonly record struct Result(
        Decomposition? Draft,
        string? Rejection,
        IReadOnlyList<string> Repairs)
    {
        public bool Accepted => Draft is not null;
    }

    /// <summary>
    /// Validates and normalizes a tree the model produced.
    /// </summary>
    public static Result Check(Decomposition candidate)
    {
        if (candidate.Roots.Count == 0)
            return new Result(null, "The model returned no work items for this document.", []);

        var repairs = new List<string>();

        var total = Count(candidate.Roots);

        if (total > MaxNodes)
        {
            return new Result(
                null,
                $"The decomposition contained {total} work items, beyond the {MaxNodes} a single " +
                "proposal may hold. Decompose one section at a time.",
                []);
        }

        /*
         * A root must be a type that can own children, or a leaf standing alone.
         *
         * Every type is legal at the root — a small document really can decompose to a handful of
         * tasks — so there is nothing to reject here. The nesting below is where the rules bite.
         */
        var next = 1;

        foreach (var root in candidate.Roots)
        {
            var rejection = Normalize(root, parent: null, repairs, ref next);

            if (rejection is not null)
                return new Result(null, rejection, []);
        }

        return new Result(
            new Decomposition(
                candidate.Roots,
                Trim(candidate.Notes),
                Phases(candidate, repairs)),
            null,
            repairs);
    }

    /// <summary>
    /// The delivery phases, repaired into something every leaf can index into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Repaired rather than rejected.</b> Phasing is advice: it changes which sprint a reviewer
    /// is offered and the order of a backlog, and a reviewer sees all of it before anything is
    /// created. Throwing away a whole correct hierarchy because the model numbered a phase wrongly
    /// would spend the allowance again to fix something nobody would have been misled by — unlike
    /// the nesting rules above, which decide what actually gets written.
    /// </para>
    /// <para>
    /// Three repairs, all silent-but-recorded: too many phases collapse to the cap, a leaf naming a
    /// phase that does not exist falls to the last one, and a draft with no phases at all becomes a
    /// single unnamed phase — which is the honest reading of "this is one go's worth of work".
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ProposedPhase> Phases(
        Decomposition candidate, List<string> repairs)
    {
        var phases = (candidate.Phases ?? [])
            .Where(phase => !string.IsNullOrWhiteSpace(phase.Name))
            .Select(phase => new ProposedPhase
            {
                Name = phase.Name.Trim(),
                Rationale = string.IsNullOrWhiteSpace(phase.Rationale)
                    ? null
                    : phase.Rationale.Trim(),
            })
            .ToList();

        if (phases.Count > MaxPhases)
        {
            repairs.Add(
                $"It proposed {phases.Count} delivery phases; the last "
                + $"{phases.Count - MaxPhases} were merged into phase {MaxPhases}.");

            phases = [.. phases.Take(MaxPhases)];
        }

        if (phases.Count == 0)
        {
            // No phases is not an error. It is what a document worth one sprint looks like, and
            // what every proposal drafted before phasing existed looks like.
            phases = [new ProposedPhase { Name = "All of it" }];
        }

        foreach (var node in Flatten(candidate.Roots))
        {
            if (node.Children.Count > 0)
            {
                // Containers span their children's phases by definition.
                node.Phase = null;
                continue;
            }

            node.Phase = node.Phase is int phase && phase >= 1 && phase <= phases.Count
                ? phase
                : phases.Count;
        }

        return phases;
    }

    /// <summary>Every node in the tree, parents before children.</summary>
    private static IEnumerable<ProposedNode> Flatten(IEnumerable<ProposedNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;

            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }

    /// <summary>
    /// Normalizes one node in place and recurses, returning a rejection reason or null.
    /// </summary>
    /// <remarks>
    /// In place because the tree is ours the moment it is deserialized — nothing else holds a
    /// reference to it, and rebuilding it would double the allocation for no gain in clarity.
    /// </remarks>
    private static string? Normalize(
        ProposedNode node,
        WorkItemType? parent,
        List<string> repairs,
        ref int next)
    {
        // Ours, not the model's — see the remark on ProposedNode.Id.
        node.Id = $"n{next++}";

        var title = node.Title?.Trim() ?? string.Empty;

        if (title.Length == 0)
        {
            return "The decomposition contained a work item with no title.";
        }

        if (title.Length > MaxTitleLength)
        {
            title = title[..MaxTitleLength];
            repairs.Add($"Truncated an over-long title to {MaxTitleLength} characters.");
        }

        node.Title = title;

        var description = node.Description?.Trim();

        if (description is { Length: > MaxDescriptionLength })
        {
            description = description[..MaxDescriptionLength];
            repairs.Add($"Truncated an over-long description to {MaxDescriptionLength} characters.");
        }

        node.Description = string.IsNullOrEmpty(description) ? null : description;

        /*
         * An estimate outside the range the column accepts is dropped rather than clamped.
         *
         * Clamping 900 to 1000 keeps a number that was never meant, and story points are read as a
         * judgment about size — a wrong one is worse than an absent one, which at least reads as
         * "not estimated".
         */
        if (node.StoryPoints is { } points && (points < 0 || points > MaxStoryPoints))
        {
            node.StoryPoints = null;
            repairs.Add($"Dropped an out-of-range estimate of {points} points.");
        }

        /*
         * The nesting rule, and the reason this class exists.
         *
         * Structured output guarantees a `type` field holding one of five names. It says nothing
         * about whether a Task may sit under an Epic, which the domain forbids — and if one reached
         * acceptance, `WorkItemService.CreateAsync` would throw partway through building the tree,
         * after some of it already existed.
         */
        if (parent is { } parentType && !WorkItemHierarchy.CanNest(parentType, node.Type))
        {
            return $"The decomposition nested a {node.Type} under a {parentType}, which this " +
                   $"board does not allow. Valid hierarchy: {WorkItemHierarchy.Description}.";
        }

        /*
         * Identical siblings collapse.
         *
         * A model asked for a breakdown will sometimes restate the same task under two headings.
         * Two work items with the same title under the same parent are indistinguishable on a
         * board, and somebody would close one and wonder why the other stayed open.
         */
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<ProposedNode>(node.Children.Count);

        foreach (var child in node.Children)
        {
            if (child.Title?.Trim() is { Length: > 0 } childTitle && !seen.Add(childTitle))
            {
                repairs.Add($"Removed a duplicate '{childTitle}' under '{node.Title}'.");
                continue;
            }

            kept.Add(child);
        }

        node.Children = kept;

        foreach (var child in node.Children)
        {
            var rejection = Normalize(child, node.Type, repairs, ref next);

            if (rejection is not null) return rejection;
        }

        return null;
    }

    private static int Count(IReadOnlyList<ProposedNode> nodes) =>
        nodes.Sum(n => 1 + Count(n.Children));

    private static IReadOnlyList<string> Trim(IReadOnlyList<string> notes) =>
        [.. notes.Select(n => n?.Trim() ?? string.Empty).Where(n => n.Length > 0)];
}
