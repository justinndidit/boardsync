using BoardSync.Api.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That a finished sprint's velocity holds still.
/// </summary>
/// <remarks>
/// <para>
/// Completed points were counted from a work item's <i>current</i> state, so closing a stale item
/// weeks later raised a past sprint retroactively — a chart that read 24 last week read 29 today,
/// with no event a reader could point at. Velocity is the figure teams plan from, which makes it
/// the one number that must not move once its window has closed.
/// </para>
/// <para>
/// The burndown beside it always counted closures against the day they happened, so the two
/// disagreed about the same sprint on the same page. They now share one predicate.
/// </para>
/// <para>
/// These tests age a sprint by writing its dates directly. The API refuses to create or move a
/// sprint into the past — correctly — so this is the only way to reach the state that real elapsed
/// time produces.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class VelocityStabilityTests(BoardSyncApiFactory factory)
{
    [Fact]
    public async Task ClosingWorkAfterASprintEndsDoesNotRaiseItsVelocity()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var sprint = await workspace.Owner.Post<Created>(
            $"/api/teams/{workspace.TeamId}/sprints",
            new
            {
                goal = "finished, then tidied up afterwards",
                startDate = DateTime.UtcNow.AddDays(1),
                endDate = DateTime.UtcNow.AddDays(15),
            });

        await workspace.Owner.Patch<object>(
            $"/api/sprints/{sprint.Id}/status", new { status = "Active" });

        var late = await workspace.AddWorkItemAsync("Slipped past the end");

        await workspace.Owner.Post($"/api/sprints/{sprint.Id}/workitems",
            new { workItemId = late, storyPoints = 5 });

        await workspace.Owner.Put($"/api/workitems/{late}",
            new { title = "Slipped past the end", storyPoints = 5, priority = "Medium",
                  tags = Array.Empty<string>() });

        await workspace.Owner.Post<object>(
            $"/api/sprints/{sprint.Id}/close",
            new { incompleteItemsDestination = "ReturnToBacklog" });

        // The sprint is now behind us, as it would be after two real weeks.
        await AgeSprintAsync(sprint.Id, TimeSpan.FromDays(30));

        // ...and only now does somebody finish the work and take it through QA.
        foreach (var state in new[] { "Active", "InReview", "Resolved", "Closed" })
        {
            await workspace.Owner.Patch<object>(
                $"/api/workitems/{late}/state", new { state });
        }

        var velocity = await workspace.Owner.Get<Velocity>(
            $"/api/projects/{workspace.ProjectId}/reports/velocity?sprints=6");

        var point = velocity.Sprints.Single(p => p.SprintId == sprint.Id);

        /*
         * The work was committed to this sprint and delivered outside it. It counts as committed,
         * because it was; it does not count as completed, because the sprint did not complete it.
         */
        Assert.Equal(5, point.CommittedPoints);
        Assert.Equal(0, point.CompletedPoints);
    }

    [Fact]
    public async Task WorkFinishedInsideTheSprintStillCounts()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var sprint = await workspace.Owner.Post<Created>(
            $"/api/teams/{workspace.TeamId}/sprints",
            new
            {
                goal = "delivered on time",
                startDate = DateTime.UtcNow.AddDays(1),
                endDate = DateTime.UtcNow.AddDays(15),
            });

        await workspace.Owner.Patch<object>(
            $"/api/sprints/{sprint.Id}/status", new { status = "Active" });

        var done = await workspace.AddWorkItemAsync("Finished in the window");

        await workspace.Owner.Post($"/api/sprints/{sprint.Id}/workitems",
            new { workItemId = done });

        await workspace.Owner.Put($"/api/workitems/{done}",
            new { title = "Finished in the window", storyPoints = 8, priority = "Medium",
                  tags = Array.Empty<string>() });

        // Closed while the sprint is still running — the ordinary case, and the one that must not
        // be broken by the fix above.
        foreach (var state in new[] { "Active", "InReview", "Resolved", "Closed" })
        {
            await workspace.Owner.Patch<object>(
                $"/api/workitems/{done}/state", new { state });
        }

        await workspace.Owner.Post<object>(
            $"/api/sprints/{sprint.Id}/close",
            new { incompleteItemsDestination = "ReturnToBacklog" });

        /*
         * Aged by less than the sprint's own length, so its window still contains the moment the
         * work was closed. Ageing past that would be testing the case above, not this one.
         */
        await AgeSprintAsync(sprint.Id, TimeSpan.FromDays(10));

        var velocity = await workspace.Owner.Get<Velocity>(
            $"/api/projects/{workspace.ProjectId}/reports/velocity?sprints=6");

        var point = velocity.Sprints.Single(p => p.SprintId == sprint.Id);

        Assert.Equal(8, point.CompletedPoints);
    }

    [Fact]
    public async Task VelocityAgreesWithWhereTheBurndownLands()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var sprint = await workspace.Owner.Post<Created>(
            $"/api/teams/{workspace.TeamId}/sprints",
            new
            {
                goal = "two charts, one answer",
                startDate = DateTime.UtcNow.AddDays(1),
                endDate = DateTime.UtcNow.AddDays(15),
            });

        await workspace.Owner.Patch<object>(
            $"/api/sprints/{sprint.Id}/status", new { status = "Active" });

        var finished = await workspace.AddWorkItemAsync("Landed");
        var slipped = await workspace.AddWorkItemAsync("Did not land");

        foreach (var (id, pts) in new[] { (finished, 3), (slipped, 5) })
        {
            await workspace.Owner.Post($"/api/sprints/{sprint.Id}/workitems",
                new { workItemId = id });

            await workspace.Owner.Put($"/api/workitems/{id}",
                new { title = "sized", storyPoints = pts, priority = "Medium",
                      tags = Array.Empty<string>() });
        }

        foreach (var state in new[] { "Active", "InReview", "Resolved", "Closed" })
        {
            await workspace.Owner.Patch<object>(
                $"/api/workitems/{finished}/state", new { state });
        }

        await workspace.Owner.Post<object>(
            $"/api/sprints/{sprint.Id}/close",
            new { incompleteItemsDestination = "ReturnToBacklog" });

        // Still containing the closure, so this is a genuinely mixed sprint: one item delivered,
        // one left. A sprint where nothing counted would satisfy the arithmetic trivially.
        await AgeSprintAsync(sprint.Id, TimeSpan.FromDays(10));

        var report = await workspace.Owner.Get<Report>(
            $"/api/sprints/{sprint.Id}/report");

        var last = report.Burndown[^1];

        /*
         * The property that was untrue before: what the burndown says is left at the end, and what
         * the summary says was delivered, add up to what the sprint committed.
         */
        Assert.Equal(
            report.Summary.CommittedPoints,
            report.Summary.CompletedPoints + last.RemainingPoints);
    }

    /// <summary>Moves a sprint's window into the past, as elapsed time would.</summary>
    private async Task AgeSprintAsync(Guid sprintId, TimeSpan by)
    {
        using var scope = factory.Services.CreateScope();

        var context = scope.ServiceProvider
            .GetRequiredService<BoardSyncDbContext>();

        var sprint = await context.Sprints
            .FirstAsync(s => s.Id == sprintId);

        sprint.StartDate -= by;
        sprint.EndDate -= by;

        await context.SaveChangesAsync();
    }

    private sealed record Created(Guid Id);
    private sealed record VelocityPoint(Guid SprintId, int CommittedPoints, int CompletedPoints);
    private sealed record Velocity(List<VelocityPoint> Sprints);
    private sealed record Summary(int CommittedPoints, int CompletedPoints);
    private sealed record BurndownPoint(int RemainingPoints);
    private sealed record Report(Summary Summary, List<BurndownPoint> Burndown);
}
