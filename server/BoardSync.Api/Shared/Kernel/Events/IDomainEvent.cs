namespace BoardSync.Api.Shared.Kernel.Events;

/// <summary>
/// Marker interface for all domain events published on the internal event bus.
/// </summary>
public interface IDomainEvent
{
    Guid EventId { get; }
    DateTime OccurredAt { get; }
}

/// <summary>
/// Base record for domain events — provides EventId and OccurredAt automatically.
/// </summary>
/// <remarks>
/// Both properties are <c>init</c>, not get-only, and that is load-bearing rather than stylistic.
/// Events round-trip through the outbox as JSON, and a get-only auto-property cannot be set by the
/// deserializer — it would silently fall back to the initializer and mint a <b>new</b> id every
/// time a message was read back. Identity would then differ on every redelivery, which defeats the
/// whole point of keying idempotency on <see cref="EventId"/>: the unique index would never see a
/// duplicate and handlers would happily record the same event twice.
///
/// <c>init</c> keeps the record immutable to callers while still letting the deserializer restore
/// the original values.
/// </remarks>
public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
