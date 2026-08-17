namespace BoardSync.Api.Shared.Kernel.Configuration;

/// <summary>
/// Tuning for the real-time hub.
/// </summary>
public class RealtimeSettings
{
    /// <summary>
    /// Whether the hub is mapped. Off means clients simply cannot connect and fall back to whatever
    /// polling they already do — the REST API is unaffected either way.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many missed messages a reconnecting client may be replayed before it is told to resync
    /// instead.
    /// </summary>
    /// <remarks>
    /// The bound exists so a client that slept for six hours cannot drag its entire backlog through
    /// the hub. Past it, one REST refetch is cheaper for both sides than thousands of deltas — and
    /// it is bounded work rather than work proportional to how long the client was away.
    /// </remarks>
    public int MaxReplayMessages { get; set; } = 200;

    /// <summary>
    /// How often live subscriptions are re-checked against current permissions.
    /// </summary>
    /// <remarks>
    /// This is the worst case for how long a revoked user can keep receiving a topic when the
    /// immediate path does not fire — Redis unavailable, or a permission changed directly in the
    /// database rather than through the API. The immediate path normally acts within milliseconds;
    /// this is the floor under it.
    /// </remarks>
    public int ReauthorizationIntervalSeconds { get; set; } = 60;
}
