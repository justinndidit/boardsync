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
public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
