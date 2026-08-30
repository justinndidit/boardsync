namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That a column's cards come back in a defined order.
/// </summary>
/// <remarks>
/// <para>
/// There was no ordering on the board query at all — cards arrived in whatever order Postgres
/// produced. Two people could see the same column differently, a refetch could reshuffle it under
/// one of them, and nothing about that was visible as a failure.
/// </para>
/// <para>
/// <c>Rank</c> is the key the move and reorder endpoints maintain. <c>Position</c> is written only
/// by the bulk reorder and is not the authority.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class BoardOrderingTests(BoardSyncApiFactory factory)
{
    [Fact]
    public async Task CardsComeBackInRankOrderAndCarryTheirRank()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var sprint = await workspace.Owner.Post<Created>(
            $"/api/teams/{workspace.TeamId}/sprints",
            new
            {
                goal = "ordering",
                startDate = DateTime.UtcNow.AddDays(1),
                endDate = DateTime.UtcNow.AddDays(15),
            });

        await workspace.Owner.Patch<object>(
            $"/api/sprints/{sprint.Id}/status", new { status = "Active" });

        // Added in a known sequence; each append takes a rank above the last.
        var added = new List<Guid>();

        foreach (var title in new[] { "first", "second", "third" })
        {
            var id = await workspace.AddWorkItemAsync(title);

            await workspace.Owner.Post(
                $"/api/sprints/{sprint.Id}/workitems", new { workItemId = id });

            added.Add(id);
        }

        var board = await workspace.Owner.Get<Board>(
            $"/api/projects/{workspace.ProjectId}/board");

        var lane = board.Columns.Single(c => c.MappedState == "New");

        Assert.Equal(added, lane.Cards.Select(c => c.WorkItemId).ToList());

        // Strictly increasing, which is what lets a client insert into it rather than append.
        Assert.Equal(
            lane.Cards.Select(c => c.Rank).OrderBy(r => r).ToList(),
            lane.Cards.Select(c => c.Rank).ToList());

        Assert.Equal(
            lane.Cards.Select(c => c.Rank).Distinct().Count(),
            lane.Cards.Count);
    }

    [Fact]
    public async Task AMovedCardKeepsItsRankAcrossColumns()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var sprint = await workspace.Owner.Post<Created>(
            $"/api/teams/{workspace.TeamId}/sprints",
            new
            {
                goal = "rank survives a state change",
                startDate = DateTime.UtcNow.AddDays(1),
                endDate = DateTime.UtcNow.AddDays(15),
            });

        await workspace.Owner.Patch<object>(
            $"/api/sprints/{sprint.Id}/status", new { status = "Active" });

        var ids = new List<Guid>();

        foreach (var title in new[] { "a", "b", "c" })
        {
            var id = await workspace.AddWorkItemAsync(title);

            await workspace.Owner.Post(
                $"/api/sprints/{sprint.Id}/workitems", new { workItemId = id });

            ids.Add(id);
        }

        var before = await workspace.Owner.Get<Board>(
            $"/api/projects/{workspace.ProjectId}/board");

        var middleRank = before.Columns
            .Single(c => c.MappedState == "New")
            .Cards.Single(c => c.WorkItemId == ids[1])
            .Rank;

        await workspace.Owner.Patch<object>(
            $"/api/workitems/{ids[1]}/state", new { state = "Active" });

        var after = await workspace.Owner.Get<Board>(
            $"/api/projects/{workspace.ProjectId}/board");

        /*
         * Rank belongs to the sprint, not the column. Changing state moves a card between lanes and
         * leaves its place in the sprint's ordering alone — which is what lets a live update insert
         * it correctly without asking the server where it went.
         */
        var moved = after.Columns
            .Single(c => c.MappedState == "Active")
            .Cards.Single();

        Assert.Equal(ids[1], moved.WorkItemId);
        Assert.Equal(middleRank, moved.Rank);
    }

    private sealed record Created(Guid Id);
    private sealed record Card(Guid WorkItemId, decimal Rank, string Reference);
    private sealed record Column(Guid Id, string MappedState, List<Card> Cards);
    private sealed record Board(Guid Id, List<Column> Columns);
}
