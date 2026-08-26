using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using BoardSync.Api.Data;
using BoardSync.Api.Modules.GitSync.Models;
using BoardSync.Api.Modules.GitSync.Repositories;
using BoardSync.Api.Modules.GitSync.Services;
using BoardSync.Api.Modules.Rbac.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That a developer branching, committing, opening a pull request and merging moves the board —
/// and that it stops at the QA gate.
/// </summary>
/// <remarks>
/// This is the product. Everything else in the system exists so that this loop can be trusted: the
/// permission model so automation cannot overreach, the outbox so a move reaches the feed, the job
/// queue so a 300-commit push does not block a webhook, the QA gate so "done" still means a person
/// said so.
/// </remarks>
[Collection(ApiCollection.Name)]
public class GitDrivenBoardTests(BoardSyncApiFactory factory)
{
    private const string Secret = "git-driven-board-secret";

    private sealed record Connected(
        Workspace Workspace, string EndpointToken, string RepositoryId, Guid InstallationId, string Key);

    /// <summary>A project with a repository wired to it, the way linking one really works.</summary>
    private async Task<Connected> ConnectAsync()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var project = await workspace.Owner.Get<ProjectView>($"/api/projects/{workspace.ProjectId}");

        var endpointToken = InstallationSecrets.NewEndpointToken();
        var repositoryId = Random.Shared.Next(100_000, 999_999).ToString();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>();
        var links = scope.ServiceProvider.GetRequiredService<IRepositoryLinkService>();

        var installation = new GitProviderInstallation
        {
            OrganizationId = workspace.OrganizationId,
            Provider = GitProvider.GitHub,
            ExternalId = $"inst-{Guid.NewGuid():N}"[..20],
            AccountName = "acme",
            WebhookSecret = Secret,
            Verification = WebhookVerification.HmacSha256,
            EndpointToken = endpointToken
        };

        context.GitProviderInstallations.Add(installation);
        await context.SaveChangesAsync();

        // Through the service, so the installation gets its Integration grant the same way a real
        // link would — that grant is what the whole QA gate rests on.
        await links.LinkAsync(
            installation.Id, workspace.ProjectId, repositoryId, "acme/payments", "main",
            workspace.Owner.UserId);

