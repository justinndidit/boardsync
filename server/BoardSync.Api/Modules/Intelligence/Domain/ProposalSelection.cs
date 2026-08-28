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
}
