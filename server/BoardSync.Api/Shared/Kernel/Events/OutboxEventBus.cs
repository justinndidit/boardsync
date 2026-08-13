using System.Text.Json;
using BoardSync.Api.Data;

namespace BoardSync.Api.Shared.Kernel.Events;

/// <summary>
/// Stages domain events as outbox rows on the caller's <c>DbContext</c>, so they commit in the same
/// transaction as the change that raised them.
/// </summary>
/// <remarks>
/// Scoped, and it shares the request's DbContext with every repository — that shared instance is
/// exactly what makes the event and the domain row land in one transaction. It does no I/O of its
/// own: <see cref="Enqueue"/> only adds to the change tracker.
/// </remarks>
public class OutboxEventBus : IEventBus
{
    private readonly BoardSyncDbContext _context;
    private readonly ILogger<OutboxEventBus> _logger;

    public OutboxEventBus(BoardSyncDbContext context, ILogger<OutboxEventBus> logger)
    {
        _context = context;
        _logger = logger;
    }

    public void Enqueue<TEvent>(TEvent domainEvent) where TEvent : IDomainEvent
    {
        // Serialized against the concrete type, not TEvent: a caller holding an IDomainEvent
        // reference would otherwise serialize only the interface's two properties and silently
        // drop the entire payload.
        var payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), SerializerOptions);

        _context.OutboxMessages.Add(new OutboxMessage
        {
            EventId = domainEvent.EventId,
            EventType = domainEvent.GetType().Name,
            Payload = payload,
            Topics = EventTopics.For(domainEvent),
            OccurredAt = domainEvent.OccurredAt
        });

        _logger.LogDebug("Enqueued domain event {EventType} ({EventId})",
            domainEvent.GetType().Name, domainEvent.EventId);
    }

    /// <summary>
    /// Shared by the bus and the dispatcher — the two have to agree on the wire format or nothing
    /// round-trips.
    /// </summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
