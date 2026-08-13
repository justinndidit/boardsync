namespace BoardSync.Api.Shared.Kernel.Configuration;

/// <summary>
/// Tuning for the outbox dispatcher.
/// </summary>
public class OutboxSettings
{
    /// <summary>
    /// Whether this instance drains the outbox. On by default.
    /// </summary>
    /// <remarks>
    /// Turning it off is for running a dedicated dispatcher process, not for saving work — with
    /// every instance disabled, events accumulate and the activity feed silently stops updating.
    /// The dispatcher logs a warning at startup when it is off, for exactly that reason.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>Messages claimed per pass. Larger batches amortise the transaction, but hold locks longer.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Fallback poll interval. Normal latency comes from Postgres NOTIFY; this is the safety net for
    /// when the listener connection has dropped, so it trades a little idle querying for never
    /// stalling the queue.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Delivery attempts before a message is left alone. It stays in the table, undispatched and
    /// visible, rather than being deleted or retried forever.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// How long delivered messages are kept before cleanup. They are the replay log a real-time
    /// client resumes from, so this is the practical ceiling on how far behind a client can fall
    /// and still catch up without a full refetch.
    /// </summary>
    public int RetentionHours { get; set; } = 48;
}
