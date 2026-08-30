using BoardSync.Api.Data;
using BoardSync.Api.Modules.Sprints.Scheduling;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That a sprint's dates actually do something.
/// </summary>
/// <remarks>
/// <para>
/// Nothing ran on a schedule, so the dates were decoration: a sprint stayed in <c>Planning</c> past
/// its start indefinitely, and an <c>Active</c> one sat open past its end with its unfinished work
/// still inside it — in no backlog, in no other sprint, on no board. The client made it worse by
/// deriving a display status from the dates, so such a sprint showed as "Closed" while its real
/// status said otherwise and its work stayed stranded.
/// </para>
/// <para>
/// Passes are driven explicitly rather than by waiting on the timer — a minute per assertion is not
/// a test suite. Sprints are aged by writing their dates, because the API refuses to create one in
/// the past, correctly.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class SprintSchedulerTests(BoardSyncApiFactory factory)
{
    private SprintScheduler Scheduler() =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SprintScheduler>.Instance);

    [Fact]
    public async Task ASprintStartsWhenItsStartTimeArrives()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var sprint = await CreateSprintAsync(workspace, "starts itself");

        await ShiftAsync(sprint, TimeSpan.FromDays(2));

        await Scheduler().RunOnceAsync();

        Assert.Equal("Active", await StatusOfAsync(sprint));
    }

    [Fact]
    public async Task ASprintWaitsIfTheTeamIsAlreadyRunningOne()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var running = await CreateSprintAsync(workspace, "already going");

        // The next fortnight, because a team's sprints may not overlap.
        var waiting = await CreateSprintAsync(
            workspace, "queued behind it", startsInDays: 16);

        await workspace.Owner.Patch<object>(
            $"/api/sprints/{running}/status", new { status = "Active" });

        // Far enough back that its start has passed but the running sprint is still going.
        await ShiftAsync(waiting, TimeSpan.FromDays(17));

        await Scheduler().RunOnceAsync();

        /*
         * One active sprint per team. Left in Planning and logged rather than started anyway or
         * silently skipped: Planning is visible on the board, and somebody has to close the old one.
         */
        Assert.Equal("Planning", await StatusOfAsync(waiting));
        Assert.Equal("Active", await StatusOfAsync(running));
    }

    [Fact]
    public async Task ASprintClosesWhenItsEndTimeArrivesAndReturnsUnfinishedWork()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var sprint = await CreateSprintAsync(workspace, "ends itself");

        await workspace.Owner.Patch<object>(
            $"/api/sprints/{sprint}/status", new { status = "Active" });

        // "Closed" rather than "finished": see the note on the report assertion below.
        var closed = await workspace.AddWorkItemAsync("taken through the QA gate");
        var unfinished = await workspace.AddWorkItemAsync("did not land");

        foreach (var id in new[] { closed, unfinished })
        {
            await workspace.Owner.Post(
                $"/api/sprints/{sprint}/workitems", new { workItemId = id });
        }

        foreach (var state in new[] { "Active", "InReview", "Resolved", "Closed" })
        {
            await workspace.Owner.Patch<object>(
                $"/api/workitems/{closed}/state", new { state });
        }

        // The whole window is now behind us.
        await ShiftAsync(sprint, TimeSpan.FromDays(30));

        await Scheduler().RunOnceAsync();

        Assert.Equal("Completed", await StatusOfAsync(sprint));

        var backlog = await workspace.Owner.Get<Paged<BacklogEntry>>(
            $"/api/projects/{workspace.ProjectId}/backlog?page=1&pageSize=50");

        Assert.Contains(backlog.Items, e => e.WorkItemId == unfinished);

        // Work that reached Closed is not returned — only what was unfinished moves.
        Assert.DoesNotContain(backlog.Items, e => e.WorkItemId == closed);

        var report = await workspace.Owner.Get<Report>($"/api/sprints/{sprint}/report");

        // The sprint still reports having committed to both, which is what makes velocity mean
        // anything — dropping the returned item would report one and one, a perfect sprint.
        Assert.Equal(2, report.Summary.CommittedItems);

        /*
         * And zero completed, which looks wrong and is not.
         *
         * Completed points are counted at the sprint's end date, so work closed after the window
         * counts toward nothing. This fixture closes the item and *then* moves the window into the
         * past, so from the report's point of view it was finished three weeks after the sprint
         * ended. A real sprint closes at its end with its finished work already behind it.
         */
        Assert.Equal(0, report.Summary.CompletedItems);
    }

    [Fact]
    public async Task ASprintInsideItsWindowIsLeftAlone()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var sprint = await CreateSprintAsync(workspace, "still running");

        await workspace.Owner.Patch<object>(
            $"/api/sprints/{sprint}/status", new { status = "Active" });

        // Started, but nowhere near its end.
        await ShiftAsync(sprint, TimeSpan.FromDays(2));

        await Scheduler().RunOnceAsync();

        Assert.Equal("Active", await StatusOfAsync(sprint));
    }

    [Fact]
    public async Task APassWithNothingDueChangesNothing()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var sprint = await CreateSprintAsync(workspace, "not yet");

        // Idempotence matters: the loop runs every minute for the life of the process.
        await Scheduler().RunOnceAsync();
        await Scheduler().RunOnceAsync();

        Assert.Equal("Planning", await StatusOfAsync(sprint));
    }

    /// <summary>
    /// A sprint in a window of its own.
    /// </summary>
    /// <remarks>
    /// <paramref name="startsInDays"/> exists because a team's sprints may not overlap — two
    /// fixtures in the same fortnight are refused with a 409, which is the rule working.
    /// </remarks>
    private static async Task<Guid> CreateSprintAsync(
        Workspace workspace, string goal, int startsInDays = 1, int lengthDays = 14)
    {
        var created = await workspace.Owner.Post<Created>(
            $"/api/teams/{workspace.TeamId}/sprints",
            new
            {
                goal,
                startDate = DateTime.UtcNow.AddDays(startsInDays),
                endDate = DateTime.UtcNow.AddDays(startsInDays + lengthDays),
            });

        return created.Id;
    }

    /// <summary>Moves a sprint's window back, as elapsed time would.</summary>
    private async Task ShiftAsync(Guid sprintId, TimeSpan by)
    {
        using var scope = factory.Services.CreateScope();

        var context = scope.ServiceProvider
            .GetRequiredService<BoardSyncDbContext>();

        var sprint = await context.Sprints.FirstAsync(s => s.Id == sprintId);

        sprint.StartDate -= by;
        sprint.EndDate -= by;

        await context.SaveChangesAsync();
    }

    private async Task<string> StatusOfAsync(Guid sprintId)
    {
        using var scope = factory.Services.CreateScope();

        var context = scope.ServiceProvider
            .GetRequiredService<BoardSyncDbContext>();

        return (await context.Sprints
            .AsNoTracking()
            .FirstAsync(s => s.Id == sprintId))
            .Status.ToString();
    }

    private sealed record Created(Guid Id);
    private sealed record BacklogEntry(Guid BacklogItemId, Guid WorkItemId);
    private sealed record Summary(int CommittedItems, int CompletedItems);
    private sealed record Report(Summary Summary);
    private sealed record Paged<T>(List<T> Items, int TotalCount, int Page, int PageSize);
}
