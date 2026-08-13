namespace BoardSync.Api.Shared.Kernel.Events;

/// <summary>
/// A domain event durably queued for delivery, written in the same transaction as the change that
/// raised it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the previous design could lose events silently. Services committed their
/// write, then published on an in-process bus that swallowed handler exceptions — so a crash, a
/// timeout, or a thrown handler between the two left the activity log with a hole and nobody the
/// wiser. Staging the event on the same <c>DbContext</c> makes the two atomic: no commit, no event;
/// commit, guaranteed event.
/// </para>
/// <para>
/// The cost is that delivery becomes at-least-once rather than at-most-once, so handlers must be
/// idempotent. That is the better half of the trade — a duplicate is recoverable, a silent loss is
/// not.
/// </para>
/// </remarks>
public class OutboxMessage
{
    /// <summary>
    /// Database-generated, monotonically increasing. This is the ordering authority for the whole
    /// system: per-topic order follows from a global sequence, which is what will let a real-time
    /// client say "I last saw 412, catch me up" without a second event store.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// The originating event's own id. Unique, and the key handlers deduplicate on — a redelivered
    /// message carries the same EventId, so an idempotent handler can recognise it.
    /// </summary>
    public Guid EventId { get; set; }

    /// <summary>
    /// The event's CLR type name (e.g. <c>WorkItemStateChanged</c>), used to resolve the handler.
    /// Deliberately the short name and not an assembly-qualified one: a namespace reorganisation
    /// should not strand messages already sitting in the queue.
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>The serialized event, as JSON.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>When the event happened, copied off the event itself.</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// When the dispatcher finished with this message. Null means still queued — the partial index
    /// on this column is what keeps claiming cheap as the table grows.
    /// </summary>
    public DateTime? DispatchedAt { get; set; }

    /// <summary>
    /// How many delivery attempts have been made. Incremented on failure so a message that cannot
    /// ever succeed stops being retried forever and becomes visible instead.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>Last failure, kept for diagnosis. Null while healthy.</summary>
    public string? LastError { get; set; }
}
