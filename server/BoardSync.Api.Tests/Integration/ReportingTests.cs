using System.Net;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That the delivery metrics reflect what actually happened on the board.
/// </summary>
/// <remarks>
/// <para>
/// Driven through the real endpoints rather than seeded, because the figures are reconstructed from
/// work item history and the point is that history is a faithful record. Seeding rows would test the
/// arithmetic against data the system would never produce.
/// </para>
/// <para>
/// The wait-for-QA figure is the one worth having: BoardSync can report it because the QA gate makes
/// <c>Resolved → Closed</c> a real transition somebody has to perform, rather than a convention
/// people follow when they remember.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class ReportingTests(BoardSyncApiFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record Sprint(Guid Id, int Number, string Status);

    private sealed record Summary(
        Guid SprintId, int Number, string? Goal, DateTime StartDate, DateTime EndDate, string Status,
        int CommittedPoints, int CompletedPoints, int CommittedItems, int CompletedItems,
        int AwaitingVerificationItems);

    private sealed record Burndown(DateTime Date, int RemainingPoints, int RemainingItems, double IdealPoints);

    private sealed record CycleTime(
        int ItemsMeasured, double? MedianPickupHours, double? MedianDevelopmentHours,
        double? MedianVerificationWaitHours, double? MedianTotalHours);

    private sealed record Report(
        Summary Summary, List<Burndown> Burndown, CycleTime CycleTime, int ItemsWithNoActivity);

    private sealed record VelocityPoint(
        Guid SprintId, int Number, DateTime EndDate, int CommittedPoints, int CompletedPoints);

    private sealed record Velocity(
        List<VelocityPoint> Sprints, double? AverageCompletedPoints, CycleTime CycleTime);

    /// <summary>
    /// Creates a sprint starting today and running a week.
    /// </summary>
    /// <remarks>
    /// <b>Today, not yesterday.</b> The API refuses a start date in the past —
    /// <c>SprintService.ValidateDates</c> — so a helper that back-dated the start only worked while
    /// nothing checked, and failed the moment anything did. A sprint cannot be made to have elapsed
    /// days through the API, which is why the burndown test below asserts the shape of a
    /// single-day series rather than a multi-day one.
    /// </remarks>
    private static async Task<Sprint> SprintAsync(Workspace workspace, string goal = "ship it")
    {
        return await workspace.Owner.Post<Sprint>(
            $"/api/teams/{workspace.TeamId}/sprints",
            new
            {
                goal,
                startDate = DateTime.UtcNow.Date,
                endDate = DateTime.UtcNow.Date.AddDays(7)
            });
    }

    private async Task<Guid> ItemInSprintAsync(Workspace workspace, Guid sprintId, string title, int points)
    {
        var item = await workspace.Owner.Post<Created>(
            $"/api/projects/{workspace.ProjectId}/workitems",
            new
            {
                title,
                type = "Task",
                teamId = workspace.TeamId,
                assigneeId = workspace.Owner.UserId,
                storyPoints = points
            });

        await workspace.Owner.Post($"/api/sprints/{sprintId}/workitems", new { workItemId = item.Id });

        return item.Id;
    }

    private static async Task MoveAsync(Workspace workspace, Guid itemId, params string[] states)
    {
        foreach (var state in states)
            await workspace.Owner.Patch<object>($"/api/workitems/{itemId}/state", new { state });
    }

    // ── The sprint report ─────────────────────────────────────────────────────

    [Fact]
    public async Task ASprintReportCountsWhatWasCommittedAndFinished()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await SprintAsync(workspace);

        var done = await ItemInSprintAsync(workspace, sprint.Id, "finished", 5);
        var inFlight = await ItemInSprintAsync(workspace, sprint.Id, "in progress", 3);
        await ItemInSprintAsync(workspace, sprint.Id, "not started", 2);

        await MoveAsync(workspace, done, "Active", "Resolved", "Closed");
        await MoveAsync(workspace, inFlight, "Active");

        var report = await workspace.Owner.Get<Report>($"/api/sprints/{sprint.Id}/report");

        Assert.Equal(10, report.Summary.CommittedPoints);
        Assert.Equal(5, report.Summary.CompletedPoints);
        Assert.Equal(3, report.Summary.CommittedItems);
        Assert.Equal(1, report.Summary.CompletedItems);

        // The item nobody started — usually the honest answer to "why did we not finish".
        Assert.Equal(1, report.ItemsWithNoActivity);
    }

    /// <summary>
    /// Work sitting in the QA lane is counted separately from work still being done.
    /// </summary>
    /// <remarks>
    /// A sprint that looks behind may be finished work nobody has verified, which is a different
    /// problem with a different owner. Rolling it into "not done" hides that.
    /// </remarks>
    [Fact]
    public async Task WorkAwaitingQaIsCountedSeparately()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await SprintAsync(workspace);

        var waiting = await ItemInSprintAsync(workspace, sprint.Id, "merged, untested", 8);
        await MoveAsync(workspace, waiting, "Active", "Resolved");

        var report = await workspace.Owner.Get<Report>($"/api/sprints/{sprint.Id}/report");

        Assert.Equal(1, report.Summary.AwaitingVerificationItems);
        Assert.Equal(0, report.Summary.CompletedItems);
        Assert.Equal(8, report.Summary.CommittedPoints);
    }

    /// <summary>
    /// The burndown has a point per elapsed day and never runs past today.
    /// </summary>
    /// <remarks>
    /// Projecting into the future would draw a flat tail that reads as "no progress" rather than
    /// "has not happened yet" — a chart that makes a healthy sprint look stalled.
    /// </remarks>
    [Fact]
    public async Task TheBurndownStopsAtToday()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await SprintAsync(workspace);

        await ItemInSprintAsync(workspace, sprint.Id, "work", 5);

        var report = await workspace.Owner.Get<Report>($"/api/sprints/{sprint.Id}/report");

        // The sprint runs a week, and one day has elapsed. The series is that one day — not the
        // seven it will eventually cover. Padding it would draw a flat tail that reads as "no
        // progress" rather than "has not happened yet".
        var today = Assert.Single(report.Burndown);

        Assert.Equal(DateTime.UtcNow.Date, today.Date.Date);

        // Nothing closed, so remaining is still what was committed.
        Assert.Equal(5, today.RemainingPoints);
        Assert.Equal(1, today.RemainingItems);

        // Day zero of the ideal line is the full commitment; it descends from here.
        Assert.Equal(5, today.IdealPoints);
    }

    /// <summary>Closing work moves the burndown down.</summary>
    [Fact]
    public async Task ClosingWorkBurnsTheLineDown()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await SprintAsync(workspace);

        var first = await ItemInSprintAsync(workspace, sprint.Id, "one", 5);
        await ItemInSprintAsync(workspace, sprint.Id, "two", 3);

        var before = await workspace.Owner.Get<Report>($"/api/sprints/{sprint.Id}/report");
        Assert.Equal(8, before.Burndown[^1].RemainingPoints);

        await MoveAsync(workspace, first, "Active", "Resolved", "Closed");

        var after = await workspace.Owner.Get<Report>($"/api/sprints/{sprint.Id}/report");
        Assert.Equal(3, after.Burndown[^1].RemainingPoints);
        Assert.Equal(1, after.Burndown[^1].RemainingItems);
    }

    /// <summary>
    /// Cycle time is measured from the history the workflow actually wrote.
    /// </summary>
    /// <remarks>
    /// The spans are near-zero because the test moves an item through in milliseconds; what is being
    /// asserted is that each stage was recognised and measured, not the durations themselves — those
    /// are covered exhaustively by <c>StateTimelineTests</c> where time can be controlled.
    /// </remarks>
    [Fact]
    public async Task CycleTimeIsMeasuredForClosedWorkOnly()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await SprintAsync(workspace);

        var closed = await ItemInSprintAsync(workspace, sprint.Id, "closed", 5);
        await ItemInSprintAsync(workspace, sprint.Id, "still open", 5);

        await MoveAsync(workspace, closed, "Active", "InReview", "Resolved", "Closed");

        var report = await workspace.Owner.Get<Report>($"/api/sprints/{sprint.Id}/report");

        // One of the two items closed; the open one is not measured, because "so far" is not a
        // cycle time.
        Assert.Equal(1, report.CycleTime.ItemsMeasured);

        Assert.NotNull(report.CycleTime.MedianPickupHours);
        Assert.NotNull(report.CycleTime.MedianDevelopmentHours);
        Assert.NotNull(report.CycleTime.MedianVerificationWaitHours);
        Assert.NotNull(report.CycleTime.MedianTotalHours);
    }

    // ── Velocity ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Velocity covers completed sprints only.
    /// </summary>
    /// <remarks>
    /// An in-flight sprint's completed points are a partial number. Charting it makes the newest bar
    /// look like a collapse to anybody who opens the page mid-sprint, which is when people look.
    /// </remarks>
    [Fact]
    public async Task VelocityIgnoresSprintsStillInFlight()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var active = await SprintAsync(workspace, "in flight");
        var item = await ItemInSprintAsync(workspace, active.Id, "done in an open sprint", 5);
        await MoveAsync(workspace, item, "Active", "Resolved", "Closed");

        var velocity = await workspace.Owner.Get<Velocity>(
            $"/api/projects/{workspace.ProjectId}/reports/velocity");

        Assert.Empty(velocity.Sprints);
        Assert.Null(velocity.AverageCompletedPoints);

        // Cycle time still counts it: the work really was finished, whatever the sprint's status.
        Assert.Equal(1, velocity.CycleTime.ItemsMeasured);
    }

    [Fact]
    public async Task VelocityReportsCompletedSprints()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await SprintAsync(workspace, "finished sprint");

        var done = await ItemInSprintAsync(workspace, sprint.Id, "delivered", 8);
        var missed = await ItemInSprintAsync(workspace, sprint.Id, "carried over", 5);

        await MoveAsync(workspace, done, "Active", "Resolved", "Closed");
        await MoveAsync(workspace, missed, "Active");

        await workspace.Owner.Patch<object>(
            $"/api/sprints/{sprint.Id}/status", new { status = "Active" });

        /*
         * Closed, not status-changed. PATCH /status no longer accepts Completed — completing a
         * sprint decides where unfinished work goes as well as flipping the status, and this sprint
         * has an item that did not land. Returning it to the backlog is what a real close does with
         * it, and the sprint keeps its record of having committed to it either way.
         */
        await workspace.Owner.Post<object>(
            $"/api/sprints/{sprint.Id}/close",
            new { incompleteItemsDestination = "ReturnToBacklog" });

        var velocity = await workspace.Owner.Get<Velocity>(
            $"/api/projects/{workspace.ProjectId}/reports/velocity");

        var point = Assert.Single(velocity.Sprints);
        Assert.Equal(13, point.CommittedPoints);
        Assert.Equal(8, point.CompletedPoints);
        Assert.Equal(8, velocity.AverageCompletedPoints);
    }

    [Fact]
    public async Task AProjectWithNoCompletedSprintsReportsNothingRatherThanFailing()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var velocity = await workspace.Owner.Get<Velocity>(
            $"/api/projects/{workspace.ProjectId}/reports/velocity");

        Assert.Empty(velocity.Sprints);
        Assert.Null(velocity.AverageCompletedPoints);
        Assert.Equal(0, velocity.CycleTime.ItemsMeasured);
    }

    // ── Access ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reports are gated on reading, not administering.
    /// </summary>
    /// <remarks>
    /// A burndown is something a team looks at together. Putting it behind project administration
    /// would make the people doing the work ask somebody else how it is going.
    /// </remarks>
    [Fact]
    public async Task AnyoneOnTheTeamCanSeeItsReports()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await SprintAsync(workspace);
        await ItemInSprintAsync(workspace, sprint.Id, "work", 5);

        var member = await workspace.AddOrganizationMemberAsync(factory);
        await workspace.Owner.Post($"/api/teams/{workspace.TeamId}/members",
            new { userId = member.UserId });

        var report = await member.Get<Report>($"/api/sprints/{sprint.Id}/report");
        Assert.Equal(5, report.Summary.CommittedPoints);

        var velocity = await member.Get<Velocity>(
            $"/api/teams/{workspace.TeamId}/reports/velocity");
        Assert.NotNull(velocity);
    }

    /// <summary>
    /// A project role sees the project's velocity and not the team's sprint report.
    /// </summary>
    /// <remarks>
    /// The consequence of sprints belonging to teams, asserted rather than left to be discovered.
    /// Somebody contributing to a project without being on the owning team can ask how fast the
    /// team building it moves — that is what the project velocity route answers — but a sprint
    /// spans projects they may not be able to see, so its report is not theirs to read.
    /// </remarks>
    [Fact]
    public async Task AProjectRoleSeesVelocityButNotTheTeamsSprintReport()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await SprintAsync(workspace);
        await ItemInSprintAsync(workspace, sprint.Id, "work", 5);

        var viewer = await workspace.AddOrganizationMemberAsync(factory);
        await workspace.Owner.Post($"/api/projects/{workspace.ProjectId}/roles",
            new { userId = viewer.UserId, role = "Viewer" });

        var velocity = await viewer.Get<Velocity>(
            $"/api/projects/{workspace.ProjectId}/reports/velocity");
        Assert.NotNull(velocity);

        // 404, not 403: they cannot see the sprint's team, and the status must not confirm that a
        // sprint with this id exists.
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetRaw($"/api/sprints/{sprint.Id}/report")).StatusCode);
    }

    /// <summary>Someone with no access to the project cannot read its numbers.</summary>
    [Fact]
    public async Task ReportsAreNotVisibleAcrossThePermissionBoundary()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await SprintAsync(workspace);

        var outsider = await workspace.AddOrganizationMemberAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound,
            (await outsider.GetRaw($"/api/sprints/{sprint.Id}/report")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await outsider.GetRaw($"/api/projects/{workspace.ProjectId}/reports/velocity")).StatusCode);
    }
}
