namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That a work item lands in the backlog whoever created it and wherever from.
/// </summary>
/// <remarks>
/// <para>
/// <c>BacklogItem</c> documents itself as "one row per work item". It was one row per work item
/// somebody remembered to add: only the backlog's own endpoint created one, so anything made from
/// the Work Items page or a board had no rank and never appeared in the backlog — the one screen
/// whose job is deciding what to do next.
/// </para>
/// <para>
/// The rank is the point. A backlog row is kept when its item is pulled into a sprint precisely so
/// the item returns to its old position if it comes back out, and an item that never had a row has
/// no position to return to.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class BacklogMembershipTests(BoardSyncApiFactory factory)
{
    [Fact]
    public async Task ACreatedWorkItemAppearsInTheBacklog()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var title = $"backlog-{Guid.NewGuid():N}"[..24];

        // Created through the work item endpoint, which is what every surface except the backlog's
        // own "Add" button uses.
        var workItemId = await workspace.AddWorkItemAsync(title);

        var appeared = await Poll(async () =>
        {
            var backlog = await workspace.Owner.Get<Paged<BacklogEntry>>(
                $"/api/projects/{workspace.ProjectId}/backlog?page=1&pageSize=50");

            return backlog.Items.Any(entry => entry.WorkItemId == workItemId);
        }, within: TimeSpan.FromSeconds(10));

        Assert.True(appeared,
            "A newly created work item never reached the backlog, so it has no rank and does not " +
            "appear on the one screen for deciding what to work on next.");
    }

    [Fact]
    public async Task EveryTypeGetsABacklogRow()
    {
        var workspace = await Workspace.CreateAsync(factory);

        // Boards used to create only Tasks. They no longer do, so the subscriber has to be
        // indifferent to type — an Epic needs a rank as much as a Task does.
        var epicId = await workspace.AddWorkItemAsync(
            $"epic-{Guid.NewGuid():N}"[..20], "Epic");

        var bugId = await workspace.AddWorkItemAsync(
            $"bug-{Guid.NewGuid():N}"[..20], "Bug");

        var both = await Poll(async () =>
        {
            var backlog = await workspace.Owner.Get<Paged<BacklogEntry>>(
                $"/api/projects/{workspace.ProjectId}/backlog?page=1&pageSize=50");

            var ids = backlog.Items.Select(e => e.WorkItemId).ToHashSet();

            return ids.Contains(epicId) && ids.Contains(bugId);
        }, within: TimeSpan.FromSeconds(10));

        Assert.True(both, "An Epic or a Bug did not reach the backlog.");
    }

    [Fact]
    public async Task AddingAnItemAlreadyInTheBacklogDoesNotDuplicateIt()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var workItemId = await workspace.AddWorkItemAsync(
            $"dupe-{Guid.NewGuid():N}"[..20]);

        await Poll(async () =>
        {
            var backlog = await workspace.Owner.Get<Paged<BacklogEntry>>(
                $"/api/projects/{workspace.ProjectId}/backlog?page=1&pageSize=50");

            return backlog.Items.Any(entry => entry.WorkItemId == workItemId);
        }, within: TimeSpan.FromSeconds(10));

        // The subscriber has already added it. Adding again by hand must be a no-op rather than a
        // second row competing for a rank.
        await workspace.Owner.Post<object>(
            $"/api/projects/{workspace.ProjectId}/backlog",
            new { workItemId });

        var backlog = await workspace.Owner.Get<Paged<BacklogEntry>>(
            $"/api/projects/{workspace.ProjectId}/backlog?page=1&pageSize=50");

        Assert.Equal(1, backlog.Items.Count(entry => entry.WorkItemId == workItemId));
    }

    private static async Task<bool> Poll(Func<Task<bool>> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }

        return false;
    }

    private sealed record BacklogEntry(Guid BacklogItemId, Guid WorkItemId, string Reference, string Title);
    private sealed record Paged<T>(List<T> Items, int TotalCount, int Page, int PageSize);
}
