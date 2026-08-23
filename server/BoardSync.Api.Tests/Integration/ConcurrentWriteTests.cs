using System.Net;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That two people editing one work item cannot silently overwrite each other, and that changing
/// one field does not require sending the other five.
/// </summary>
/// <remarks>
/// <para>
/// The race that matters is not load-to-save inside one request — EF already covers that — it is
/// <em>A reads, B saves, A saves</em>. Closing it needs the version the client actually read, which
/// is why <c>expectedVersion</c> exists on the request. It had never been applied: the value
/// travelled from the client, through the controller, into the service signature, and was dropped.
/// </para>
/// <para>
/// This is also the shape the git integration needs. A webhook worker moving state while somebody
/// edits the title is a routine event, not a rare race, and a full-replace PUT would have the worker
/// write back five stale fields to change one.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class ConcurrentWriteTests(BoardSyncApiFactory factory)
{
    private static async Task<WorkItem> Read(TestApi api, Guid id) =>
        await api.Get<WorkItem>($"/api/workitems/{id}");

    // ── Optimistic concurrency ────────────────────────────────────────────────

    /// <summary>
    /// A second writer working from a stale version is refused, not silently applied.
    /// </summary>
    /// <remarks>
    /// The exact sequence the guarantee is about: both read, one saves, the other saves against the
    /// version they read. Before this worked, the second write won and the first person's change
    /// vanished with no signal to anybody.
    /// </remarks>
    [Fact]
    public async Task AStaleWriteIsRejectedRatherThanWinning()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync("contested");

        var asBothReadIt = await Read(workspace.Owner, workItemId);

        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}",
            new { title = "first writer wins", expectedVersion = asBothReadIt.Version });

        var second = await workspace.Owner.PatchRaw($"/api/workitems/{workItemId}",
            new { title = "second writer, stale", expectedVersion = asBothReadIt.Version });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("changed by someone else", await TestApi.MessageOf(second));

        // And the first writer's change is still there.
        Assert.Equal("first writer wins", (await Read(workspace.Owner, workItemId)).Title);
    }

    /// <summary>Re-reading after a conflict yields a version that works.</summary>
    /// <remarks>
    /// The recovery the 409 message tells the client to perform, verified rather than assumed —
    /// a conflict that cannot be recovered from is just a broken endpoint.
    /// </remarks>
    [Fact]
    public async Task RereadingAfterAConflictLetsTheWriteThrough()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync("retry me");

        var stale = await Read(workspace.Owner, workItemId);
        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}", new { title = "moved on" });

        Assert.Equal(HttpStatusCode.Conflict,
            (await workspace.Owner.PatchRaw($"/api/workitems/{workItemId}",
                new { title = "stale attempt", expectedVersion = stale.Version })).StatusCode);

        var current = await Read(workspace.Owner, workItemId);

        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}",
            new { title = "rebased", expectedVersion = current.Version });

        Assert.Equal("rebased", (await Read(workspace.Owner, workItemId)).Title);
    }

    /// <summary>
    /// Omitting the version keeps the old last-write-wins behaviour.
    /// </summary>
    /// <remarks>
    /// Deliberate: requiring it would break every client that does not send one yet. The safe
    /// behaviour is available to anyone who opts in, and the frontend doc says to start sending it.
    /// </remarks>
    [Fact]
    public async Task OmittingTheVersionStillLastWriteWins()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync("unguarded");

        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}", new { title = "first" });
        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}", new { title = "second" });

        Assert.Equal("second", (await Read(workspace.Owner, workItemId)).Title);
    }

    /// <summary>The state endpoint honours the version too, not just the field endpoints.</summary>
    [Fact]
    public async Task StateTransitionsRespectTheVersion()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync("state race");

        var stale = await Read(workspace.Owner, workItemId);
        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}", new { title = "touched" });

        var conflicted = await workspace.Owner.PatchRaw($"/api/workitems/{workItemId}/state",
            new { state = "Active", expectedVersion = stale.Version });

        Assert.Equal(HttpStatusCode.Conflict, conflicted.StatusCode);
    }

    /// <summary>A version this server could never have issued is a 422, not a 500.</summary>
    [Fact]
    public async Task AnImpossibleVersionIsRejectedCleanly()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync("bad version");

        var response = await workspace.Owner.PatchRaw($"/api/workitems/{workItemId}",
            new { title = "nope", expectedVersion = 99999999999L });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ── Partial updates ───────────────────────────────────────────────────────

    /// <summary>
    /// A field the caller did not mention is left alone.
    /// </summary>
    /// <remarks>
    /// The whole point. Under the full-replace PUT, a client changing the title had to send
    /// description, priority, assignee, story points, team and tags back — and wrote whatever it had
    /// loaded, clobbering anything another editor had changed in between.
    /// </remarks>
    [Fact]
    public async Task OmittedFieldsAreUntouched()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync("keep my fields");

        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}",
            new { description = "carefully written", storyPoints = 5, priority = "High" });

        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}", new { title = "renamed only" });

        var item = await Read(workspace.Owner, workItemId);

        Assert.Equal("renamed only", item.Title);
        Assert.Equal("carefully written", item.Description);
        Assert.Equal(5, item.StoryPoints);
        Assert.Equal("High", item.Priority);
    }

    /// <summary>
    /// An explicit null clears the field, which is how an item is unassigned.
    /// </summary>
    /// <remarks>
    /// The distinction a nullable property cannot make, and the reason <c>Patch&lt;T&gt;</c> exists:
    /// without it, "unassign" and "do not mention the assignee" are the same request.
    /// </remarks>
    [Fact]
    public async Task AnExplicitNullClearsTheFieldWhileOmissionDoesNot()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync("assigned then not");

        Assert.NotNull((await Read(workspace.Owner, workItemId)).AssigneeId);

        // Omitted — still assigned.
        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}", new { title = "still mine" });
        Assert.NotNull((await Read(workspace.Owner, workItemId)).AssigneeId);

        // Explicitly null — unassigned.
        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}",
            new { assigneeId = (Guid?)null });
        Assert.Null((await Read(workspace.Owner, workItemId)).AssigneeId);
    }

    /// <summary>Tags are replaced only when mentioned, never emptied by omission.</summary>
    [Fact]
    public async Task TagsSurviveAPatchThatDoesNotMentionThem()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync("tagged");

        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}",
            new { tags = new[] { "payments", "urgent" } });

        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}", new { title = "renamed" });

        Assert.Equal(2, (await Read(workspace.Owner, workItemId)).Tags.Count);

        // Mentioned as empty — actually cleared.
        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}",
            new { tags = Array.Empty<string>() });

        Assert.Empty((await Read(workspace.Owner, workItemId)).Tags);
    }

    /// <summary>An empty patch is valid and changes nothing.</summary>
    [Fact]
    public async Task AnEmptyPatchIsANoOp()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync("untouched");

        var before = await Read(workspace.Owner, workItemId);
        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}", new { });
        var after = await Read(workspace.Owner, workItemId);

        Assert.Equal(before.Title, after.Title);
        Assert.Equal(before.AssigneeId, after.AssigneeId);
    }

    /// <summary>
    /// State cannot be moved through the field endpoint, so the QA gate has one door.
    /// </summary>
    /// <remarks>
    /// <c>state</c> is not on the patch request at all, so sending it is ignored rather than
    /// rejected — the assertion is that the item does not move, which is what matters. Allowing it
    /// here would be a second, unguarded route past the workflow and the certification check.
    /// </remarks>
    [Fact]
    public async Task PatchCannotMoveState()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync("state stays put");

        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}",
            new { title = "sneaky", state = "Closed" });

        Assert.Equal("New", (await Read(workspace.Owner, workItemId)).State);
    }

    /// <summary>Reassignment still has to respect team membership.</summary>
    [Fact]
    public async Task PatchStillEnforcesTeamMembershipOnReassignment()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync("wrong assignee");
        var outsider = await workspace.AddOrganizationMemberAsync(factory);

        var response = await workspace.Owner.PatchRaw($"/api/workitems/{workItemId}",
            new { assigneeId = outsider.UserId });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("not a member", await TestApi.MessageOf(response));
    }

    /// <summary>A blank title is refused rather than stored.</summary>
    /// <remarks>
    /// <c>[Required]</c> cannot see through <c>Patch&lt;T&gt;</c>, so this is checked by hand — which
    /// makes it exactly the kind of rule that needs a test rather than a trusted attribute.
    /// </remarks>
    [Fact]
    public async Task ABlankTitleIsRefused()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync("has a title");

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await workspace.Owner.PatchRaw($"/api/workitems/{workItemId}", new { title = "   " })).StatusCode);
    }

    private sealed record WorkItem(
        Guid Id, string Title, string? Description, string State, string Priority,
        Guid? AssigneeId, int? StoryPoints, List<string> Tags, long Version);
}
