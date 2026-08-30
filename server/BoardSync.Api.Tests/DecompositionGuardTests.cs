using BoardSync.Api.Modules.Intelligence.Domain;
using BoardSync.Api.Modules.Intelligence.DTOs;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Tests;

/// <summary>
/// The structural checks a model's decomposition has to pass before a human sees it.
/// </summary>
/// <remarks>
/// These are the rules structured output cannot express. A JSON schema constrains the shape of the
/// response; it has no opinion about whether a Task may sit under an Epic, and the domain does.
/// </remarks>
public class DecompositionGuardTests
{
    private static ProposedNode Node(
        WorkItemType type,
        string title,
        params ProposedNode[] children) => new()
    {
        Title = title,
        Type = type,
        Children = [.. children],
    };

    private static Decomposition Tree(params ProposedNode[] roots) => new(roots, []);

    [Fact]
    public void Accepts_a_well_formed_hierarchy()
    {
        var result = DecompositionGuard.Check(Tree(
            Node(WorkItemType.Epic, "Billing",
                Node(WorkItemType.Feature, "Invoices",
                    Node(WorkItemType.UserStory, "Download an invoice",
                        Node(WorkItemType.Task, "Render the PDF"))))));

        Assert.True(result.Accepted, result.Rejection);
        Assert.Null(result.Rejection);
    }

    [Fact]
    public void Rejects_a_task_nested_under_an_epic()
    {
        // The case the schema cannot catch, and the reason this class exists: it would have thrown
        // partway through acceptance, after part of the tree already existed on the board.
        var result = DecompositionGuard.Check(Tree(
            Node(WorkItemType.Epic, "Billing",
                Node(WorkItemType.Task, "Render the PDF"))));

        Assert.False(result.Accepted);
        Assert.Contains("Task", result.Rejection);
        Assert.Contains("Epic", result.Rejection);
    }

    [Fact]
    public void Rejects_nesting_under_a_leaf()
    {
        var result = DecompositionGuard.Check(Tree(
            Node(WorkItemType.UserStory, "Download an invoice",
                Node(WorkItemType.Task, "Render the PDF",
                    Node(WorkItemType.Task, "Pick a font")))));

        Assert.False(result.Accepted);
    }

    [Fact]
    public void Allows_any_type_at_the_root()
    {
        // A small document really can decompose to a handful of tasks, and forcing an epic over the
        // top of it invents a layer nobody asked for.
        var result = DecompositionGuard.Check(Tree(
            Node(WorkItemType.Task, "Rotate the signing key")));

        Assert.True(result.Accepted, result.Rejection);
    }

    [Fact]
    public void Rejects_an_empty_tree()
    {
        Assert.False(DecompositionGuard.Check(Tree()).Accepted);
    }

    [Fact]
    public void Rejects_a_node_with_no_title()
    {
        var result = DecompositionGuard.Check(Tree(Node(WorkItemType.Task, "   ")));

        Assert.False(result.Accepted);
        Assert.Contains("no title", result.Rejection);
    }

    [Fact]
    public void Rejects_a_tree_beyond_the_review_limit()
    {
        var roots = Enumerable
            .Range(0, DecompositionGuard.MaxNodes + 1)
            .Select(i => Node(WorkItemType.Task, $"Task {i}"))
            .ToArray();

        var result = DecompositionGuard.Check(Tree(roots));

        Assert.False(result.Accepted);
        Assert.Contains(DecompositionGuard.MaxNodes.ToString(), result.Rejection);
    }

    [Fact]
    public void Assigns_its_own_ids_rather_than_trusting_the_model()
    {
        var tree = Tree(
            Node(WorkItemType.Epic, "Billing",
                Node(WorkItemType.Feature, "Invoices")));

        var result = DecompositionGuard.Check(tree);

        var root = result.Draft!.Roots[0];

        Assert.False(string.IsNullOrWhiteSpace(root.Id));
        Assert.NotEqual(root.Id, root.Children[0].Id);
    }

    [Fact]
    public void Truncates_an_over_long_title_and_says_so()
    {
        var result = DecompositionGuard.Check(Tree(
            Node(WorkItemType.Task, new string('x', 400))));

        Assert.True(result.Accepted, result.Rejection);
        Assert.Equal(255, result.Draft!.Roots[0].Title.Length);
        Assert.Single(result.Repairs, r => r.Contains("title"));
    }

    [Fact]
    public void Drops_an_out_of_range_estimate_rather_than_clamping_it()
    {
        // Clamping 9000 to 1000 keeps a number nobody meant. Story points read as a judgment about
        // size, and a wrong one is worse than an absent one.
        var node = Node(WorkItemType.Task, "Render the PDF");
        node.StoryPoints = 9000;

        var result = DecompositionGuard.Check(Tree(node));

        Assert.True(result.Accepted, result.Rejection);
        Assert.Null(result.Draft!.Roots[0].StoryPoints);
        Assert.Single(result.Repairs, r => r.Contains("estimate"));
    }

