using BoardSync.Api.Modules.Intelligence.DTOs;

namespace BoardSync.Api.Modules.Intelligence.Domain;

/// <summary>
/// Works out which proposed nodes an acceptance creates, and in what order.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>ProposalService</c> because it is the rule, not the plumbing: a pure function
/// over a tree, and the part of acceptance most likely to be wrong in a way nothing else notices.
/// A selection that silently drops a node produces a board missing work somebody thought they
/// accepted, which is invisible until the sprint is short.
/// </para>
/// </remarks>
public static class ProposalSelection
{
    /// <summary>One node to create, and the node that will be its parent.</summary>
    /// <param name="ParentId">Null for a node created at the top level.</param>
    public readonly record struct Selected(ProposedNode Node, string? ParentId);

    /// <summary>
    /// The nodes to create, ordered so every parent precedes its children.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Selecting a node selects its ancestors.</b> A User Story cannot be created under a
    /// Feature that was not accepted — the parent would not exist. The alternatives were worse:
    /// refusing the selection makes the reviewer reconstruct the tree by hand, and quietly
    /// reparenting the story to the top level changes what it means, because a story's parent is
    /// most of its context. Recorded in <c>docs/adr-002-proposals.md</c>.
    /// </para>
    /// <para>
    /// An empty <paramref name="include"/> means the whole draft, which is the common case — most
    /// reviewers take a draft whole or reject it.
    /// </para>
    /// <para>
    /// Selecting a node does <b>not</b> select its descendants. Accepting an epic and getting
    /// forty tasks nobody looked at is the failure this module exists to prevent; a reviewer who
    /// wants the subtree selects it.
    /// </para>
    /// </remarks>
    public static List<Selected> Resolve(
        IReadOnlyList<ProposedNode> roots,
        IReadOnlyList<string> include)
    {
        var wanted = include.Count == 0 ? null : new HashSet<string>(include);

        var ordered = new List<Selected>();

        foreach (var root in roots) Walk(root, parentId: null);

        return ordered;

        void Walk(ProposedNode node, string? parentId)
        {
            /*
             * Included when chosen, or when something below it was — the second is what carrying
             * ancestors along actually means. An ancestor pulled in this way is created so its
             * descendant has a parent, and it is a real work item either way: a Feature nobody
             * ticked but whose Story they did is still work the team took on.
             */
            var included = wanted is null
                || wanted.Contains(node.Id)
                || HasWantedDescendant(node, wanted);

            if (included) ordered.Add(new Selected(node, parentId));

            foreach (var child in node.Children)
            {
                // A child's parent is this node only if this node was created. When it was not,
                // nothing below it was either, and the recursion adds nothing.
                Walk(child, included ? node.Id : parentId);
            }
        }
    }

    private static bool HasWantedDescendant(ProposedNode node, HashSet<string> wanted) =>
        node.Children.Any(child =>
            wanted.Contains(child.Id) || HasWantedDescendant(child, wanted));

    /// <summary>
    /// The leaves of a resolved selection — the nodes nothing else in it hangs off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a sprint should actually hold. A parent and its children in the same sprint count the
    /// same work twice: an epic carrying thirteen points over three five-point stories commits the
    /// sprint to twenty-eight, and every figure downstream — the burndown, the velocity, the
    /// completion rate — is wrong by the difference for as long as anybody keeps the record.
    /// </para>
    /// <para>
    /// Leaf of the <i>accepted</i> tree, not of the draft. Someone who takes an epic and none of
    /// its stories has chosen to schedule the epic, and it is the only thing there is to schedule.
    /// </para>
    /// </remarks>
    public static List<ProposedNode> Leaves(IReadOnlyList<Selected> selected)
    {
        var parents = selected
            .Select(entry => entry.ParentId)
            .Where(id => id is not null)
            .ToHashSet();

        return [.. selected
            .Where(entry => !parents.Contains(entry.Node.Id))
            .Select(entry => entry.Node)];
    }

    /// <summary>
    /// The leaves in delivery order: by phase, then by their order in the tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the backlog is ranked by after an acceptance, and what decides which items the
    /// first sprint is offered. The tree order inside a phase is the model's own — it wrote the
    /// siblings in an order, and there is no better tiebreak available.
    /// </para>
    /// <para>
    /// A leaf with no phase sorts last. <c>DecompositionGuard</c> gives every leaf one, so this only
    /// arises for a proposal drafted before phases existed — where "all of it, in tree order" is
    /// exactly right.
    /// </para>
    /// </remarks>
    public static List<ProposedNode> LeavesInDeliveryOrder(IReadOnlyList<Selected> selected)
    {
        var leaves = Leaves(selected);

        // OrderBy is stable, so equal phases keep the tree order Leaves produced.
        return [.. leaves.OrderBy(node => node.Phase ?? int.MaxValue)];
    }

    /// <summary>The leaves in the first phase that was accepted — what a first sprint holds.</summary>
    /// <remarks>
    /// The <i>first accepted</i> phase, not phase 1. A reviewer who unticks everything in the first
    /// phase has said that work is not happening, and offering them an empty sprint would be a
    /// worse answer than offering the earliest work they did keep.
    /// </remarks>
    public static List<ProposedNode> FirstPhase(IReadOnlyList<Selected> selected)
    {
        var ordered = LeavesInDeliveryOrder(selected);

        if (ordered.Count == 0) return [];

        var first = ordered[0].Phase;

        return [.. ordered.TakeWhile(node => node.Phase == first)];
    }
}
