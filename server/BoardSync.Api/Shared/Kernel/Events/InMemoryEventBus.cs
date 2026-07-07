using Microsoft.Extensions.DependencyInjection;

namespace BoardSync.Api.Shared.Kernel.Events;

/// <summary>
/// Simple in-process event bus backed by the DI container.
/// All handlers registered as IEventHandler&lt;TEvent&gt; in DI are resolved and invoked.
/// </summary>
public class InMemoryEventBus : IEventBus
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InMemoryEventBus> _logger;

    public InMemoryEventBus(IServiceProvider serviceProvider, ILogger<InMemoryEventBus> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken = default)
        where TEvent : IDomainEvent
    {
        _logger.LogDebug("Publishing domain event {EventType} ({EventId})",
            typeof(TEvent).Name, domainEvent.EventId);

        // Resolve handlers within a new scope so scoped services (e.g. DbContext) work correctly
        using var scope = _serviceProvider.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IEventHandler<TEvent>>();

        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(domainEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling event {EventType} in handler {HandlerType}",
                    typeof(TEvent).Name, handler.GetType().Name);
                // Do not rethrow — a failing handler should not break the publishing operation
            }
        }
    }

    // Manual subscribe not needed when using DI-based handler registration,
    // but kept to satisfy the interface for future extensibility.
    public void Subscribe<TEvent>(IEventHandler<TEvent> handler) where TEvent : IDomainEvent
    {
        // No-op: DI registration is the subscribe mechanism for this implementation
    }
}
