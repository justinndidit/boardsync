using System.Net;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// Putting a work item under another one, and taking it back out.
/// </summary>
/// <remarks>
/// <para>
/// A parent could be chosen when an item was created and never afterwards. Fixing a wrong one meant
/// deleting the item and making another, which takes a new number — so every branch named after the
/// old reference stops binding — and leaves its history behind.
/// </para>
/// <para>
/// The refusals matter more than the move. A ring in the tree is the one that does real damage:
/// nothing that walks parents can leave it, and the first query to try will hang rather than report
/// something a reader could act on.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class WorkItemHierarchyTests(BoardSyncApiFactory factory)
{
    /// <summary>A PUT body carrying everything the endpoint requires, plus a parent.</summary>
    private static object Update(string title, Guid? parentId) =>
        new { title, parentId, priority = "Medium", tags = Array.Empty<string>() };

    [Fact]
    public async Task AParentCanBeSetAfterCreation()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var feature = await workspace.AddWorkItemAsync("Multi-currency", "Feature");
        var story = await workspace.AddWorkItemAsync("Charge in local currency", "UserStory");

        await workspace.Owner.Put($"/api/workitems/{story}",
            Update("Charge in local currency", feature));

        var updated = await workspace.Owner.Get<Item>($"/api/workitems/{story}");

        Assert.Equal(feature, updated.ParentId);
    }

    [Fact]
    public async Task AParentCanBeRemoved()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var feature = await workspace.AddWorkItemAsync("Billing", "Feature");
        var story = await workspace.AddWorkItemAsync("Download an invoice", "UserStory");

        await workspace.Owner.Put($"/api/workitems/{story}",
            Update("Download an invoice", feature));

        // A PUT that says nothing about the parent clears it, the same way it already clears an
        // assignee or a team. Documented on the request, and pinned here because a client that
        // edits a title without echoing the parent will orphan the item.
        await workspace.Owner.Put($"/api/workitems/{story}",
            Update("Download an invoice", null));

        var updated = await workspace.Owner.Get<Item>($"/api/workitems/{story}");

        Assert.Null(updated.ParentId);
    }

    [Fact]
    public async Task TheHierarchyRuleStillApplies()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var task = await workspace.AddWorkItemAsync("Write the migration", "Task");
        var epic = await workspace.AddWorkItemAsync("Payments", "Epic");

        // An Epic under a Task inverts the workflow. Creation has always refused it; so does this.
        var response = await workspace.Owner.PutRaw($"/api/workitems/{epic}",
            Update("Payments", task));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AnItemCannotBeItsOwnParent()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var story = await workspace.AddWorkItemAsync("Refunds", "UserStory");

        var response = await workspace.Owner.PutRaw($"/api/workitems/{story}",
            Update("Refunds", story));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        Assert.Contains("its own parent",
            await TestApi.MessageOf(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AParentCannotBeOneOfItsOwnDescendants()
    {
        var workspace = await Workspace.CreateAsync(factory);

        // Epic → Feature → Story, built downwards, then an attempt to close it into a ring.
        var epic = await workspace.AddWorkItemAsync("Subscriptions", "Epic");
        var feature = await workspace.AddWorkItemAsync("Renewals", "Feature");

        await workspace.Owner.Put($"/api/workitems/{feature}", Update("Renewals", epic));

        /*
         * The ring. The feature is already under the epic, so putting the epic under the feature
         * would close the loop — and nothing that walks parents could ever leave it again.
         *
         * Worth noting this is refused by the descendant check and *not* by the hierarchy rule:
         * Epic-under-Feature is already illegal on type alone, so the ring is proven with the pair
         * where both refusals would fire, and the message says which one did.
         */
        var response = await workspace.Owner.PutRaw($"/api/workitems/{epic}",
            Update("Subscriptions", feature));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AParentFromAnotherProjectIsNotFound()
    {
        var one = await Workspace.CreateAsync(factory);
        var two = await Workspace.CreateAsync(factory);

        var theirFeature = await two.AddWorkItemAsync("Somebody else's feature", "Feature");
        var ourStory = await one.AddWorkItemAsync("Our story", "UserStory");

        var response = await one.Owner.PutRaw($"/api/workitems/{ourStory}",
            Update("Our story", theirFeature));

        // 404 rather than 422: a work item in a project you cannot see reads as absent, so the
        // response does not confirm that somebody else's id is real.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReparentingIsRecordedInHistory()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var feature = await workspace.AddWorkItemAsync("Reporting", "Feature");
        var story = await workspace.AddWorkItemAsync("Velocity chart", "UserStory");

        await workspace.Owner.Put($"/api/workitems/{story}", Update("Velocity chart", feature));

        var history = await workspace.Owner.Get<Paged<HistoryEntry>>(
            $"/api/workitems/{story}/history?page=1&pageSize=50");

        Assert.Contains(history.Items, entry =>
            entry.FieldName == "ParentId" && entry.NewValue == feature.ToString());
    }

    private sealed record Item(Guid Id, Guid? ParentId, string Title);
    private sealed record HistoryEntry(Guid Id, string FieldName, string? OldValue, string? NewValue);
    private sealed record Paged<T>(List<T> Items, int TotalCount, int Page, int PageSize);
}
