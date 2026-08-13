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
}
