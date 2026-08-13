using System.Security.Claims;
using BoardSync.Api.Shared.Kernel.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BoardSync.Api.Shared.Realtime;

/// <summary>
/// The real-time channel. Clients subscribe to topics and receive changes as they happen.
/// </summary>
/// <remarks>
/// <para>
/// Authorization happens per <see cref="SubscribeAsync"/>, not once at connect. A connection proves
/// who you are; a subscription proves what you may watch, and those are different questions with
/// different answers that change at different times.
/// </para>
/// <para>
/// Everything a client receives arrives as <c>Message</c> with a monotonically increasing
/// <c>sequence</c>. That sequence is the client's resume point — see <see cref="SubscribeAsync"/>.
/// </para>
/// </remarks>
[Authorize]
public class WorkspaceHub : Hub
{
    private readonly ITopicAuthorizer _authorizer;
    private readonly IRealtimeReplay _replay;
    private readonly IPresenceTracker? _presence;
    private readonly ILogger<WorkspaceHub> _logger;

    /// <summary>
    /// Topics this connection has joined, so a disconnect can clean up its presence.
    /// </summary>
    /// <remarks>
    /// Held per connection rather than globally: SignalR gives no way to enumerate a connection's
    /// groups, and presence that only clears on a polite unsubscribe would leak every closed tab.
    /// </remarks>
    private HashSet<string> JoinedTopics =>
        (HashSet<string>)(Context.Items.TryGetValue(nameof(JoinedTopics), out var existing)
            ? existing!
            : Context.Items[nameof(JoinedTopics)] = new HashSet<string>());

