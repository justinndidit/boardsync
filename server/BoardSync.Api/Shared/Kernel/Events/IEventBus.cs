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

    // Subscription is DI registration: register an IEventHandler<TEvent> and the bus will resolve
    // and invoke it. There is deliberately no Subscribe() method — the previous one was a no-op
    // that silently did nothing when called.
}

/// <summary>
/// Handler for a specific domain event type.
/// </summary>
public interface IEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default);
}
