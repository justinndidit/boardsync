namespace BoardSync.Api.Shared.Kernel.Events;

/// <summary>
/// In-process event bus abstraction.
/// Modules publish events here; other modules subscribe to react without direct coupling.
/// </summary>
public interface IEventBus
{
    /// <summary>Publish a domain event to all registered handlers.</summary>
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken = default)
        where TEvent : IDomainEvent;

    /// <summary>Subscribe a handler for a specific event type.</summary>
    void Subscribe<TEvent>(IEventHandler<TEvent> handler)
        where TEvent : IDomainEvent;
}

/// <summary>
/// Handler for a specific domain event type.
/// </summary>
public interface IEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default);
}