    public WorkspaceHub(
        ITopicAuthorizer authorizer,
        IRealtimeReplay replay,
        ILogger<WorkspaceHub> logger,
        IPresenceTracker? presence = null)
    {
        _authorizer = authorizer;
        _replay = replay;
        _presence = presence;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        // Every connection joins its own user topic without asking — it is theirs by definition,
        // and notifications should not require the client to remember to subscribe.
        if (TryGetUserId(out var userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, Topic.User(userId));

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Joins a topic and, when a resume point is supplied, catches the caller up on what they
    /// missed.
    /// </summary>
    /// <param name="topic">A topic string, e.g. <c>project:{guid}</c>.</param>
    /// <param name="lastSequence">
    /// The sequence of the last message this client processed on this topic, or null for a fresh
    /// subscription.
    /// </param>
    /// <returns>
    /// The outcome — whether the subscription was accepted, the current sequence to resume from
    /// next time, and whether the client must refetch instead of relying on replay.
    /// </returns>
    /// <remarks>
    /// The failure mode this guards against is not the disconnect — it is the client that
    /// reconnects, misses the messages sent while it was away, and carries on looking correct while
    /// showing stale data. Replay closes that gap; <c>resync</c> is the honest answer when the gap
    /// is too wide to close cheaply.
    /// </remarks>
    public async Task<SubscribeResult> SubscribeAsync(string topic, long? lastSequence = null)
    {
        if (!TryGetUserId(out var userId))
            return SubscribeResult.Denied("Not authenticated.");

        if (!await _authorizer.CanSubscribeAsync(userId, topic, Context.ConnectionAborted))
        {
            _logger.LogInformation("User {UserId} denied subscription to {Topic}", userId, topic);

            // Deliberately the same answer for "no such topic" and "not allowed". Distinguishing
            // them would turn the hub into a way to discover which projects exist.
            return SubscribeResult.Denied("Not permitted to subscribe to this topic.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, topic, Context.ConnectionAborted);

        JoinedTopics.Add(topic);
        await AnnounceJoinAsync(topic, userId);

        var currentSequence = await _replay.GetCurrentSequenceAsync(Context.ConnectionAborted);

        if (lastSequence is null)
        {
            // A fresh subscriber has no gap to close. They fetch their own snapshot over REST and
            // start applying deltas from here.
            return SubscribeResult.Accept(currentSequence, resync: false);
        }

        var missed = await _replay.GetMissedAsync(topic, lastSequence.Value, Context.ConnectionAborted);

        if (missed is null)
        {
            // Past the replay bound. Telling the client to refetch is cheaper for both sides than
            // streaming an unbounded backlog, and it is the only honest answer once the outbox has
            // aged past its retention.
            _logger.LogDebug("Resync required for {Topic} from sequence {LastSequence}", topic, lastSequence);
            return SubscribeResult.Accept(currentSequence, resync: true);
        }

        foreach (var message in missed)
            await Clients.Caller.SendAsync("Message", message, Context.ConnectionAborted);

        return SubscribeResult.Accept(currentSequence, resync: false);
    }

    /// <summary>Leaves a topic. Unsubscribing from something you were never on is not an error.</summary>
    public async Task UnsubscribeAsync(string topic)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, topic, Context.ConnectionAborted);

        JoinedTopics.Remove(topic);
        await AnnounceLeaveAsync(topic);
    }

    /// <summary>Who else is watching this topic right now.</summary>
    /// <remarks>
    /// Authorized like a subscription — being able to see who is on a board is being able to see
    /// that the board exists.
    /// </remarks>
    public async Task<IReadOnlyList<Guid>> GetPresenceAsync(string topic)
    {
        if (_presence is null || !TryGetUserId(out var userId)) return [];

        if (!await _authorizer.CanSubscribeAsync(userId, topic, Context.ConnectionAborted))
            return [];

        return await _presence.GetPresentAsync(topic);
    }

    /// <summary>
    /// Keeps this connection counted as present. Call every ~30 seconds while a view is open.
    /// </summary>
    /// <remarks>
    /// Presence entries expire on their own, so a client that stops heartbeating fades out rather
    /// than lingering. That is the point — a crashed tab cannot send a goodbye.
    /// </remarks>
    public async Task HeartbeatAsync()
    {
        if (_presence is null || !TryGetUserId(out var userId)) return;

        foreach (var topic in JoinedTopics.ToList())
            await _presence.JoinAsync(topic, userId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Best effort: if this does not run, the entries age out of the sorted set anyway.
        foreach (var topic in JoinedTopics.ToList())
            await AnnounceLeaveAsync(topic);

        await base.OnDisconnectedAsync(exception);
    }

    private async Task AnnounceJoinAsync(string topic, Guid userId)
    {
        if (_presence is null) return;

        // Only a genuinely new arrival is broadcast — a refresh is not news.
        if (await _presence.JoinAsync(topic, userId))
            await NotifyPresenceChangedAsync(topic);
    }

    private async Task AnnounceLeaveAsync(string topic)
    {
        if (_presence is null || !TryGetUserId(out var userId)) return;

        // A user may have this topic open in two tabs; only announce when the last one goes.
        if (await _presence.LeaveAsync(topic, userId))
            await NotifyPresenceChangedAsync(topic);
    }

    private async Task NotifyPresenceChangedAsync(string topic)
    {
        if (_presence is null) return;

        var present = await _presence.GetPresentAsync(topic);
        await Clients.Group(topic).SendAsync("PresenceChanged", new { topic, userIds = present });
    }

    private bool TryGetUserId(out Guid userId)
    {
        var claim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out userId);
    }
}

/// <summary>The outcome of a subscribe attempt.</summary>
/// <param name="Subscribed">Whether the client is now receiving this topic.</param>
/// <param name="CurrentSequence">
/// The newest sequence at subscribe time. Persist it and send it back as <c>lastSequence</c> on the
/// next reconnect.
/// </param>
/// <param name="Resync">
/// True when the gap was too large to replay. The client must refetch the relevant state over REST;
/// the deltas that follow are still valid from <paramref name="CurrentSequence"/> onward.
/// </param>
/// <param name="Reason">Why a denied subscription was denied. Null on success.</param>
public record SubscribeResult(bool Subscribed, long CurrentSequence, bool Resync, string? Reason)
{
    public static SubscribeResult Denied(string reason) => new(false, 0, false, reason);

    public static SubscribeResult Accept(long currentSequence, bool resync) =>
        new(true, currentSequence, resync, null);
}
