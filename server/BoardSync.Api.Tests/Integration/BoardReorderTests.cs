namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That reordering a board's columns is published like every other change to it.
/// </summary>
/// <remarks>
/// <para>
/// Audit finding 16: <c>ReorderColumnsAsync</c> saved and emitted nothing, alone among the board's
/// mutations. It read as cosmetic — an activity feed missing one line — right up until boards
/// started updating live. Then it meant a person dragging a column saw a board that nobody else
/// did, with no error and nothing to notice.
/// </para>
/// <para>
/// This is an integration test rather than a unit one deliberately. The defect was never in the
/// reordering, which always worked; it was in what the write did <i>not</i> publish, and only the
/// far end of the outbox can tell the difference.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class BoardReorderTests(BoardSyncApiFactory factory)
{
    [Fact]
    public async Task ReorderingColumnsPublishesABoardChange()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var board = await workspace.Owner.Get<Board>(
            $"/api/projects/{workspace.ProjectId}/board");

        var original = board.Columns
            .OrderBy(column => column.Position)
            .Select(column => column.Id)
            .ToList();

        // Swap the first two. A permutation the board is not already in, so the no-op guard does
        // not swallow it — which is the guard's own test, in the direction that matters.
        var reordered = new List<Guid>(original);
        (reordered[0], reordered[1]) = (reordered[1], reordered[0]);

        await workspace.Owner.Patch<object>(
            $"/api/boards/{board.Id}/columns/reorder",
            new { columnIds = reordered });

        var published = await Poll(async () =>
        {
            var feed = await workspace.Owner.Get<Paged<Activity>>(
                $"/api/orgs/{workspace.OrganizationId}/activity?page=1&pageSize=50");

            return feed.Items.Any(entry =>
                entry.EntityId == board.Id
                && (entry.Detail?.Contains("reorder", StringComparison.OrdinalIgnoreCase) == true
                    || entry.Title.Contains("reorder", StringComparison.OrdinalIgnoreCase)));
        }, within: TimeSpan.FromSeconds(10));

        Assert.True(published,
            "Reordering columns published no board change, so nothing watching the board was told.");
    }

    [Fact]
    public async Task TheNewOrderIsWhatTheBoardReturns()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var board = await workspace.Owner.Get<Board>(
            $"/api/projects/{workspace.ProjectId}/board");

        var original = board.Columns
            .OrderBy(column => column.Position)
            .Select(column => column.Id)
            .ToList();

        var reordered = new List<Guid>(original);
        (reordered[0], reordered[1]) = (reordered[1], reordered[0]);

        await workspace.Owner.Patch<object>(
            $"/api/boards/{board.Id}/columns/reorder",
            new { columnIds = reordered });

        var after = await workspace.Owner.Get<Board>(
            $"/api/projects/{workspace.ProjectId}/board");

        Assert.Equal(
            reordered,
            after.Columns.OrderBy(c => c.Position).Select(c => c.Id).ToList());
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

    private sealed record Board(Guid Id, List<Column> Columns);
    private sealed record Column(Guid Id, string Name, string MappedState, int Position);
    private sealed record Paged<T>(List<T> Items, int TotalCount, int Page, int PageSize);
    private sealed record Activity(Guid Id, string Title, string Verb, Guid EntityId, string? Detail);
}
