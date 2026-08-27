using System.Net;
using System.Text.Json;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Tests.Integration;

[Collection(ApiCollection.Name)]
public sealed class WorkItemMovementTests(BoardSyncApiFactory factory)
{
    [Fact]
    public async Task CombinedMovementReturnsStateRankAndNewVersion()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await workspace.Owner.Post<Sprint>(
            $"/api/projects/{workspace.ProjectId}/sprints",
            new { goal = "movement", startDate = DateTime.UtcNow.Date, endDate = DateTime.UtcNow.Date.AddDays(7) });
        var first = await workspace.AddWorkItemAsync("first");
        var moved = await workspace.AddWorkItemAsync("moved");

        await workspace.Owner.Post($"/api/sprints/{sprint.Id}/workitems", new { workItemId = first });
        await workspace.Owner.Post($"/api/sprints/{sprint.Id}/workitems", new { workItemId = moved });
        var current = await workspace.Owner.Get<WorkItem>($"/api/workitems/{moved}");

        var result = await workspace.Owner.Patch<Movement>(
            $"/api/sprints/{sprint.Id}/workitems/{moved}/move-with-state",
            new { state = "Active", expectedVersion = current.Version, beforeWorkItemId = first });

        Assert.Equal(moved, result.WorkItemId);
        Assert.Equal("Active", result.State);
        Assert.Equal(0, result.Rank);
        Assert.NotEqual(current.Version, result.Version);
    }

    [Fact]
    public async Task CombinedMovementRejectsStaleVersion()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await workspace.Owner.Post<Sprint>(
            $"/api/projects/{workspace.ProjectId}/sprints",
            new { goal = "movement", startDate = DateTime.UtcNow.Date, endDate = DateTime.UtcNow.Date.AddDays(7) });
        var item = await workspace.AddWorkItemAsync("stale movement");
        await workspace.Owner.Post($"/api/sprints/{sprint.Id}/workitems", new { workItemId = item });
        var current = await workspace.Owner.Get<WorkItem>($"/api/workitems/{item}");

        await workspace.Owner.Patch<object>($"/api/workitems/{item}", new { title = "changed" });
        var response = await workspace.Owner.PatchRaw(
            $"/api/sprints/{sprint.Id}/workitems/{item}/move-with-state",
            new { state = "Active", expectedVersion = current.Version });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("New", (await workspace.Owner.Get<WorkItem>($"/api/workitems/{item}")).State);
    }

    [Fact]
    public async Task InvalidNeighbourDoesNotCommitState()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await workspace.Owner.Post<Sprint>(
            $"/api/projects/{workspace.ProjectId}/sprints",
            new { goal = "movement", startDate = DateTime.UtcNow.Date, endDate = DateTime.UtcNow.Date.AddDays(7) });
        var item = await workspace.AddWorkItemAsync("invalid placement");
        await workspace.Owner.Post($"/api/sprints/{sprint.Id}/workitems", new { workItemId = item });
        var current = await workspace.Owner.Get<WorkItem>($"/api/workitems/{item}");

        var response = await workspace.Owner.PatchRaw(
            $"/api/sprints/{sprint.Id}/workitems/{item}/move-with-state",
            new { state = "Active", expectedVersion = current.Version, beforeWorkItemId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var unchanged = await workspace.Owner.Get<WorkItem>($"/api/workitems/{item}");
        Assert.Equal("New", unchanged.State);
        Assert.Equal(current.Version, unchanged.Version);
    }

    [Fact]
    public async Task BothNeighboursNullIsRejectedWhenSprintHasOtherItems()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await CreateSprint(workspace);
        var first = await AddToSprint(workspace, sprint.Id, "first");
        var moved = await AddToSprint(workspace, sprint.Id, "moved");
        var current = await workspace.Owner.Get<WorkItem>($"/api/workitems/{moved}");

        var response = await workspace.Owner.PatchRaw(
            $"/api/sprints/{sprint.Id}/workitems/{moved}/move-with-state",
            new { state = "Active", expectedVersion = current.Version });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("New", (await workspace.Owner.Get<WorkItem>($"/api/workitems/{moved}")).State);
    }

    [Fact]
    public async Task BothNeighboursNullIsAllowedForOnlySprintItem()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await CreateSprint(workspace);
        var item = await AddToSprint(workspace, sprint.Id, "only");
        var current = await workspace.Owner.Get<WorkItem>($"/api/workitems/{item}");

        var result = await workspace.Owner.Patch<Movement>(
            $"/api/sprints/{sprint.Id}/workitems/{item}/move-with-state",
            new { state = "Active", expectedVersion = current.Version });

        Assert.Equal(1024, result.Rank);
    }

    [Fact]
    public async Task ConcurrentMovesIntoSameGapCannotBothPersistSameRank()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await CreateSprint(workspace);
        var first = await AddToSprint(workspace, sprint.Id, "first");
        var second = await AddToSprint(workspace, sprint.Id, "second");
        var third = await AddToSprint(workspace, sprint.Id, "third");
        var fourth = await AddToSprint(workspace, sprint.Id, "fourth");
        var secondVersion = (await workspace.Owner.Get<WorkItem>($"/api/workitems/{second}")).Version;
        var thirdVersion = (await workspace.Owner.Get<WorkItem>($"/api/workitems/{third}")).Version;

        var responses = await Task.WhenAll(
            workspace.Owner.PatchRaw($"/api/sprints/{sprint.Id}/workitems/{second}/move-with-state",
                new { state = "Active", expectedVersion = secondVersion, afterWorkItemId = first, beforeWorkItemId = fourth }),
            workspace.Owner.PatchRaw($"/api/sprints/{sprint.Id}/workitems/{third}/move-with-state",
                new { state = "Active", expectedVersion = thirdVersion, afterWorkItemId = first, beforeWorkItemId = fourth }));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var ranks = await Task.WhenAll(responses.Select(async response =>
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("data").GetProperty("rank").GetDecimal();
        }));
        Assert.Equal(2, ranks.Distinct().Count());
    }

    [Fact]
    public async Task WholeListReorderPersistsPositionsAndRanks()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var sprint = await CreateSprint(workspace);
        var first = await AddToSprint(workspace, sprint.Id, "first");
        var second = await AddToSprint(workspace, sprint.Id, "second");

        await workspace.Owner.Patch<object>($"/api/sprints/{sprint.Id}/workitems/reorder",
            new { workItemIds = new[] { second, first } });

        var result = await workspace.Owner.Get<PagedResult<SprintItem>>(
            $"/api/sprints/{sprint.Id}/workitems?pageSize=20");
        Assert.Equal(new[] { second, first }, result.Items.Select(item => item.WorkItemId));
        Assert.Equal(new[] { 0, 1 }, result.Items.Select(item => item.Position));
    }

    private static async Task<Sprint> CreateSprint(Workspace workspace) =>
        await workspace.Owner.Post<Sprint>($"/api/projects/{workspace.ProjectId}/sprints",
            new { goal = "movement", startDate = DateTime.UtcNow.Date, endDate = DateTime.UtcNow.Date.AddDays(7) });

    private static async Task<Guid> AddToSprint(Workspace workspace, Guid sprintId, string title)
    {
        var item = await workspace.AddWorkItemAsync(title);
        await workspace.Owner.Post($"/api/sprints/{sprintId}/workitems", new { workItemId = item });
        return item;
    }

    private sealed record Sprint(Guid Id, int Number, string Status);
    private sealed record WorkItem(Guid Id, string State, long Version);
    private sealed record Movement(Guid WorkItemId, string State, decimal Rank, long Version);
    private sealed record SprintItem(Guid WorkItemId, string Title, string Type, string State,
        string Priority, Guid? AssigneeId, int? StoryPoints, int Position);
}