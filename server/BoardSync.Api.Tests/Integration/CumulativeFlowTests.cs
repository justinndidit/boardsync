namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// Where a project's work is piling up, day by day.
/// </summary>
/// <remarks>
/// <para>
/// The tab said "the API does not compute this" for months. The series was always derivable — it is
/// the same history reconstruction the burndown uses, counted across states instead of summed
/// across points — and computing it late rather than snapshotting it means the chart is correct for
/// days before the endpoint existed.
/// </para>
/// <para>
/// The two failures worth pinning are both about honesty on the axis: an item must not be counted
/// before it existed, and every state must appear on every point so a client stacking bands has the
/// same series to stack on each day.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class CumulativeFlowTests(BoardSyncApiFactory factory)
{
    [Fact]
    public async Task EveryItemIsCountedInExactlyOneState()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var untouched = await workspace.AddWorkItemAsync("Never started");
        var started = await workspace.AddWorkItemAsync("Underway");
        var finished = await workspace.AddWorkItemAsync("Done and verified");

        await workspace.Owner.Patch<object>(
            $"/api/workitems/{started}/state", new { state = "Active" });

        foreach (var state in new[] { "Active", "InReview", "Resolved", "Closed" })
        {
            await workspace.Owner.Patch<object>(
                $"/api/workitems/{finished}/state", new { state });
        }

        var flow = await workspace.Owner.Get<Flow>(
            $"/api/projects/{workspace.ProjectId}/reports/cumulative-flow?days=7");

        var today = flow.Points[^1];

        Assert.Equal(3, flow.TotalItems);
        Assert.Equal(1, today.New);
        Assert.Equal(1, today.Active);
        Assert.Equal(1, today.Closed);

        // The bands stack to the total. An item counted twice, or dropped, breaks the chart's one
        // real invariant — its height is how much work exists.
        Assert.Equal(
            flow.TotalItems,
            today.New + today.Active + today.InReview + today.Resolved + today.Closed);

        _ = untouched;
    }

    [Fact]
    public async Task WorkIsNotCountedBeforeItExisted()
    {
        var workspace = await Workspace.CreateAsync(factory);

        await workspace.AddWorkItemAsync("Written down today");

        var flow = await workspace.Owner.Get<Flow>(
            $"/api/projects/{workspace.ProjectId}/reports/cumulative-flow?days=7");

        /*
         * Not yet created is not the same as New. Counting an item on days before somebody wrote it
         * down would hold up the bottom band across the whole window and make every project look
         * like it started with a full backlog.
         */
        var yesterday = flow.Points[^2];

        Assert.Equal(0, yesterday.New);
        Assert.Equal(0, yesterday.Closed);

        Assert.Equal(1, flow.Points[^1].New);
    }

    [Fact]
    public async Task TheWindowIsTheDaysAskedForAndStopsAtToday()
    {
        var workspace = await Workspace.CreateAsync(factory);

        await workspace.AddWorkItemAsync("Something to count");

        var flow = await workspace.Owner.Get<Flow>(
            $"/api/projects/{workspace.ProjectId}/reports/cumulative-flow?days=14");

        Assert.Equal(14, flow.Points.Count);

        // Oldest first, and never padded into the future — a flat tail to the right reads as "no
        // progress" rather than "has not happened yet".
        Assert.True(flow.Points[0].Date < flow.Points[^1].Date);
        Assert.Equal(DateTime.UtcNow.Date, flow.Points[^1].Date.Date);
    }

    [Fact]
    public async Task AnOversizedWindowIsClampedRatherThanRefused()
    {
        var workspace = await Workspace.CreateAsync(factory);

        await workspace.AddWorkItemAsync("Anything");

        var flow = await workspace.Owner.Get<Flow>(
            $"/api/projects/{workspace.ProjectId}/reports/cumulative-flow?days=9999");

        // The series costs items times days, so the ceiling is real. Clamped like the velocity
        // endpoint's sprint count rather than rejected — the caller asked for "as much as you have".
        Assert.Equal(90, flow.Points.Count);
    }

    [Fact]
    public async Task AProjectWithNoWorkReturnsNoSeries()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var flow = await workspace.Owner.Get<Flow>(
            $"/api/projects/{workspace.ProjectId}/reports/cumulative-flow?days=7");

        // Empty rather than fourteen points of zero: a chart of nothing should say there is nothing,
        // not draw a flat line that looks like a project standing still.
        Assert.Empty(flow.Points);
        Assert.Equal(0, flow.TotalItems);
    }

    private sealed record Point(
        DateTime Date, int New, int Active, int InReview, int Resolved, int Closed);

    private sealed record Flow(List<Point> Points, int TotalItems);
}
