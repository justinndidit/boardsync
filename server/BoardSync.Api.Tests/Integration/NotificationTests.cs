using System.Net;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That the right people are told the right things, and nobody is told about their own actions.
/// </summary>
/// <remarks>
/// <para>
/// The bell was previously a query over work item history filtered to the caller's organizations —
/// no recipient, no read state, no targeting. It showed everyone the same rows and called them
/// notifications, and because it filtered on a column nothing wrote, it showed nobody anything.
/// </para>
/// <para>
/// Notifications are raised on the outbox path, so every assertion here polls: they are eventually
/// consistent by design, usually within milliseconds.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class NotificationTests(BoardSyncApiFactory factory)
{
    private static async Task<Feed> WaitForAsync(
        TestApi api, Func<Feed, bool> until, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(15));
        Feed feed;

        do
        {
            feed = await api.Get<Feed>("/api/notifications");
            if (until(feed)) return feed;
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        return feed;
    }

    /// <summary>Adds a second person to the team so they can be assigned work.</summary>
    private async Task<TestApi> AddTeammateAsync(Workspace workspace)
    {
        var teammate = await workspace.AddOrganizationMemberAsync(factory);

        await workspace.Owner.Post($"/api/teams/{workspace.TeamId}/members",
            new { userId = teammate.UserId });

        await workspace.Owner.Post($"/api/projects/{workspace.ProjectId}/roles",
            new { userId = teammate.UserId, role = "Contributor" });

        return teammate;
    }

    private async Task<Guid> AssignedItemAsync(Workspace workspace, TestApi assignee, string title)
    {
        var created = await workspace.Owner.Post<Created>(
            $"/api/projects/{workspace.ProjectId}/workitems",
            new { title, type = "Task", teamId = workspace.TeamId, assigneeId = assignee.UserId });

        return created.Id;
    }

    // ── Being told ────────────────────────────────────────────────────────────

    /// <summary>Being assigned work tells you, and names the item the way you would.</summary>
    [Fact]
    public async Task BeingAssignedWorkNotifiesYou()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var teammate = await AddTeammateAsync(workspace);

        await AssignedItemAsync(workspace, teammate, "please look at this");

        var feed = await WaitForAsync(teammate, f => f.Items.Count > 0);

        var notification = Assert.Single(feed.Items);
        Assert.Equal("WorkItemAssigned", notification.Type);
        Assert.Contains("assigned to you", notification.Title);

        // The reference is carried so the bell renders without a join, and it is what a developer
        // would put in a branch name.
        Assert.Matches(@"^[A-Z][A-Z0-9]*-\d+$", notification.Reference);
        Assert.False(notification.IsRead);
        Assert.Equal(1, feed.UnreadCount);
    }

    /// <summary>
    /// Nobody is told about their own action.
    /// </summary>
    /// <remarks>
    /// The difference between a bell worth opening and one people mute in the first week. Asserted
    /// across all three verbs, because each resolves its recipients separately and any one of them
    /// could regress alone.
    /// </remarks>
    [Fact]
    public async Task YourOwnActionsNeverNotifyYou()
    {
        var workspace = await Workspace.CreateAsync(factory);

        // Assigned to themselves, transitioned by themselves, commented on by themselves.
        var itemId = await workspace.AddWorkItemAsync("all my own work");
        await workspace.Owner.Patch<object>($"/api/workitems/{itemId}/state", new { state = "Active" });
        await workspace.Owner.Post($"/api/workitems/{itemId}/comments", new { body = "talking to myself" });

        // Long enough for the outbox to have delivered several times over.
        await Task.Delay(2000);

        var feed = await workspace.Owner.Get<Feed>("/api/notifications");

        Assert.Empty(feed.Items);
        Assert.Equal(0, feed.UnreadCount);
    }

    /// <summary>Assignment starts you watching, so later changes reach you too.</summary>
    /// <remarks>
    /// Implicit watching is what makes the bell useful without anybody discovering a "watch" button
    /// first. If it required an explicit action the bell would stay empty for most people.
    /// </remarks>
    [Fact]
    public async Task BeingAssignedStartsYouWatching()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var teammate = await AddTeammateAsync(workspace);

        var itemId = await AssignedItemAsync(workspace, teammate, "watch me");
        await WaitForAsync(teammate, f => f.Items.Count > 0);

        // Somebody else moves it.
        await workspace.Owner.Patch<object>($"/api/workitems/{itemId}/state", new { state = "Active" });

        var feed = await WaitForAsync(
            teammate, f => f.Items.Any(n => n.Type == "WorkItemStateChanged"));

        var moved = feed.Items.First(n => n.Type == "WorkItemStateChanged");
        Assert.Contains("moved to Active", moved.Title);
    }

    /// <summary>A comment reaches the people watching, and not its author.</summary>
    [Fact]
    public async Task CommentsNotifyWatchers()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var teammate = await AddTeammateAsync(workspace);

        var itemId = await AssignedItemAsync(workspace, teammate, "discuss this");
        await WaitForAsync(teammate, f => f.Items.Count > 0);

        await workspace.Owner.Post($"/api/workitems/{itemId}/comments",
            new { body = "First line of the comment\nand a second line nobody needs in a bell" });

        var feed = await WaitForAsync(teammate, f => f.Items.Any(n => n.Type == "WorkItemCommented"));
        var comment = feed.Items.First(n => n.Type == "WorkItemCommented");

        // First line only: a bell row is one line high.
        Assert.Equal("First line of the comment", comment.Detail);
    }

    // ── The one this product exists to send ───────────────────────────────────

    /// <summary>
    /// Work reaching the QA lane tells whoever can certify it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The notification the QA gate depends on. Git carries work as far as "merged, awaiting test" on
    /// its own, and a gate nobody is told about is just a column people have to remember to check.
    /// </para>
    /// <para>
    /// Reaching the right people needs the reverse permission lookup — whoever holds
    /// <c>workitem:verify</c> here, however they came by it. The tester below holds it through a
    /// project role and has never touched the item.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ReachingTheQaLaneNotifiesWhoeverCanCertify()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var developer = await AddTeammateAsync(workspace);

        var tester = await workspace.AddOrganizationMemberAsync(factory);
        await workspace.Owner.Post($"/api/projects/{workspace.ProjectId}/roles",
            new { userId = tester.UserId, role = "Tester" });

        var itemId = await AssignedItemAsync(workspace, developer, "ready for test");

        await developer.Patch<object>($"/api/workitems/{itemId}/state", new { state = "Active" });
        await developer.Patch<object>($"/api/workitems/{itemId}/state", new { state = "Resolved" });

        var feed = await WaitForAsync(
            tester, f => f.Items.Any(n => n.Type == "WorkItemAwaitingVerification"));

        var awaiting = feed.Items.First(n => n.Type == "WorkItemAwaitingVerification");
        Assert.Contains("awaiting QA", awaiting.Title);
    }

    /// <summary>
    /// Somebody who cannot certify is not added to the QA queue.
    /// </summary>
    /// <remarks>
    /// The recipient list is derived from the permission table rather than from a role name, so this
    /// is what stops the QA notification becoming a broadcast to the whole project.
    /// </remarks>
    [Fact]
    public async Task AContributorIsNotToldWorkIsAwaitingQa()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var developer = await AddTeammateAsync(workspace);
        var otherContributor = await AddTeammateAsync(workspace);

        var itemId = await AssignedItemAsync(workspace, developer, "not their queue");

        await developer.Patch<object>($"/api/workitems/{itemId}/state", new { state = "Active" });
        await developer.Patch<object>($"/api/workitems/{itemId}/state", new { state = "Resolved" });

        await Task.Delay(2000);

        var feed = await otherContributor.Get<Feed>("/api/notifications");

        Assert.DoesNotContain(feed.Items, n => n.Type == "WorkItemAwaitingVerification");
    }

    // ── Read state ────────────────────────────────────────────────────────────

    [Fact]
    public async Task NotificationsCanBeMarkedRead()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var teammate = await AddTeammateAsync(workspace);

        await AssignedItemAsync(workspace, teammate, "mark me read");

        var feed = await WaitForAsync(teammate, f => f.Items.Count > 0);
        var notification = feed.Items[0];

        var marked = await teammate.PostRaw($"/api/notifications/{notification.Id}/read", new { });
        Assert.Equal(HttpStatusCode.NoContent, marked.StatusCode);

        var after = await teammate.Get<Feed>("/api/notifications");
        Assert.True(after.Items[0].IsRead);
        Assert.Equal(0, after.UnreadCount);

        // Only unread, please.
        Assert.Empty((await teammate.Get<Feed>("/api/notifications?unreadOnly=true")).Items);
    }

    /// <summary>
    /// One person cannot mark another's notification read.
    /// </summary>
    /// <remarks>
    /// 404 rather than 403: the recipient is part of the update's predicate, so somebody else's
    /// notification is indistinguishable from one that does not exist — which is the answer that
    /// says least.
    /// </remarks>
    [Fact]
    public async Task YouCannotMarkSomebodyElsesNotificationRead()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var teammate = await AddTeammateAsync(workspace);

        await AssignedItemAsync(workspace, teammate, "not yours");

        var feed = await WaitForAsync(teammate, f => f.Items.Count > 0);

        var response = await workspace.Owner.PostRaw(
            $"/api/notifications/{feed.Items[0].Id}/read", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False((await teammate.Get<Feed>("/api/notifications")).Items[0].IsRead);
    }

    [Fact]
    public async Task EverythingCanBeMarkedReadAtOnce()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var teammate = await AddTeammateAsync(workspace);

        await AssignedItemAsync(workspace, teammate, "one");
        await AssignedItemAsync(workspace, teammate, "two");

        await WaitForAsync(teammate, f => f.UnreadCount >= 2);

        await teammate.Post("/api/notifications/read-all", new { });

        Assert.Equal(0, (await teammate.Get<Feed>("/api/notifications")).UnreadCount);
    }

    // ── Watching ──────────────────────────────────────────────────────────────

    /// <summary>Watching can be started explicitly, for work somebody else is doing.</summary>
    [Fact]
    public async Task WatchingCanBeStartedExplicitly()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var observer = await AddTeammateAsync(workspace);

        var itemId = await workspace.AddWorkItemAsync("somebody else's work");

        Assert.False((await observer.Get<WatchState>($"/api/workitems/{itemId}/watch")).IsWatching);

        await observer.Post<WatchState>($"/api/workitems/{itemId}/watch", new { });

        Assert.True((await observer.Get<WatchState>($"/api/workitems/{itemId}/watch")).IsWatching);

        await workspace.Owner.Patch<object>($"/api/workitems/{itemId}/state", new { state = "Active" });

        var feed = await WaitForAsync(observer, f => f.Items.Count > 0);
        Assert.Contains(feed.Items, n => n.Type == "WorkItemStateChanged");
    }

    /// <summary>
    /// Unwatching is remembered, so a later comment does not re-subscribe you.
    /// </summary>
    /// <remarks>
    /// The reason a watcher row records a decision rather than being deleted. Implicit watching is a
    /// convenience, and a convenience that overrides a stated preference is an annoyance.
    /// </remarks>
    [Fact]
    public async Task UnwatchingSurvivesSomethingThatWouldOtherwiseResubscribeYou()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var teammate = await AddTeammateAsync(workspace);

        var itemId = await AssignedItemAsync(workspace, teammate, "leave me alone");
        await WaitForAsync(teammate, f => f.Items.Count > 0);

        await teammate.DeleteRaw($"/api/workitems/{itemId}/watch");
        await teammate.Post("/api/notifications/read-all", new { });

        // Commenting auto-watches its author, and would auto-watch the assignee were it not for the
        // remembered decision.
        await workspace.Owner.Post($"/api/workitems/{itemId}/comments", new { body = "still here?" });
        await Task.Delay(2000);

        var feed = await teammate.Get<Feed>("/api/notifications");
        Assert.DoesNotContain(feed.Items, n => n.Type == "WorkItemCommented" && !n.IsRead);
    }

    /// <summary>
    /// A notification carries enough to link to the thing it is about.
    /// </summary>
    /// <remarks>
    /// The bell is global — it renders outside any organization's routes — so a client holding only
    /// a project id cannot build a URL without a round trip per row. The slug is joined on read
    /// rather than stored on the notification, because a slug is renameable and a copy taken when
    /// the notification was raised would point at a URL that had since stopped existing.
    /// </remarks>
    [Fact]
    public async Task ANotificationCarriesWhatALinkToItNeeds()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var teammate = await AddTeammateAsync(workspace);

        var item = await AssignedItemAsync(workspace, teammate, "click through to me");

        var feed = await WaitForAsync(teammate, f => f.Items.Count > 0);

        var notification = Assert.Single(feed.Items);

        Assert.Equal(item, notification.EntityId);
        Assert.Equal(workspace.ProjectId, notification.ProjectId);
        Assert.False(string.IsNullOrWhiteSpace(notification.OrganizationSlug));
    }

    private sealed record Created(Guid Id);
    private sealed record WatchState(Guid WorkItemId, bool IsWatching);

    private sealed record NotificationView(
        Guid Id, string Type, string Title, string? Detail, string Reference,
        Guid EntityId, Guid ProjectId, string OrganizationSlug, string ActorName,
        bool IsRead, DateTime CreatedAt);

    private sealed record Feed(List<NotificationView> Items, int UnreadCount);
}