    [Fact]
    public void Keeps_a_valid_estimate()
    {
        var node = Node(WorkItemType.Task, "Render the PDF");
        node.StoryPoints = 5;

        Assert.Equal(5, DecompositionGuard.Check(Tree(node)).Draft!.Roots[0].StoryPoints);
    }

    [Fact]
    public void Collapses_identical_siblings()
    {
        var result = DecompositionGuard.Check(Tree(
            Node(WorkItemType.UserStory, "Download an invoice",
                Node(WorkItemType.Task, "Render the PDF"),
                Node(WorkItemType.Task, "render the pdf"))));

        Assert.Single(result.Draft!.Roots[0].Children);
        Assert.Single(result.Repairs, r => r.Contains("duplicate"));
    }

    [Fact]
    public void Keeps_the_same_title_under_different_parents()
    {
        // "Write tests" under two different stories is two real pieces of work, not a duplicate.
        var result = DecompositionGuard.Check(Tree(
            Node(WorkItemType.Feature, "Invoices",
                Node(WorkItemType.UserStory, "Download an invoice",
                    Node(WorkItemType.Task, "Write tests")),
                Node(WorkItemType.UserStory, "Email an invoice",
                    Node(WorkItemType.Task, "Write tests")))));

        Assert.True(result.Accepted, result.Rejection);
        Assert.Equal(2, result.Draft!.Roots[0].Children.Count);
    }

    [Fact]
    public void Discards_blank_notes()
    {
        var result = DecompositionGuard.Check(
            new Decomposition([Node(WorkItemType.Task, "Rotate the key")], ["  ", "Unclear scope"]));

        Assert.Equal("Unclear scope", Assert.Single(result.Draft!.Notes));
    }

    /// <summary>
    /// Phasing is repaired, not rejected.
    /// </summary>
    /// <remarks>
    /// It is advice — it decides which sprint a reviewer is offered and the order of a backlog, and
    /// they see all of it before anything is created. Throwing away a correct hierarchy over a
    /// mis-numbered phase would spend the allowance again to fix something nobody was misled by.
    /// </remarks>
    [Fact]
    public void ALeafNamingAPhaseThatDoesNotExistFallsToTheLast()
    {
        var result = DecompositionGuard.Check(new Decomposition(
            [new ProposedNode { Title = "Ship it", Type = WorkItemType.Task, Phase = 9 }],
            [],
            [new ProposedPhase { Name = "Only phase" }]));

        Assert.NotNull(result.Draft);
        Assert.Equal(1, result.Draft!.Roots[0].Phase);
    }

    /// <summary>Too many phases collapse to the cap rather than failing the draft.</summary>
    [Fact]
    public void MorePhasesThanTheCapAreMerged()
    {
        var result = DecompositionGuard.Check(new Decomposition(
            [new ProposedNode { Title = "Ship it", Type = WorkItemType.Task, Phase = 1 }],
            [],
            [.. Enumerable.Range(1, DecompositionGuard.MaxPhases + 4)
                .Select(n => new ProposedPhase { Name = $"Phase {n}" })]));

        Assert.NotNull(result.Draft);
        Assert.Equal(DecompositionGuard.MaxPhases, result.Draft!.Phases!.Count);
        Assert.Contains(result.Repairs, r => r.Contains("delivery phases"));
    }

    /// <summary>
    /// Containers carry no phase, whatever the model said.
    /// </summary>
    /// <remarks>
    /// An epic spans its children's phases by definition, so a phase on it is a fourth number that
    /// can disagree with the other three.
    /// </remarks>
    [Fact]
    public void AContainerCarriesNoPhase()
    {
        var result = DecompositionGuard.Check(new Decomposition(
            [
                new ProposedNode
                {
                    Title = "Billing",
                    Type = WorkItemType.Epic,
                    Phase = 1,
                    Children =
                    [
                        new ProposedNode { Title = "Invoices", Type = WorkItemType.Feature, Phase = 2 },
                    ],
                },
            ],
            [],
            [new ProposedPhase { Name = "A" }, new ProposedPhase { Name = "B" }]));

        Assert.Null(result.Draft!.Roots[0].Phase);
        Assert.Equal(2, result.Draft.Roots[0].Children[0].Phase);
    }

    /// <summary>A phase with no name is not a phase.</summary>
    [Fact]
    public void UnnamedPhasesAreDropped()
    {
        var result = DecompositionGuard.Check(new Decomposition(
            [new ProposedNode { Title = "Ship it", Type = WorkItemType.Task, Phase = 1 }],
            [],
            [new ProposedPhase { Name = "  " }, new ProposedPhase { Name = "Real" }]));

        Assert.Equal("Real", Assert.Single(result.Draft!.Phases!).Name);
    }
}
