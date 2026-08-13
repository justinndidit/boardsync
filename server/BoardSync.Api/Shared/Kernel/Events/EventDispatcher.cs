using System.Reflection;

namespace BoardSync.Api.Shared.Kernel.Events;

/// <summary>
/// Resolves and runs the handlers registered for a domain event.
/// </summary>
/// <remarks>
/// Handlers are registered against the closed interface (<c>IEventHandler&lt;WorkItemCreated&gt;</c>),
/// but the dispatcher only has an <see cref="IDomainEvent"/> at runtime, so the closed type is
/// rebuilt reflectively to look them up.
/// </remarks>
public class EventDispatcher : IEventDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EventDispatcher> _logger;

    public EventDispatcher(IServiceProvider serviceProvider, ILogger<EventDispatcher> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task DispatchAsync(IDomainEvent domainEvent, CancellationToken ct = default)
    {
        var eventType = domainEvent.GetType();
        var handlerType = typeof(IEventHandler<>).MakeGenericType(eventType);

        var handlers = _serviceProvider.GetServices(handlerType).ToList();

        if (handlers.Count == 0)
        {
            _logger.LogDebug("No handlers registered for {EventType}", eventType.Name);
            return;
        }

        var method = handlerType.GetMethod(nameof(IEventHandler<IDomainEvent>.HandleAsync))
            ?? throw new InvalidOperationException($"HandleAsync not found on {handlerType.Name}.");

        foreach (var handler in handlers)
        {
            if (handler is null) continue;

            try
            {
                await (Task)method.Invoke(handler, [domainEvent, ct])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                // Reflection wraps whatever the handler threw. Unwrap it so the dispatcher records
                // the real failure against the outbox row instead of a meaningless
                // "Exception has been thrown by the target of an invocation".
                throw ex.InnerException;
            }
        }
    }
}
