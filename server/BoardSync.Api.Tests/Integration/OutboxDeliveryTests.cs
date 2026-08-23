using System.Diagnostics;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That a domain event reaches its handlers promptly, and that history is written completely.
/// </summary>
/// <remarks>
/// Both of the defects pinned here were invisible to every unit test and to reading the code, and
/// both were found by watching the running system do the wrong thing. They are the argument for this
/// test project existing.
/// </remarks>
[Collection(ApiCollection.Name)]
public class OutboxDeliveryTests(BoardSyncApiFactory factory)
{
    /// <summary>
    /// A change reaches the activity feed in well under a second.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect this pins: the outbox trigger fired <c>pg_notify</c>, the dispatcher held a
    /// <c>LISTEN</c> connection open, and the handler wrote a trace line and woke nothing. The
    /// dispatch loop's wait was an unconditional <c>Task.Delay(PollIntervalSeconds)</c>, so delivery
    /// latency was uniformly 0–5s rather than the milliseconds documented on the class, on
    /// <c>OutboxSettings</c> and in the README. Everything downstream inherited it.
    /// </para>
    /// <para>
    /// The fixture sets <c>Outbox:PollIntervalSeconds</c> to 30 precisely so this measures the NOTIFY
    /// path. If the wake-up regresses, the polling fallback cannot rescue it inside the timeout and
    /// this fails — which is the point. A shorter interval would let a broken listener still look
    /// fine.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DomainEventsReachTheActivityFeedWithoutWaitingForAPoll()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var title = $"latency-{Guid.NewGuid():N}"[..24];
        var started = Stopwatch.StartNew();

        await workspace.AddWorkItemAsync(title);

        var appeared = await Poll(async () =>
        {
            var feed = await workspace.Owner.Get<Paged<Activity>>(
                $"/api/orgs/{workspace.OrganizationId}/activity?page=1&pageSize=50");

            return feed.Items.Any(entry => entry.Title.Contains(title));
        }, within: TimeSpan.FromSeconds(10));

        started.Stop();

        Assert.True(appeared,
            "The work item never reached the activity feed. The outbox dispatcher is not delivering.");

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5),
            $"Delivery took {started.Elapsed.TotalSeconds:F1}s with a 30s poll interval, which means " +
            "the NOTIFY wake-up is not working and the poll fallback is doing the delivery.");
    }

    /// <summary>
    /// Creating, transitioning and commenting on a work item each reach the activity feed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect this pins, and the reason it needs more than one verb: every
    /// <c>_eventBus.Enqueue</c> in <c>WorkItemService</c> sat <em>after</em> its
    /// <c>SaveChangesAsync</c>. The bus stages an outbox row on the request's DbContext and does no
    /// I/O of its own, so enqueueing after the save left the row in the change tracker with nothing
    /// left to persist it — discarded on scope disposal, with no exception and no log. Not one work
    /// item event had ever been delivered: no work item activity, no live board updates, no board
    /// cache invalidation.
    /// </para>
    /// <para>
    /// Six call sites were wrong independently, so asserting one verb would let the next five
    /// regress unnoticed. These three cover distinct methods; the delete and link paths are the same
    /// shape.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EveryKindOfWorkItemChangeReachesTheActivityFeed()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var title = $"verbs-{Guid.NewGuid():N}"[..20];
        var workItemId = await workspace.AddWorkItemAsync(title);

        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}/state", new { state = "Active" });
        await workspace.Owner.Post($"/api/workitems/{workItemId}/comments", new { body = "looking at this" });

        var verbs = await Poll<HashSet<string>>(
            async () =>
            {
                var feed = await workspace.Owner.Get<Paged<Activity>>(
                    $"/api/orgs/{workspace.OrganizationId}/activity?page=1&pageSize=100");

                // Matched on the title rather than EntityId: a comment's activity row carries the
                // comment's id there so a client can deep-link to it, while its Title is the work
                // item's. Title is the field all three verbs agree on.
                return feed.Items
                    .Where(entry => entry.Title.Contains(title))
                    .Select(entry => entry.Verb)
                    .ToHashSet();
            },
            until: found => found.Count >= 3,
            within: TimeSpan.FromSeconds(10));

        Assert.True(verbs.Count >= 3,
            $"Only {verbs.Count} of the three work item changes reached the feed ({string.Join(", ", verbs)}). " +
            "An Enqueue has probably moved back after its SaveChangesAsync.");
    }

    /// <summary>
    /// Every history row carries the project its work item belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect this pins: <c>WorkItemHistory.ProjectId</c> is documented on the model as copied
    /// from the work item at write time, <c>(ProjectId, CreatedAt)</c> is indexed for it, and a
    /// migration shipped the column — but <c>AddHistory</c> only ever received a work item id, so
    /// every row ever written carried <c>Guid.Empty</c>. The notification feed filters on exactly
    /// that column, so it returned nothing to anybody, including users who could see everything.
    /// </para>
    /// <para>
    /// Asserted through the bell rather than by reading the column, because the bell is what the
    /// unset value actually broke, and a test that reached into the table would have kept passing
    /// while the feature stayed dead.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WorkItemHistoryIsFiledUnderItsProjectSoTheBellCanFindIt()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var workItemId = await workspace.AddWorkItemAsync($"bell-{Guid.NewGuid():N}"[..20]);

        // A second history row, from a transition rather than creation, so both write paths are
        // covered — they were separate calls and could have been fixed separately.
        await workspace.Owner.Patch<object>($"/api/workitems/{workItemId}/state", new { state = "Active" });

        var notifications = await Poll<List<Notification>>(
            async () => await workspace.Owner.Get<List<Notification>>("/api/notifications"),
            until: n => n.Count >= 2,
            within: TimeSpan.FromSeconds(10));

        Assert.True(notifications.Count >= 2,
            $"Expected the creation and the transition to both appear; got {notifications.Count}. " +
            "WorkItemHistory.ProjectId is probably unset again.");

        Assert.Contains(notifications, n => n.Type == "WorkItemActive");
    }

    // ── Polling helpers ───────────────────────────────────────────────────────
    //
    // The activity feed and the bell are eventually consistent by design — the outbox delivers after
    // the originating transaction commits — so asserting immediately after a write would be a race.
    // These poll to a deadline rather than sleeping a fixed amount, which keeps a passing run fast
    // and a failing one honest about how long it actually waited.

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

    private static async Task<T> Poll<T>(Func<Task<T>> read, Func<T, bool> until, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        var latest = await read();

        while (DateTime.UtcNow < deadline && !until(latest))
        {
            await Task.Delay(50);
            latest = await read();
        }

        return latest;
    }

    private sealed record Paged<T>(List<T> Items, int TotalCount, int Page, int PageSize);
    private sealed record Activity(Guid Id, string Title, string Verb, Guid EntityId, string? Detail);
    private sealed record Notification(Guid Id, string Type, string Title);
}
