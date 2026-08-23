using System.Net;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That the QA gate is enforced by the API, not merely described by the rules.
/// </summary>
/// <remarks>
/// <para>
/// The permission a transition needs depends on the states being moved between, and the target
/// arrives in the request body — so it cannot live in a <c>[RequirePermission]</c> attribute and is
/// therefore invisible to the endpoint-authorization coverage test. That makes end-to-end coverage
/// the only thing standing behind it.
/// </para>
/// <para>
/// <c>QaGateTests</c> covers the rules; this covers whether they are applied.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class QaGateEndpointTests(BoardSyncApiFactory factory)
{
    private static async Task MoveTo(TestApi api, Guid workItemId, string state) =>
        await api.Patch<object>($"/api/workitems/{workItemId}/state", new { state });

    private static Task<HttpResponseMessage> TryMoveTo(TestApi api, Guid workItemId, string state) =>
        api.PatchRaw($"/api/workitems/{workItemId}/state", new { state });

    /// <summary>
    /// A contributor can carry work all the way to the QA lane and no further.
    /// </summary>
    /// <remarks>
    /// The path git will drive, walked by hand. The final step is the one that must fail: the
    /// project's owner here is an OrgAdmin, so this is deliberately run as a plain contributor who
    /// holds <c>workitem:write</c> and nothing more.
    /// </remarks>
    [Fact]
    public async Task AContributorCanReachAwaitingQaAndNotPastIt()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var contributor = await workspace.AddOrganizationMemberAsync(factory);

        await workspace.Owner.Post($"/api/projects/{workspace.ProjectId}/roles",
            new { userId = contributor.UserId, role = "Contributor" });

        var workItemId = await workspace.AddWorkItemAsync("contributor path");

        await MoveTo(contributor, workItemId, "Active");
        await MoveTo(contributor, workItemId, "InReview");
        await MoveTo(contributor, workItemId, "Resolved");

        var closing = await TryMoveTo(contributor, workItemId, "Closed");

        // 403 with the system's generic denial message. A refusal deliberately does not describe
        // what the caller is missing; a client that wants to know before trying reads the
        // transition's requiresPermission from /api/metadata against /api/me/capabilities.
        Assert.Equal(HttpStatusCode.Forbidden, closing.StatusCode);
    }

    /// <summary>
    /// A contributor cannot pull work back out of the QA lane either.
    /// </summary>
    /// <remarks>
    /// The bypass that guarding only the Closed edge would have left open: move it back to Active,
    /// and QA never sees it, with no rejection recorded.
    /// </remarks>
    [Fact]
    public async Task AContributorCannotTakeWorkBackOutOfTheQaLane()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var contributor = await workspace.AddOrganizationMemberAsync(factory);

        await workspace.Owner.Post($"/api/projects/{workspace.ProjectId}/roles",
            new { userId = contributor.UserId, role = "Contributor" });

        var workItemId = await workspace.AddWorkItemAsync("no take-backs");

        await MoveTo(contributor, workItemId, "Active");
        await MoveTo(contributor, workItemId, "Resolved");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await TryMoveTo(contributor, workItemId, "Active")).StatusCode);
    }

    /// <summary>A tester certifies, and can also send work back.</summary>
    [Fact]
    public async Task ATesterCanCertifyAndReject()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var developer = await workspace.AddOrganizationMemberAsync(factory);
        var tester = await workspace.AddOrganizationMemberAsync(factory);

        await workspace.Owner.Post($"/api/projects/{workspace.ProjectId}/roles",
            new { userId = developer.UserId, role = "Contributor" });
        await workspace.Owner.Post($"/api/projects/{workspace.ProjectId}/roles",
            new { userId = tester.UserId, role = "Tester" });

        // Rejected once, then accepted — the round trip a real item makes.
        var rejected = await workspace.AddWorkItemAsync("rejected then fixed");
        await MoveTo(developer, rejected, "Active");
        await MoveTo(developer, rejected, "Resolved");
        await MoveTo(tester, rejected, "Active");
        await MoveTo(developer, rejected, "Resolved");

        // Reaches Closed without throwing, which is the assertion — MoveTo fails the test with the
        // server's own message on any non-success status.
        await MoveTo(tester, rejected, "Closed");
    }

    /// <summary>
    /// Nobody may certify work assigned to them, however much authority they hold.
    /// </summary>
    /// <remarks>
    /// A tester who is also the assignee is the case the setting exists for. The refusal is not about
    /// their permission — they hold <c>workitem:verify</c> — but about who the work belongs to.
    /// </remarks>
    [Fact]
    public async Task ATesterCannotCertifyTheirOwnWork()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var tester = await workspace.AddOrganizationMemberAsync(factory);

        await workspace.Owner.Post($"/api/teams/{workspace.TeamId}/members",
            new { userId = tester.UserId });
        await workspace.Owner.Post($"/api/projects/{workspace.ProjectId}/roles",
            new { userId = tester.UserId, role = "Tester" });

        var workItemId = await workspace.Owner.Post<Created>(
            $"/api/projects/{workspace.ProjectId}/workitems",
            new
            {
                title = "assigned to the tester",
                type = "Task",
                teamId = workspace.TeamId,
                assigneeId = tester.UserId
            });

        await MoveTo(tester, workItemId.Id, "Active");
        await MoveTo(tester, workItemId.Id, "Resolved");

        var closing = await TryMoveTo(tester, workItemId.Id, "Closed");

        // 422, not 403: they hold workitem:verify. Answering "forbidden" would send them looking
        // for a grant that would not help, so the rule explains itself instead.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, closing.StatusCode);
        Assert.Contains("assigned to you", await TestApi.MessageOf(closing));
    }

    /// <summary>
    /// A project may allow self-certification, for teams small enough that it is the honest setting.
    /// </summary>
    /// <remarks>
    /// Exercised as a Tester rather than as the project's owner: an administrator is exempt from the
    /// rule anyway — they can flip this setting themselves, so blocking them only adds a round trip —
    /// which would have made the first assertion pass for the wrong reason.
    /// </remarks>
    [Fact]
    public async Task SelfCertificationCanBeEnabledPerProject()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var tester = await workspace.AddOrganizationMemberAsync(factory);

        await workspace.Owner.Post($"/api/teams/{workspace.TeamId}/members",
            new { userId = tester.UserId });
        await workspace.Owner.Post($"/api/projects/{workspace.ProjectId}/roles",
            new { userId = tester.UserId, role = "Tester" });

        var item = await workspace.Owner.Post<Created>(
            $"/api/projects/{workspace.ProjectId}/workitems",
            new
            {
                title = "tester's own work",
                type = "Task",
                teamId = workspace.TeamId,
                assigneeId = tester.UserId
            });

        await MoveTo(tester, item.Id, "Active");
        await MoveTo(tester, item.Id, "Resolved");

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await TryMoveTo(tester, item.Id, "Closed")).StatusCode);

        await workspace.Owner.Put($"/api/projects/{workspace.ProjectId}",
            new { name = "Self-certifying project", allowSelfCertification = true });

        await MoveTo(tester, item.Id, "Closed");
    }

    /// <summary>
    /// The state machine still refuses a move that skips the QA lane, whoever asks.
    /// </summary>
    /// <remarks>
    /// 422 rather than 403: this is not a permission answer. Even an OrgAdmin cannot go from Active
    /// straight to Closed, because that edge does not exist — which is what keeps the permission on
    /// the Resolved edge from being the only thing holding the gate shut.
    /// </remarks>
    [Fact]
    public async Task NobodyCanSkipTheQaLane()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync("no shortcuts");

        await MoveTo(workspace.Owner, workItemId, "Active");

        var response = await TryMoveTo(workspace.Owner, workItemId, "Closed");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("not allowed", await TestApi.MessageOf(response));
    }

    /// <summary>
    /// A new project's board has a lane for every state, so no card can be in a state nothing shows.
    /// </summary>
    [Fact]
    public async Task ANewBoardHasALaneForEveryState()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var board = await workspace.Owner.Get<Board>($"/api/projects/{workspace.ProjectId}/board");
        var mapped = board.Columns.Select(c => c.MappedState).ToHashSet();

        Assert.Equal(
            ["New", "Active", "InReview", "Resolved", "Closed"],
            board.Columns.OrderBy(c => c.Position).Select(c => c.MappedState).ToArray());

        Assert.Contains("InReview", mapped);
    }

    private sealed record Created(Guid Id);
    private sealed record Board(Guid Id, List<Column> Columns);
    private sealed record Column(Guid Id, string Name, string MappedState, int Position);
}
