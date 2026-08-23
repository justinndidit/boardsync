namespace BoardSync.Api.Shared.Kernel.Jobs;

/// <summary>
/// A piece of work that can be queued. Implementations are plain serializable records.
/// </summary>
/// <remarks>
/// <see cref="JobType"/> is the name the handler is resolved by and the key the per-type concurrency
/// cap applies to. It is on the payload rather than inferred from the CLR type so that renaming a
/// class does not strand rows already in the queue.
/// </remarks>
public interface IJobPayload
{
    /// <summary>The stable name of this kind of work.</summary>
    static abstract string JobType { get; }
}

/// <summary>Stages work for the background worker.</summary>
public interface IJobQueue
{
    /// <summary>
    /// Queues a job on the caller's unit of work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Like <see cref="Events.IEventBus.Enqueue"/> this only adds to the change tracker — it does no
    /// I/O — so the job and the rows it describes commit together or not at all. A webhook delivery
    /// row without its processing job, or the reverse, is exactly the inconsistency this avoids.
    /// </para>
    /// <para>
    /// <b>Call it before your <c>SaveChangesAsync</c>, not after.</b> Enqueueing afterwards leaves
    /// the row in the change tracker with nothing left to persist it, and it is discarded silently
    /// when the scope is disposed — which is how every work item domain event went missing for
    /// months (audit finding 15).
    /// </para>
    /// </remarks>
    /// <param name="jobId">
    /// The idempotency key. Enqueueing the same id twice is a no-op, which is what lets a webhook
    /// redelivery be accepted without doing the work again.
    /// </param>
    /// <param name="payload">The work to do.</param>
    /// <param name="priority">Lower runs first. See <see cref="JobPriority"/>.</param>
    /// <param name="visibleAt">When it may first run. Null means immediately.</param>
    void Enqueue<TPayload>(
        Guid jobId,
        TPayload payload,
        int priority = JobPriority.Normal,
        DateTime? visibleAt = null)
        where TPayload : IJobPayload;
}

/// <summary>Does the work for one kind of job.</summary>
/// <remarks>
/// <b>Handlers must be idempotent.</b> A job is claimed under a lease and worked outside the
/// claiming transaction, so a worker that dies mid-job leaves work that another worker will redo.
/// That is deliberate — at-least-once with a duplicate is recoverable, at-most-once with a silent
/// loss is not.
/// </remarks>
public interface IJobHandler<in TPayload> where TPayload : IJobPayload
{
    Task HandleAsync(TPayload payload, CancellationToken ct = default);
}