        return new Connected(workspace, endpointToken, repositoryId, installation.Id, project.Key);
    }

    private async Task DeliverAsync(string endpointToken, string payload, string eventName)
    {
        var http = factory.CreateClient();
        var body = Encoding.UTF8.GetBytes(payload);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/git/github/webhook/{endpointToken}") { Content = new ByteArrayContent(body) };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Event", eventName);
        request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation(
            "X-Hub-Signature-256",
            "sha256=" + Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body)));

        (await http.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private Task PushAsync(Connected c, string branch, string message) =>
        DeliverAsync(c.EndpointToken, $$"""
        {
          "ref": "refs/heads/{{branch}}",
          "created": false,
          "repository": { "id": {{c.RepositoryId}}, "full_name": "acme/payments" },
          "pusher": { "name": "ada", "email": "ada@acme.test" },
          "commits": [
            { "id": "{{Guid.NewGuid():N}}", "message": "{{message}}",
              "author": { "name": "Ada", "email": "ada@acme.test" },
              "timestamp": "{{DateTimeOffset.UtcNow:O}}" }
          ]
        }
        """, "push");

    private Task PullRequestAsync(Connected c, string branch, string action, bool merged, string baseBranch = "main") =>
        DeliverAsync(c.EndpointToken, $$"""
        {
          "action": "{{action}}",
          "repository": { "id": {{c.RepositoryId}}, "full_name": "acme/payments" },
          "sender": { "login": "ada" },
          "pull_request": {
            "number": 42, "title": "work", "body": null,
            "html_url": "https://github.com/acme/payments/pull/42",
            "merged": {{(merged ? "true" : "false")}},
            "head": { "ref": "{{branch}}" }, "base": { "ref": "{{baseBranch}}" }
          }
        }
        """, "pull_request");

    /// <summary>Waits for the worker to move an item, then reports where it is.</summary>
    private async Task<string> StateAfterAsync(Workspace workspace, Guid workItemId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        string state;

        do
        {
            state = (await workspace.Owner.Get<WorkItemView>($"/api/workitems/{workItemId}")).State;
            if (state == expected) return state;
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        return state;
    }

    // ── The loop ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Branch, commit, open, merge — and the card walks New → Active → InReview → Resolved with
    /// nobody touching the board.
    /// </summary>
    [Fact]
    public async Task AWholeDevelopmentCycleMovesTheCardOnItsOwn()
    {
        var c = await ConnectAsync();
        var itemId = await c.Workspace.AddWorkItemAsync("git drives this");
        var item = await c.Workspace.Owner.Get<WorkItemView>($"/api/workitems/{itemId}");

        var branch = $"{item.Reference.ToLowerInvariant()}-fix-login";

        Assert.Equal("New", item.State);

        await PushAsync(c, branch, "start of the work");
        Assert.Equal("Active", await StateAfterAsync(c.Workspace, itemId, "Active"));

        await PullRequestAsync(c, branch, "opened", merged: false);
        Assert.Equal("InReview", await StateAfterAsync(c.Workspace, itemId, "InReview"));

        await PullRequestAsync(c, branch, "closed", merged: true);
        Assert.Equal("Resolved", await StateAfterAsync(c.Workspace, itemId, "Resolved"));
    }

    /// <summary>
    /// And it stops there. A merge never closes a work item.
    /// </summary>
    /// <remarks>
    /// The promise the product makes. It holds structurally rather than by a check in the transition
    /// code: the integration principal does not hold <c>workitem:verify</c>, so the permission check
    /// inside <c>WorkItemService</c> would refuse <c>Closed</c> even if the rules here asked for it.
    /// </remarks>
    [Fact]
    public async Task AMergeNeverClosesAWorkItem()
    {
        var c = await ConnectAsync();
        var itemId = await c.Workspace.AddWorkItemAsync("stops at the gate");
        var item = await c.Workspace.Owner.Get<WorkItemView>($"/api/workitems/{itemId}");
        var branch = item.Reference.ToLowerInvariant();

        await PushAsync(c, branch, "work");
        await PullRequestAsync(c, branch, "opened", merged: false);
        await PullRequestAsync(c, branch, "closed", merged: true);

        Assert.Equal("Resolved", await StateAfterAsync(c.Workspace, itemId, "Resolved"));

        // Give the worker room to do something wrong before asserting it did not.
        await Task.Delay(1000);
        Assert.Equal("Resolved",
            (await c.Workspace.Owner.Get<WorkItemView>($"/api/workitems/{itemId}")).State);
    }

    /// <summary>The integration holds contribution and not certification.</summary>
    /// <remarks>
    /// Asserted against the evaluator directly, because it is the mechanism rather than a
    /// consequence: if this grant ever widened, every test above would still pass and the promise
    /// would be gone.
    /// </remarks>
    [Fact]
    public async Task TheIntegrationPrincipalCannotCertify()
    {
        var c = await ConnectAsync();

        using var scope = factory.Services.CreateScope();
        var rbac = scope.ServiceProvider
            .GetRequiredService<Modules.Rbac.Services.Interfaces.IRbacService>();

        var held = await rbac.GetPermissionsAtAsync(
            c.InstallationId, new ScopeRef(RoleScope.Project, c.Workspace.ProjectId));

        Assert.Contains(Permissions.WorkItemWrite, held);
        Assert.Contains(Permissions.WorkItemRead, held);

        Assert.DoesNotContain(Permissions.WorkItemVerify, held);
        Assert.DoesNotContain(Permissions.WorkItemDelete, held);
        Assert.DoesNotContain(Permissions.ProjectAdmin, held);
        Assert.DoesNotContain(Permissions.SprintScope, held);
    }

    // ── The invariants ────────────────────────────────────────────────────────

    /// <summary>
    /// A late-arriving push does not drag a merged item backwards.
    /// </summary>
    /// <remarks>
    /// Webhooks arrive out of order routinely — a retried delivery landing after the pull request it
    /// preceded. Without the monotonic rule, that would undo a merge.
    /// </remarks>
    [Fact]
    public async Task ALatePushDoesNotMoveAnItemBackwards()
    {
        var c = await ConnectAsync();
        var itemId = await c.Workspace.AddWorkItemAsync("no going back");
        var item = await c.Workspace.Owner.Get<WorkItemView>($"/api/workitems/{itemId}");
        var branch = item.Reference.ToLowerInvariant();

        await PushAsync(c, branch, "work");
        await PullRequestAsync(c, branch, "opened", merged: false);
        await PullRequestAsync(c, branch, "closed", merged: true);
        Assert.Equal("Resolved", await StateAfterAsync(c.Workspace, itemId, "Resolved"));

        // The straggler.
        await PushAsync(c, branch, "an earlier commit, delivered late");
        await Task.Delay(1500);

        Assert.Equal("Resolved",
            (await c.Workspace.Owner.Get<WorkItemView>($"/api/workitems/{itemId}")).State);
    }

    /// <summary>
    /// A person who moved the card wins over a git event that arrives afterwards.
    /// </summary>
    /// <remarks>
    /// The board is derived from git, but somebody who deliberately overrode it knew something git
    /// did not — a branch abandoned, work descoped. Automation quietly undoing that is what makes
    /// people stop trusting a tool like this.
    /// </remarks>
    [Fact]
    public async Task AHumanOverrideWinsOverALaterGitEvent()
    {
        var c = await ConnectAsync();
        var itemId = await c.Workspace.AddWorkItemAsync("human decides");
        var item = await c.Workspace.Owner.Get<WorkItemView>($"/api/workitems/{itemId}");
        var branch = item.Reference.ToLowerInvariant();

        await PushAsync(c, branch, "start");
        Assert.Equal("Active", await StateAfterAsync(c.Workspace, itemId, "Active"));

        // A person decides this is ready for test, without a pull request.
        await c.Workspace.Owner.Patch<object>(
            $"/api/workitems/{itemId}/state", new { state = "Resolved" });

        // A pull request opens afterwards. InReview is behind Resolved, so it must not apply.
        await PullRequestAsync(c, branch, "opened", merged: false);
        await Task.Delay(1500);

        Assert.Equal("Resolved",
            (await c.Workspace.Owner.Get<WorkItemView>($"/api/workitems/{itemId}")).State);
    }

    // ── Boundaries ────────────────────────────────────────────────────────────

    /// <summary>
    /// A merge into a branch that is not the default does not resolve anything.
    /// </summary>
    /// <remarks>
    /// Merging a feature branch into another feature branch is ordinary work, not completion.
    /// </remarks>
    [Fact]
    public async Task AMergeIntoANonDefaultBranchDoesNotResolve()
    {
        var c = await ConnectAsync();
        var itemId = await c.Workspace.AddWorkItemAsync("not done yet");
        var item = await c.Workspace.Owner.Get<WorkItemView>($"/api/workitems/{itemId}");
        var branch = item.Reference.ToLowerInvariant();

        await PushAsync(c, branch, "work");
        Assert.Equal("Active", await StateAfterAsync(c.Workspace, itemId, "Active"));

        await PullRequestAsync(c, branch, "closed", merged: true, baseBranch: "develop");
        await Task.Delay(1500);

        Assert.Equal("Active",
            (await c.Workspace.Owner.Get<WorkItemView>($"/api/workitems/{itemId}")).State);
    }

    /// <summary>
    /// A repository cannot move another project's work, whatever the commit says.
    /// </summary>
    /// <remarks>
    /// The security boundary for binding. Without it, anyone able to push to any connected
    /// repository could move any work item in the system by naming it.
    /// </remarks>
    [Fact]
    public async Task ARepositoryCannotMoveAnotherProjectsWork()
    {
        var mine = await ConnectAsync();
        var theirs = await ConnectAsync();

        var theirItemId = await theirs.Workspace.AddWorkItemAsync("not yours to move");
        var theirItem = await theirs.Workspace.Owner.Get<WorkItemView>($"/api/workitems/{theirItemId}");

        // My repository pushes a branch naming their work item.
        await PushAsync(mine, theirItem.Reference.ToLowerInvariant(), "trying to reach across");
        await Task.Delay(1500);

        Assert.Equal("New",
            (await theirs.Workspace.Owner.Get<WorkItemView>($"/api/workitems/{theirItemId}")).State);
    }

    /// <summary>A branch naming nothing leaves the board alone, and says so.</summary>
    [Fact]
    public async Task AnUnreferencedBranchIsRecordedAsBindingNothing()
    {
        var c = await ConnectAsync();

        await PushAsync(c, "just-a-branch", "no reference here");

        var outcome = await PollOutcomeAsync(c.InstallationId);

        Assert.NotNull(outcome);
        Assert.Contains("no work item referenced", outcome);
    }

    /// <summary>The delivery records what moved, so a quiet integration is distinguishable from a broken one.</summary>
    [Fact]
    public async Task TheDeliveryRecordsWhatItMoved()
    {
        var c = await ConnectAsync();
        var itemId = await c.Workspace.AddWorkItemAsync("recorded");
        var item = await c.Workspace.Owner.Get<WorkItemView>($"/api/workitems/{itemId}");

        await PushAsync(c, item.Reference.ToLowerInvariant(), "work");
        await StateAfterAsync(c.Workspace, itemId, "Active");

        var outcome = await PollOutcomeAsync(c.InstallationId);

        Assert.NotNull(outcome);
        Assert.Contains(item.Reference, outcome);
        Assert.Contains("New→Active", outcome);
    }

    private async Task<string?> PollOutcomeAsync(Guid installationId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();

            var outcome = await scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>()
                .WebhookDeliveries
                .Where(d => d.InstallationId == installationId && d.Outcome != null)
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => d.Outcome)
                .FirstOrDefaultAsync();

            if (outcome is not null) return outcome;
            await Task.Delay(100);
        }

        return null;
    }

    /// <summary>
    /// History says an integration made the change, not a person.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The product's claim is that the board updates itself, and a work item's history is the one
    /// place that is most legible — but only if a client can tell the two apart. Without
    /// <c>actorType</c> on the response, a git transition renders as though whoever the installation
    /// is keyed to had dragged the card, which is both wrong and the opposite of the point.
    /// </para>
    /// <para>
    /// <c>attributedToUserId</c> is the commit author matched by email. It is attribution, not
    /// authorship: the integration made the change, and a client must not render it as though the
    /// person did.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GitDrivenHistoryIsDistinguishableFromAPerson()
    {
        var c = await ConnectAsync();
        var itemId = await c.Workspace.AddWorkItemAsync("history provenance");

        var reference = (await c.Workspace.Owner.Get<WorkItemView>(
            $"/api/workitems/{itemId}")).Reference;

        // A person moves it first, so both kinds of row exist on one item.
        await c.Workspace.Owner.Patch<object>(
            $"/api/workitems/{itemId}/state", new { state = "Active" });

        await PushAsync(c, $"feature/{reference}-provenance", "work");
        await PullRequestAsync(c, $"feature/{reference}-provenance", "opened", merged: false);

        await StateAfterAsync(c.Workspace, itemId, "InReview");

        var history = await c.Workspace.Owner.Get<Paged<HistoryView>>(
            $"/api/workitems/{itemId}/history");

        var states = history.Items
            .Where(h => h.FieldName == "State")
            .ToList();

        var byPerson = states.Where(h => h.ActorType == "User").ToList();
        var byIntegration = states
            .Where(h => h.ActorType == "Integration")
            .ToList();

        // Both kinds are present on one item, which is the case a client has to render.
        Assert.NotEmpty(byPerson);
        Assert.NotEmpty(byIntegration);

        Assert.Contains(byPerson, h => h.NewValue == "Active");
        Assert.All(byPerson, h =>
            Assert.Equal(c.Workspace.Owner.UserId, h.ChangedBy));

        Assert.Contains(byIntegration, h => h.NewValue == "InReview");

        // The installation made the change, so it is what `changedBy` names — never the person.
        Assert.All(byIntegration, h =>
            Assert.NotEqual(c.Workspace.Owner.UserId, h.ChangedBy));
    }

    /// <summary>
    /// The board's cards carry the reference a branch name has to contain.
    /// </summary>
    /// <remarks>
    /// Work binds to git by its reference appearing in a branch name, a commit message or a pull
    /// request title. A board that shows a card without ever showing <c>BS-142</c> makes that
    /// something a developer has to go and look up elsewhere before they can start — which is the
    /// integration being technically present and practically unusable.
    /// </remarks>
    [Fact]
    public async Task BoardCardsCarryTheReferenceBranchNamesNeed()
    {
        var c = await ConnectAsync();
        var itemId = await c.Workspace.AddWorkItemAsync("needs a reference");

        var sprint = await c.Workspace.Owner.Post<Created>(
            $"/api/projects/{c.Workspace.ProjectId}/sprints",
            new
            {
                goal = "reference on cards",
                startDate = DateTime.UtcNow.Date,
                endDate = DateTime.UtcNow.Date.AddDays(7)
            });

        await c.Workspace.Owner.Post(
            $"/api/sprints/{sprint.Id}/workitems", new { workItemId = itemId });

        await c.Workspace.Owner.Patch<object>(
            $"/api/sprints/{sprint.Id}/status", new { status = "Active" });

        var expected = (await c.Workspace.Owner.Get<WorkItemView>(
            $"/api/workitems/{itemId}")).Reference;

        var board = await c.Workspace.Owner.Get<BoardView>(
            $"/api/projects/{c.Workspace.ProjectId}/board");

        var card = Assert.Single(
            board.Columns.SelectMany(col => col.Cards),
            x => x.WorkItemId == itemId);

        Assert.Equal(expected, card.Reference);
        Assert.Matches(@"^[A-Z][A-Z0-9]*-\d+$", card.Reference);

        // And on the sprint listing, which is the other place work is picked up from.
        var items = await c.Workspace.Owner.Get<Paged<SprintItemView>>(
            $"/api/sprints/{sprint.Id}/workitems");

        Assert.Equal(expected,
            Assert.Single(items.Items, i => i.WorkItemId == itemId).Reference);

        // And on the backlog — the third list work is picked up from, and the one where somebody is
        // most likely to be deciding what to start next.
        var backlogItem = await c.Workspace.AddWorkItemAsync("waiting in the backlog");

        await c.Workspace.Owner.Post(
            $"/api/projects/{c.Workspace.ProjectId}/backlog",
            new { workItemId = backlogItem });

        var backlog = await c.Workspace.Owner.Get<Paged<BacklogItemView>>(
            $"/api/projects/{c.Workspace.ProjectId}/backlog");

        var queued = Assert.Single(
            backlog.Items, b => b.WorkItemId == backlogItem);

        Assert.Matches(@"^[A-Z][A-Z0-9]*-\d+$", queued.Reference);
    }

    private sealed record Created(Guid Id);
    private sealed record BoardView(List<BoardColumnView> Columns);
    private sealed record BoardColumnView(Guid Id, string Name, List<BoardCardView> Cards);
    private sealed record BoardCardView(Guid WorkItemId, string Reference, string Title);
    private sealed record SprintItemView(Guid WorkItemId, string Reference, string Title);
    private sealed record BacklogItemView(Guid WorkItemId, string Reference, string Title);

    private sealed record HistoryView(
        Guid Id, Guid WorkItemId, Guid ChangedBy, string ActorType, Guid? AttributedToUserId,
        string FieldName, string? OldValue, string? NewValue, DateTime CreatedAt);

    private sealed record Paged<T>(List<T> Items, int TotalCount);

    private sealed record ProjectView(Guid Id, string Key, string Name);
    private sealed record WorkItemView(Guid Id, int Number, string Reference, string State, string Title);
}
