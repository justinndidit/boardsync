namespace BoardSync.Api.Shared.Kernel.Jobs;

/// <summary>
/// A unit of long-running work, durably queued.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not the outbox, and not RabbitMQ.</b> The outbox carries domain events: tiny,
/// latency-sensitive, and expected to complete in milliseconds. Processing a webhook, backfilling
/// ninety days of commits, or decomposing a PRD is a different animal — minutes rather than
/// milliseconds, expensive to redo, and wanting a concurrency limit of its own. Running those
/// through the outbox would let one backfill starve the activity feed queued behind it.
/// </para>
/// <para>
/// A broker is the usual answer and is not warranted yet: the outbox already provides atomic
/// enqueue, at-least-once delivery, ordering, multi-instance draining and retry, and RabbitMQ would
/// sit <em>downstream</em> of it rather than replacing it — a second delivery guarantee to reason
/// about, plus a stateful service to run, for a system with one deployable and no external
/// consumer. So this reuses the mechanism that already works (<c>FOR UPDATE SKIP LOCKED</c>) and
/// adds only what long work actually needs: a visibility timestamp for backoff, a lease so a
/// crashed worker's job is reclaimed, and a per-type concurrency cap. See build_context.md §9 for
/// the triggers that would change the answer.
/// </para>
/// </remarks>
public class Job
{
    /// <summary>Database-generated, monotonically increasing. Ties are broken by it, so FIFO within a priority.</summary>
    public long Sequence { get; set; }

    /// <summary>
    /// The caller's own idempotency key. Unique, so enqueueing the same work twice is a no-op
    /// rather than a duplicate — which is what lets a webhook redelivery be safe to accept.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// What kind of work this is — the handler lookup key, and what the concurrency cap applies to.
    /// </summary>
    /// <remarks>
    /// A short name rather than an assembly-qualified type, for the same reason
    /// <see cref="Events.OutboxMessage.EventType"/> is: a namespace reorganisation must not strand
    /// rows already in the queue.
    /// </remarks>
    public string JobType { get; set; } = string.Empty;

    /// <summary>The serialized payload, as JSON.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// Lower runs first. Interactive work outranks bulk work, so a user waiting on a PRD
    /// decomposition is not queued behind a ninety-day backfill.
    /// </summary>
    public int Priority { get; set; } = JobPriority.Normal;

    /// <summary>
    /// The earliest this may be claimed. Set forward on failure to back off, so a job that is
    /// failing does not spin the worker.
    /// </summary>
    public DateTime VisibleAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the current lease expires, or null when unclaimed.
    /// </summary>
    /// <remarks>
    /// The difference between this and the outbox. An outbox message is delivered inside the
    /// claiming transaction, so a crash rolls the claim back. A job runs for minutes, far longer
    /// than a transaction should be held open, so it is claimed in one short transaction and worked
    /// outside it. The lease is what makes that safe: a worker that dies mid-job leaves a lease that
    /// expires, and another worker picks the job up. It is the reason handlers must be idempotent.
    /// </remarks>
    public DateTime? LeaseExpiresAt { get; set; }

    /// <summary>Which worker holds the lease. Diagnostic only — the expiry is what grants the claim.</summary>
    public string? LeasedBy { get; set; }

    /// <summary>How many times this has been attempted.</summary>
    public int Attempts { get; set; }

    /// <summary>When it finished successfully. Null while outstanding.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// When it stopped being retried. Null unless it exhausted its attempts.
    /// </summary>
    /// <remarks>
    /// The row stays, like an exhausted outbox message: a dead job you can query and re-drive beats
    /// one that vanished into a queue nobody has a dashboard for.
    /// </remarks>
    public DateTime? DeadAt { get; set; }

    /// <summary>Last failure, kept for diagnosis.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Conventional priorities. Any int works; these are the ones worth naming.</summary>
public static class JobPriority
{
    /// <summary>Somebody is waiting on the result.</summary>
    public const int Interactive = 10;

    /// <summary>The default — reactive work nobody is watching, like a webhook.</summary>
    public const int Normal = 50;

    /// <summary>Bulk work that must never delay anything else, like a repository backfill.</summary>
    public const int Bulk = 90;
}
