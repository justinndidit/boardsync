namespace BoardSync.Api.Shared.Kernel.Configuration;

/// <summary>
/// Tuning for the background job worker.
/// </summary>
public class JobSettings
{
    /// <summary>
    /// Whether this instance runs queued work. On by default.
    /// </summary>
    /// <remarks>
    /// Turning it off is for running a dedicated worker process, not for saving effort — with every
    /// instance disabled, webhook deliveries and backfills accumulate and never run. The worker logs
    /// a warning at startup when it is off, for exactly that reason.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a claim is held before another worker may take the job.
    /// </summary>
    /// <remarks>
    /// The ceiling on how long a crashed worker's job stays stuck, and therefore also the floor on
    /// how long the longest job may run before a second worker starts it in parallel. Raise it above
    /// the slowest handler's realistic worst case; the cost of it being too high is only recovery
    /// latency after a crash.
    /// </remarks>
    public int LeaseSeconds { get; set; } = 300;

    /// <summary>How long to wait before looking again once the queue comes back empty.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Attempts before a job is marked dead. It stays in the table, queryable and re-drivable,
    /// rather than being deleted.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Base for the exponential retry backoff.</summary>
    public int BackoffSeconds { get; set; } = 10;

    /// <summary>
    /// Ceiling on the backoff, so a job failing on something that will fix itself — an expired
    /// token, a provider outage — still recovers within minutes rather than hours.
    /// </summary>
    public int MaxBackoffSeconds { get; set; } = 300;
}
