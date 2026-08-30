using System.Text.Json;

using BoardSync.Api.Data;
using BoardSync.Api.Modules.Intelligence.Models;

using Microsoft.Extensions.DependencyInjection;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// Accepting a proposal — the only thing in the Intelligence module that writes to the board.
/// </summary>
/// <remarks>
/// <para>
/// Needs the real database and the real pipeline. The failure this class was written for could not
/// happen anywhere smaller: the accept path opened its own transaction while the connection is
/// configured with <c>EnableRetryOnFailure</c>, and <c>NpgsqlRetryingExecutionStrategy</c> throws
/// <c>InvalidOperationException</c> rather than retry one. No unit test has a connection, so
/// nothing caught it, and the middleware reported it as a flat "Invalid operation" 400 — which
/// reads like a rejected request rather than code that cannot run at all.
/// </para>
/// <para>
/// The proposal is planted rather than requested. No model is configured here, so a real
/// decomposition fails fast and never reaches <c>Ready</c> — and it is acceptance, not the model
/// call, that these tests are about.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class ProposalAcceptanceTests(BoardSyncApiFactory factory)
{
    /// <summary>
    /// An epic over a feature over two stories, in two delivery phases.
    /// </summary>
    /// <remarks>
    /// Two phases on purpose: a plan that fits in one sprint cannot show that the first sprint
    /// takes only the first phase, which is the behaviour these tests exist for.
    /// </remarks>
    private const string Draft = """
        {
          "roots": [
            {
              "id": "n1",
              "title": "Billing",
              "description": "The billing area.",
              "type": "Epic",
              "priority": "High",
              "children": [
                {
                  "id": "n2",
                  "title": "Invoices",
                  "description": "Invoice handling.",
                  "type": "Feature",
                  "priority": "Medium",
                  "children": [
                    {
                      "id": "n3",
                      "title": "Download an invoice",
                      "description": "A customer downloads a PDF.",
                      "type": "UserStory",
                      "priority": "Medium",
                      "storyPoints": 5,
                      "phase": 1,
                      "children": []
                    },
                    {
                      "id": "n4",
                      "title": "Email an invoice",
                      "description": "A customer receives a PDF by email.",
                      "type": "UserStory",
                      "priority": "Low",
                      "storyPoints": 3,
                      "phase": 2,
                      "children": []
                    }
                  ]
                }
              ]
            }
          ],
          "notes": [],
          "phases": [
            { "name": "Foundations", "rationale": "Nothing reads an invoice until one exists." },
            { "name": "Delivery", "rationale": "Needs the download path to exist first." }
          ]
        }
        """;

    /// <summary>Every node in the draft.</summary>
    private static readonly string[] Everything = ["n1", "n2", "n3", "n4"];

    private static async Task<Guid> PlantAsync(
        BoardSyncApiFactory factory, Workspace workspace, string? draft = null)
    {
        using var scope = factory.Services.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>();

        var proposal = new Proposal
        {
            ProjectId = workspace.ProjectId,
            OrganizationId = workspace.OrganizationId,
            TeamId = workspace.TeamId,
            Status = ProposalStatus.Ready,
            SourceText = new string('x', 200),
            DraftJson = draft ?? Draft,
            TokensSpent = 900,
            CreatedBy = workspace.Owner.UserId,
        };

        context.Proposals.Add(proposal);

        await context.SaveChangesAsync();

        return proposal.Id;
    }

    /// <summary>
    /// The whole tree reaches the board.
    /// </summary>
    /// <remarks>
    /// This is the test that fails outright without the execution strategy — not with a wrong
    /// count, but with a 400 before anything is written.
    /// </remarks>
    [Fact]
    public async Task AcceptingCreatesTheWorkItems()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var proposalId = await PlantAsync(factory, workspace);

        var result = await workspace.Owner.Post<Accepted>(
            $"/api/intelligence/proposals/{proposalId}/accept",
            new { include = Everything });

        Assert.Equal(4, result.Created);

        // Null, not a sprint nobody asked for.
        Assert.Null(result.SprintId);
        Assert.Equal(0, result.Scheduled);
    }

    /// <summary>
    /// Acceptance can plan the work into a sprint, and creates it in Planning.
    /// </summary>
    /// <remarks>
    /// Only the leaf is scheduled. The epic and the feature both carry the story's work, and all
    /// three in one sprint would commit the team to it three times — the burndown, the velocity and
    /// the completion rate would every one of them be wrong by the difference.
    /// </remarks>
    [Fact]
    public async Task AcceptingCanPlanTheWorkIntoANewSprint()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var proposalId = await PlantAsync(factory, workspace);

        var start = DateTime.UtcNow.AddDays(1);

        var result = await workspace.Owner.Post<Accepted>(
            $"/api/intelligence/proposals/{proposalId}/accept",
            new
            {
                include = Everything,
                sprint = new
                {
                    goal = "Ship invoice downloads",
                    startDate = start,
                    endDate = start.AddDays(14),
                },
            });

        Assert.Equal(4, result.Created);

        Assert.NotNull(result.SprintId);
        Assert.Equal(1, result.Scheduled);

        var sprint = await workspace.Owner.Get<SprintShape>(
            $"/api/sprints/{result.SprintId}");

        // Planning, never started: a plan a model drafted should not put itself into a team's
        // current work, which is the same reason acceptance exists at all.
        Assert.Equal("Planning", sprint.Status);
        Assert.Equal("Ship invoice downloads", sprint.Goal);
    }

    /// <summary>
    /// A second acceptance is refused, and refused before it writes anything.
    /// </summary>
    /// <remarks>
    /// Without this a double-submitted form creates the whole tree twice, and the second copy is
    /// indistinguishable from the first on the board — somebody has to work out by hand which of
    /// forty items to delete.
    /// </remarks>
    [Fact]
    public async Task AProposalCannotBeAcceptedTwice()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var proposalId = await PlantAsync(factory, workspace);

        var body = new { include = Everything };

        await workspace.Owner.Post<Accepted>(
            $"/api/intelligence/proposals/{proposalId}/accept", body);

        var second = await workspace.Owner.PostRaw(
            $"/api/intelligence/proposals/{proposalId}/accept", body);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, second.StatusCode);
    }

    /// <summary>
    /// A decomposition can only be requested for the team the project is assigned to.
    /// </summary>
    /// <remarks>
    /// The domain's answer to "which team serves this project" is singular —
    /// <c>TeamServesProjectAsync</c> is <c>AssignedTeamId == teamId</c> — so any other team
    /// produces work nothing can plan. Refused at the request because both ways it failed later
    /// were worse: with a sprint, acceptance rejected a work item it had created seconds earlier
    /// and reported it as not found; without one, the items landed in the project tagged to a team
    /// with no relationship to it and nothing complained.
    /// </remarks>
    [Fact]
    public async Task ADecompositionCannotNameATeamThatDoesNotServeTheProject()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var other = await workspace.Owner.Post<Created>(
            $"/api/orgs/{workspace.OrganizationId}/teams",
            new { name = $"Other {Guid.NewGuid().ToString()[..8]}" });

        var refused = await workspace.Owner.PostRaw(
            $"/api/projects/{workspace.ProjectId}/intelligence/decompose",
            new { content = new string('x', 200), teamId = other.Id });

        Assert.Equal(
            System.Net.HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    /// <summary>The project's own team is accepted, which is what the UI always sends.</summary>
    [Fact]
    public async Task TheProjectsOwnTeamIsAccepted()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var queued = await workspace.Owner.PostRaw(
            $"/api/projects/{workspace.ProjectId}/intelligence/decompose",
            new { content = new string('x', 200), teamId = workspace.TeamId });

        Assert.Equal(System.Net.HttpStatusCode.Accepted, queued.StatusCode);
    }

    /// <summary>
    /// The first sprint takes the first phase, not the whole plan.
    /// </summary>
    /// <remarks>
    /// The correction this behaviour exists for. Jumbling every phase into one sprint destroys the
    /// thing a decomposition is for — showing how the work spreads out over time — and hands the
    /// team a sprint they cannot finish.
    /// </remarks>
    [Fact]
    public async Task TheFirstSprintTakesOnlyTheFirstPhase()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var proposalId = await PlantAsync(factory, workspace);

        var start = DateTime.UtcNow.AddDays(1);

        var result = await workspace.Owner.Post<Accepted>(
            $"/api/intelligence/proposals/{proposalId}/accept",
            new
            {
                include = Everything,
                sprint = new
                {
                    goal = "Foundations",
                    startDate = start,
                    endDate = start.AddDays(14),
                },
            });

        // Four created; two are leaves, and only the phase-1 leaf is scheduled.
        Assert.Equal(4, result.Created);
        Assert.Equal(1, result.Scheduled);

        var items = await workspace.Owner.Get<Paged<SprintItem>>(
            $"/api/sprints/{result.SprintId}/workitems?page=1&pageSize=20");

        Assert.Equal(
            "Download an invoice",
            Assert.Single(items.Items).Title);
    }

    /// <summary>
    /// Everything not in the first sprint is left in the backlog, in the suggested order.
    /// </summary>
    /// <remarks>
    /// The order is the durable half of the model's advice. The dates are a projection over
    /// measured velocity that the reviewer saw before accepting; the sequence is what survives into
    /// the team's day-to-day.
    /// </remarks>
    [Fact]
    public async Task TheRestOfThePlanIsRankedInTheBacklog()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var proposalId = await PlantAsync(factory, workspace);

        await workspace.Owner.Post<Accepted>(
            $"/api/intelligence/proposals/{proposalId}/accept",
            new { include = Everything });

        var backlog = await workspace.Owner.Get<Paged<BacklogRow>>(
            $"/api/projects/{workspace.ProjectId}/backlog?page=1&pageSize=50");

        var stories = backlog.Items
            .Where(row => row.Title.Contains("invoice"))
            .ToList();

        // Phase 1 before phase 2, whatever order they were created in.
        Assert.Equal(
            ["Download an invoice", "Email an invoice"],
            stories.Select(row => row.Title));
    }

    /// <summary>
    /// A draft exactly as it was stored before delivery phases existed — no `phases` key.
    /// </summary>
    /// <remarks>
    /// Copied in shape from what the guard used to persist. Kept literal rather than generated
    /// from today's types, which is the whole point: a type that has since gained a field cannot
    /// reproduce the JSON that predates it.
    /// </remarks>
    private const string PrePhaseDraft = """
        {
          "roots": [
            {
              "id": "n1",
              "title": "Download an invoice",
              "description": "A customer downloads a PDF.",
              "type": "UserStory",
              "priority": "Medium",
              "storyPoints": 5,
              "children": []
            }
          ],
          "notes": []
        }
        """;

    /// <summary>
    /// A proposal drafted before phases existed is still readable.
    /// </summary>
    /// <remarks>
    /// Drafts are stored JSON and outlive the schema that produced them. When phasing shipped,
    /// every proposal already in the database deserialized with a null <c>phases</c>, and the review
    /// screen read <c>phases.length</c> off it — so the breakdown broke outright for every proposal
    /// made before the feature. Normalized to an empty list on read rather than migrated: the
    /// stored JSON is the record of what the model actually returned.
    /// </remarks>
    [Fact]
    public async Task ADraftStoredBeforePhasesExistedStillReads()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var proposalId = await PlantAsync(factory, workspace, PrePhaseDraft);

        var view = await workspace.Owner.Get<ProposalShape>(
            $"/api/intelligence/proposals/{proposalId}");

        Assert.NotNull(view.Draft);

        // A list, not null — every reader written since assumes one.
        Assert.NotNull(view.Draft!.Phases);
        Assert.Empty(view.Draft.Phases!);
    }

    /// <summary>And it can still be accepted, which is what it is for.</summary>
    [Fact]
    public async Task ADraftStoredBeforePhasesExistedCanStillBeAccepted()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var proposalId = await PlantAsync(factory, workspace, PrePhaseDraft);

        var result = await workspace.Owner.Post<Accepted>(
            $"/api/intelligence/proposals/{proposalId}/accept",
            new { include = new[] { "n1" } });

        Assert.Equal(1, result.Created);
    }

    private sealed record ProposalShape(Guid Id, string Status, DraftShape? Draft);

    private sealed record DraftShape(
        List<object> Roots, List<string> Notes, List<object>? Phases);

    private sealed record SprintItem(Guid WorkItemId, string Title);

    private sealed record BacklogRow(Guid WorkItemId, string Title, decimal Rank);

    private sealed record Created(Guid Id);

    private sealed record Paged<T>(List<T> Items, int TotalCount, int Page, int PageSize);

    private sealed record Accepted(
        Guid ProposalId, int Created, List<Guid> WorkItemIds, Guid? SprintId, int Scheduled);

    private sealed record SprintShape(Guid Id, string Status, string? Goal);
}
