namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// What closing a sprint does to the record of what it carried.
/// </summary>
/// <remarks>
/// The subtle half of moving unfinished work back to the backlog: the item has to leave the
/// backlog's sprint pointer so it reappears as unscheduled, and it has to stay in the sprint's
/// membership so the sprint still reports what it committed to. Get the second wrong and a sprint
/// that took on eight items and finished five reports five and five — a hundred percent, for every
/// team, for ever, and velocity stops describing anything.
/// </remarks>
[Collection(ApiCollection.Name)]
public class SprintCloseReportingTests(BoardSyncApiFactory factory)
{
    [Fact]
    public async Task AClosedSprintStillReportsWhatItCommittedTo()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var sprint = await workspace.Owner.Post<Created>(
            $"/api/teams/{workspace.TeamId}/sprints",
            new
            {
                goal = "carry some, leave some",
                // Sprints are created forward-looking — the API refuses a start date in the past.
                // Closing does not care about the dates, only that the sprint is Active.
                startDate = DateTime.UtcNow.AddDays(1),
                endDate = DateTime.UtcNow.AddDays(15),
            });

        await workspace.Owner.Patch<object>(
            $"/api/sprints/{sprint.Id}/status", new { status = "Active" });

        var finished = await workspace.AddWorkItemAsync("Finished work");
        var unfinished = await workspace.AddWorkItemAsync("Ran out of time");

        foreach (var id in new[] { finished, unfinished })
        {
            await workspace.Owner.Post($"/api/sprints/{sprint.Id}/workitems",
                new { workItemId = id });
        }

        // Only the first one is carried all the way through the QA gate.
        foreach (var state in new[] { "Active", "InReview", "Resolved", "Closed" })
        {
            await workspace.Owner.Patch<object>(
                $"/api/workitems/{finished}/state", new { state });
        }

        var result = await workspace.Owner.Post<CloseResult>(
            $"/api/sprints/{sprint.Id}/close",
            new { incompleteItemsDestination = "ReturnToBacklog" });

        Assert.Equal(1, result.CompletedItemCount);
        Assert.Equal(1, result.IncompleteItemCount);

        // The sprint's own record: two committed, one completed. Not one and one.
        var report = await workspace.Owner.Get<Report>(
            $"/api/sprints/{sprint.Id}/report");

        Assert.Equal(2, report.Summary.CommittedItems);
        Assert.Equal(1, report.Summary.CompletedItems);
    }

    [Fact]
    public async Task UnfinishedWorkComesBackToTheBacklog()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var sprint = await workspace.Owner.Post<Created>(
            $"/api/teams/{workspace.TeamId}/sprints",
            new
            {
                goal = "return what did not land",
                // Sprints are created forward-looking — the API refuses a start date in the past.
                // Closing does not care about the dates, only that the sprint is Active.
                startDate = DateTime.UtcNow.AddDays(1),
                endDate = DateTime.UtcNow.AddDays(15),
            });

        await workspace.Owner.Patch<object>(
            $"/api/sprints/{sprint.Id}/status", new { status = "Active" });

        var unfinished = await workspace.AddWorkItemAsync("Still to do");

        await workspace.Owner.Post($"/api/sprints/{sprint.Id}/workitems",
            new { workItemId = unfinished });

        // Committed to a sprint, so it has left the unscheduled backlog.
        var duringSprint = await workspace.Owner.Get<Paged<BacklogEntry>>(
            $"/api/projects/{workspace.ProjectId}/backlog?page=1&pageSize=50");

        Assert.DoesNotContain(duringSprint.Items, e => e.WorkItemId == unfinished);

        await workspace.Owner.Post<CloseResult>(
            $"/api/sprints/{sprint.Id}/close",
            new { incompleteItemsDestination = "ReturnToBacklog" });

        var afterClose = await workspace.Owner.Get<Paged<BacklogEntry>>(
            $"/api/projects/{workspace.ProjectId}/backlog?page=1&pageSize=50");

        Assert.Contains(afterClose.Items, e => e.WorkItemId == unfinished);
    }

    [Fact]
    public async Task JoiningASprintDoesNotMarkAnythingDone()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var sprint = await workspace.Owner.Post<Created>(
            $"/api/teams/{workspace.TeamId}/sprints",
            new
            {
                goal = "membership is not progress",
                startDate = DateTime.UtcNow.AddDays(1),
                endDate = DateTime.UtcNow.AddDays(15),
            });

        await workspace.Owner.Patch<object>(
            $"/api/sprints/{sprint.Id}/status", new { status = "Active" });

        var item = await workspace.AddWorkItemAsync("Just committed");

        await workspace.Owner.Post($"/api/sprints/{sprint.Id}/workitems",
            new { workItemId = item });

        /*
         * Sprint membership and work item state are orthogonal. Committing to a sprint says the
         * team intends to do the work; only the QA gate says it is done. A report that counted
         * membership as progress would show every sprint finishing the moment it was planned.
         */
        var report = await workspace.Owner.Get<Report>(
            $"/api/sprints/{sprint.Id}/report");

        Assert.Equal(1, report.Summary.CommittedItems);
        Assert.Equal(0, report.Summary.CompletedItems);
        Assert.Equal(0, report.Summary.AwaitingVerificationItems);
    }

    private sealed record Created(Guid Id);
    private sealed record CloseResult(int CompletedItemCount, int IncompleteItemCount);
    private sealed record Summary(int CommittedItems, int CompletedItems, int AwaitingVerificationItems);
    private sealed record Report(Summary Summary);
    private sealed record BacklogEntry(Guid BacklogItemId, Guid WorkItemId);
    private sealed record Paged<T>(List<T> Items, int TotalCount, int Page, int PageSize);
}
