using BoardSync.Api.Modules.Intelligence.Domain;
using BoardSync.Api.Modules.Intelligence.DTOs;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Tests;

/// <summary>
/// Which nodes an acceptance creates, and in what order.
/// </summary>
/// <remarks>
/// The part of acceptance most likely to be wrong in a way nothing else notices. A selection that
/// silently drops a node produces a board missing work somebody believed they accepted, and that is
/// invisible until the sprint comes up short.
/// </remarks>
public class ProposalSelectionTests
{
    /// <summary>A four-level tree with ids matching the guard's numbering: n1…n4.</summary>
    private static IReadOnlyList<ProposedNode> Tree()
    {
        var draft = new Decomposition(
        [
            new ProposedNode
            {
                Title = "Billing",
                Type = WorkItemType.Epic,
                Children =
                [
                    new ProposedNode
                    {
                        Title = "Invoices",
                        Type = WorkItemType.Feature,
                        Children =
                        [
                            new ProposedNode
                            {
                                Title = "Download an invoice",
                                Type = WorkItemType.UserStory,
                                Children =
                                [
                                    new ProposedNode { Title = "Render the PDF", Type = WorkItemType.Task },
                                ],
                            },
                        ],
                    },
                ],
            },
        ], []);

        // Through the guard, so the ids are the ones acceptance will actually be given.
        return DecompositionGuard.Check(draft).Draft!.Roots;
    }

    [Fact]
    public void An_empty_selection_takes_the_whole_draft()
    {
        var selected = ProposalSelection.Resolve(Tree(), []);

        Assert.Equal(4, selected.Count);
    }

    [Fact]
    public void Parents_always_precede_their_children()
    {
        // Acceptance maps a node id to the work item it became, so a child created before its
        // parent would have nothing to point at.
        var selected = ProposalSelection.Resolve(Tree(), []);

        var positions = selected
            .Select((s, index) => (s.Node.Id, s.ParentId, index))
            .ToDictionary(x => x.Id, x => (x.ParentId, x.index));

        foreach (var (_, (parentId, index)) in positions)
        {
            if (parentId is null) continue;

            Assert.True(positions[parentId].index < index);
        }
    }

    [Fact]
    public void The_roots_parent_is_null()
    {
        var selected = ProposalSelection.Resolve(Tree(), []);

        Assert.Null(selected[0].ParentId);
    }

    [Fact]
    public void Selecting_a_leaf_carries_its_ancestors()
    {
        // The rule from the ADR. A task cannot be created under a story that was not accepted, so
        // choosing the task chooses the chain above it.
        var selected = ProposalSelection.Resolve(Tree(), ["n4"]);

        Assert.Equal(4, selected.Count);
        Assert.Equal(["n1", "n2", "n3", "n4"], selected.Select(s => s.Node.Id));
    }

    [Fact]
    public void Selecting_a_parent_does_not_pull_in_its_children()
    {
        // The other half, and the one that protects the reviewer: accepting an epic must not
        // silently create forty descendants nobody looked at.
        var selected = ProposalSelection.Resolve(Tree(), ["n1"]);

        Assert.Equal(["n1"], selected.Select(s => s.Node.Id));
    }

    [Fact]
    public void Selecting_a_middle_node_takes_it_and_its_ancestors_only()
    {
        var selected = ProposalSelection.Resolve(Tree(), ["n3"]);

        Assert.Equal(["n1", "n2", "n3"], selected.Select(s => s.Node.Id));
    }

    [Fact]
    public void An_unknown_id_selects_nothing()
    {
        // Not an error here — ProposalService rejects an empty selection with a message. What
        // matters is that a stale id does not quietly select the whole tree.
        var selected = ProposalSelection.Resolve(Tree(), ["nonexistent"]);

        Assert.Empty(selected);
    }

    [Fact]
    public void A_skipped_branch_does_not_reparent_the_branch_beside_it()
    {
        var roots = DecompositionGuard.Check(new Decomposition(
        [
            new ProposedNode
            {
                Title = "Billing",
                Type = WorkItemType.Epic,
                Children =
                [
                    new ProposedNode { Title = "Invoices", Type = WorkItemType.Feature },
                    new ProposedNode { Title = "Refunds", Type = WorkItemType.Feature },
                ],
            },
        ], [])).Draft!.Roots;

        // n1 Billing, n2 Invoices, n3 Refunds — take only Refunds.
        var selected = ProposalSelection.Resolve(roots, ["n3"]);

        Assert.Equal(["n1", "n3"], selected.Select(s => s.Node.Id));

        // Refunds still hangs off Billing, not off the top level.
        Assert.Equal("n1", selected.Single(s => s.Node.Id == "n3").ParentId);
    }
}
