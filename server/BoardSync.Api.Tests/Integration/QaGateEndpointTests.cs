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
    /// A team member cannot certify, reaching the project through the team edge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from the contributor case above, and not covered by it. A project-scope
    /// <c>Contributor</c> is a direct grant the evaluator finds without leaving the project; a
    /// team-scope <c>TeamMember</c> reaches the project only through
    /// <c>GetProjectLocationAsync</c> and the team → project inheritance table. Different code,
    /// different chance of being wrong, and it is the shape most real users have — people are added
    /// to teams, not granted project roles one at a time.
    /// </para>
    /// <para>
    /// <c>QaGateTests</c> asserts the same thing against a hand-built snapshot. This asserts that a
    /// real membership resolves to that snapshot.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATeamMemberCannotCertifyThroughTheTeamEdge()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var member = await workspace.AddOrganizationMemberAsync(factory);

        // Team membership only. No project role at all — this is the whole point.
        await workspace.Owner.Post($"/api/teams/{workspace.TeamId}/members",
            new { userId = member.UserId });

        var workItemId = await workspace.AddWorkItemAsync("team member path");

        // Contribution reaches the QA lane, which confirms the team edge is granting write.
        await MoveTo(member, workItemId, "Active");
        await MoveTo(member, workItemId, "Resolved");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await TryMoveTo(member, workItemId, "Closed")).StatusCode);

        // And cannot pull it back out of the lane either.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await TryMoveTo(member, workItemId, "Active")).StatusCode);
    }

    /// <summary>
    /// An OrgAdmin can certify, including work on a team they merely belong to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted because it is the behaviour most likely to be reported as a hole in the gate. An
    /// organization administrator holds every permission at every scope beneath them, so joining a
    /// team as an ordinary member does not reduce what they may do — the team grant adds to their
    /// authority, it does not replace it. Somebody testing the gate from the account that created
    /// the organization will find they can close anything, and nothing is wrong.
    /// </para>
    /// <para>
    /// If that is not wanted, the change is to <c>RolePermissions.Everything</c>, and it is a
    /// product decision rather than a bug fix: an OrgAdmin can already grant themselves
    /// <c>Tester</c> on any project in one request, so withholding certification would inconvenience
    /// them without actually separating the authority.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnOrgAdminCertifiesEvenAsAnOrdinaryTeamMember()
    {
        var workspace = await Workspace.CreateAsync(factory);

        // The owner is the organization's OrgAdmin and a plain member of the team.
        var workItemId = await workspace.AddWorkItemAsync("org admin path");

        await MoveTo(workspace.Owner, workItemId, "Active");
        await MoveTo(workspace.Owner, workItemId, "Resolved");

        // Self-certification is the separate rule, and it is what stops this one — the item is
        // assigned to them. Reassigning removes that, leaving only the permission question.
        var other = await workspace.AddOrganizationMemberAsync(factory);
        await workspace.Owner.Post($"/api/teams/{workspace.TeamId}/members",
            new { userId = other.UserId });

        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}",
            new { assigneeId = other.UserId });

        var closing = await TryMoveTo(workspace.Owner, workItemId, "Closed");

        Assert.Equal(HttpStatusCode.OK, closing.StatusCode);
    }

    /// <summary>
    /// A team Tester certifies on every project the team serves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The grant most teams actually want, and until this endpoint existed there was no way to make
    /// it: team membership carries no role, and positions cover only Team Lead, Scrum Master and
    /// Product Owner. So the person doing the testing could not be given the role that exists for
    /// testing, and the gate could only be passed by people holding certification incidentally.
    /// </para>
    /// <para>
    /// It reaches the project through the team → project edge rather than through any project-scope
    /// row, which is exactly what makes it worth having: one grant, every project the team serves.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATeamTesterCertifiesWithoutAnyProjectRole()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var tester = await workspace.AddOrganizationMemberAsync(factory);

        await workspace.Owner.Post($"/api/teams/{workspace.TeamId}/members",
            new { userId = tester.UserId });

        // Team scope. No project role at all — that is the point.
        await workspace.Owner.Post($"/api/teams/{workspace.TeamId}/roles",
            new { userId = tester.UserId, role = "Tester" });

        var workItemId = await workspace.AddWorkItemAsync("team tester certifies");

        await MoveTo(workspace.Owner, workItemId, "Active");
        await MoveTo(workspace.Owner, workItemId, "Resolved");

        // Assigned to the owner, so self-certification is not in play for the tester.
        var closing = await TryMoveTo(tester, workItemId, "Closed");

        Assert.Equal(HttpStatusCode.OK, closing.StatusCode);
    }

    /// <summary>
    /// The positions are not grantable through the ordinary role endpoint.
    /// </summary>
    /// <remarks>
    /// They transfer in one call so the seat is never half empty. Granting one through here would
    /// let two people hold it at once, which is the whole thing a position is defined against.
    /// </remarks>
    [Fact]
    public async Task PositionsCannotBeGrantedAsOrdinaryTeamRoles()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var member = await workspace.AddOrganizationMemberAsync(factory);

        await workspace.Owner.Post($"/api/teams/{workspace.TeamId}/members",
            new { userId = member.UserId });

        Assert.Equal(HttpStatusCode.BadRequest,
            (await workspace.Owner.PostRaw($"/api/teams/{workspace.TeamId}/roles",
                new { userId = member.UserId, role = "ScrumMaster" })).StatusCode);
    }

    /// <summary>A role is what membership means, not a way to join.</summary>
    [Fact]
    public async Task ATeamRoleCannotBeGrantedToSomebodyOutsideTheTeam()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var outsider = await workspace.AddOrganizationMemberAsync(factory);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await workspace.Owner.PostRaw($"/api/teams/{workspace.TeamId}/roles",
                new { userId = outsider.UserId, role = "Tester" })).StatusCode);
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
