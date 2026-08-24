using System.Net;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That the cross-cutting reads show only what the caller may actually open.
/// </summary>
/// <remarks>
/// <para>
/// Search, the notification bell and the workspace summary span every scope, so none of them can
/// carry a <c>[RequirePermission]</c> attribute and none is covered by the endpoint-authorization
/// coverage test. Each therefore had to scope itself, and each independently arrived at the same
/// wrong rule: treat organization membership as access to everything inside the organization.
/// </para>
/// <para>
/// These are the regression tests for that, written end to end because that is the only altitude at
/// which the bug was visible — every layer in isolation looked reasonable.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class AuthorizationScopingTests(BoardSyncApiFactory factory)
{
    /// <summary>
    /// The control: an organization member who is on no team and holds no project role cannot open
    /// the project or its work items.
    /// </summary>
    /// <remarks>
    /// 404 rather than 403, because they cannot see the project at all — the response must be
    /// indistinguishable from an id that names nothing. Everything below asserts that the
    /// scope-spanning reads agree with this answer.
    /// </remarks>
    [Fact]
    public async Task OrganizationMemberCannotOpenTheProjectOrItsWorkItems()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync("control item");
        var member = await workspace.AddOrganizationMemberAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound,
            (await member.GetRaw($"/api/projects/{workspace.ProjectId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await member.GetRaw($"/api/workitems/{workItemId}")).StatusCode);
    }

    /// <summary>
    /// Search does not return work items from projects the caller cannot open.
    /// </summary>
    /// <remarks>
    /// The defect: it did. Search resolved its scope from <c>OrganizationMemberships</c> and read
    /// every active project in those organizations, so an organization member could read the title
    /// of every work item in the organization — while the project itself answered 404 to the same
    /// person on the same request.
    /// </remarks>
    [Fact]
    public async Task SearchDoesNotLeakWorkItemsAcrossThePermissionBoundary()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var term = $"needle{Guid.NewGuid():N}"[..20];
        await workspace.AddWorkItemAsync($"{term} migrate billing");

        var member = await workspace.AddOrganizationMemberAsync(factory);

        var theirs = await workspace.Owner.Get<SearchResults>($"/api/search?q={term}");
        var stranger = await member.Get<SearchResults>($"/api/search?q={term}");

        Assert.Single(theirs.WorkItems);
        Assert.Empty(stranger.WorkItems);
        Assert.Empty(stranger.Projects);
    }

    /// <summary>
    /// The notification bell tells people only what was addressed to them.
    /// </summary>
    /// <remarks>
    /// The leak this was written for is now structurally impossible rather than filtered away. The
    /// bell used to read everyone's work item history and narrow it by permission; a notification
    /// is now written to one recipient when it is raised, so somebody nothing was addressed to has an
    /// empty bell by construction. Kept because that is the property that matters, however it is
    /// achieved.
    /// </remarks>
    [Fact]
    public async Task NotificationsAreAddressedRatherThanFiltered()
    {
        var workspace = await Workspace.CreateAsync(factory);
        await workspace.AddWorkItemAsync("notify me");

        var member = await workspace.AddOrganizationMemberAsync(factory);

        var theirs = await member.Get<Feed>("/api/notifications");

        Assert.Empty(theirs.Items);
        Assert.Equal(0, theirs.UnreadCount);
    }

    /// <summary>
    /// The workspace summary counts only what the caller may open.
    /// </summary>
    /// <remarks>
    /// The organization count stays 1 for the member: they really are in one organization and really
    /// do hold <c>org:read</c> on it. It is the project and work item counters that had been
    /// reporting a workspace the user could not actually reach.
    /// </remarks>
    [Fact]
    public async Task WorkspaceSummaryCountsOnlyWhatTheCallerCanOpen()
    {
        var workspace = await Workspace.CreateAsync(factory);
        await workspace.AddWorkItemAsync("counted");

        var member = await workspace.AddOrganizationMemberAsync(factory);
        var summary = await member.Get<Summary>("/api/workspace/summary");

        Assert.Equal(1, summary.Organizations);
        Assert.Equal(0, summary.Projects);
        Assert.Equal(0, summary.ActiveWorkItems);
    }

    /// <summary>Someone with no connection at all sees nothing anywhere.</summary>
    [Fact]
    public async Task AnUnrelatedUserSeesNothing()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var term = $"private{Guid.NewGuid():N}"[..20];
        await workspace.AddWorkItemAsync($"{term} secret");

        var outsider = await TestApi.RegisterAsync(factory);

        Assert.Empty((await outsider.Get<SearchResults>($"/api/search?q={term}")).WorkItems);
        Assert.Empty((await outsider.Get<Feed>("/api/notifications")).Items);

        var summary = await outsider.Get<Summary>("/api/workspace/summary");
        Assert.Equal(0, summary.Organizations);
        Assert.Equal(0, summary.Projects);
    }

    private sealed record SearchResults(
        List<Hit> Organizations, List<Hit> Projects, List<Hit> Members, List<Hit> WorkItems);

    private sealed record Hit(Guid Id, string Title);
    private sealed record Notification(Guid Id, string Type, string Title);
    private sealed record Feed(List<Notification> Items, int UnreadCount);
    private sealed record Summary(int Organizations, int Projects, int Members, int ActiveWorkItems);
}
