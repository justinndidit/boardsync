namespace BoardSync.Api.Shared.Kernel.Events;

/// <summary>
/// How modules tell the rest of the system that something happened.
/// </summary>
/// <remarks>
/// <para>
/// Enqueuing stages the event on the caller's unit of work. It is <b>not</b> delivered here —
/// it is written to the outbox by the next <c>SaveChangesAsync</c>, in the same transaction as the
/// domain change, and delivered afterwards by the dispatcher.
/// </para>
/// <para>
/// That ordering is the whole point, and it is the opposite of what this interface used to do.
/// Enqueue <b>before</b> saving:
/// </para>
/// <code>
/// _eventBus.Enqueue(new WorkItemStateChanged(...));
/// await _repository.SaveChangesAsync(ct);   // domain row + outbox row, one transaction
/// </code>
/// <para>
/// Enqueuing after the save still works — the event lands on the next save — but it is a trap: if
/// nothing saves again, the event never leaves. Put it before.
/// </para>
/// </remarks>
public interface IEventBus
{
    /// <summary>
    /// Stages a domain event for durable delivery. Returns immediately; nothing is written until
    /// the surrounding unit of work is saved.
    /// </summary>
    void Enqueue<TEvent>(TEvent domainEvent) where TEvent : IDomainEvent;
}

/// <summary>
/// Invokes the handlers subscribed to a domain event. Used by the outbox dispatcher, not by the
/// modules that raise events.
/// </summary>
public interface IEventDispatcher
{
    /// <summary>
    /// Resolves and runs every handler registered for the event's type.
    /// </summary>
    /// <remarks>
    /// Exceptions propagate. The dispatcher records the failure against the outbox row and retries
    /// it, which is only possible because nothing is marked dispatched until the handlers succeed.
    /// </remarks>
    Task DispatchAsync(IDomainEvent domainEvent, CancellationToken ct = default);
}

/// <summary>
/// Handler for a specific domain event type.
/// </summary>
/// <remarks>
/// Subscription is DI registration: register an <see cref="IEventHandler{TEvent}"/> and the
/// dispatcher will resolve and invoke it.
///
/// Handlers must be <b>idempotent</b>. The outbox delivers at least once, so the same event can
/// arrive twice — after a dispatcher crash between running the handlers and marking the row
/// dispatched, for instance. Key any write on <see cref="IDomainEvent.EventId"/>.
/// </remarks>
public interface IEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default);
}
