using System.Collections.Frozen;
using System.Reflection;

namespace BoardSync.Api.Shared.Kernel.Events;

/// <summary>
/// Maps the event type names stored in the outbox back to CLR types.
/// </summary>
/// <remarks>
/// Outbox rows store the short type name rather than an assembly-qualified one, so moving an event
/// between namespaces does not strand messages already queued. The cost is that names must stay
/// unique across the assembly — <see cref="Resolve"/> refuses to guess between duplicates rather
/// than silently picking one and deserializing into the wrong shape.
/// </remarks>
public static class EventTypeRegistry
{
    private static readonly FrozenDictionary<string, Type> ByName = Build();

    /// <summary>The CLR type for a stored event name, or null if nothing matches it.</summary>
    public static Type? Resolve(string eventTypeName) =>
        ByName.TryGetValue(eventTypeName, out var type) ? type : null;

    /// <summary>Every domain event type known to this build. Exposed for the startup self-check.</summary>
    public static IReadOnlyCollection<string> KnownEventTypes => ByName.Keys;

    private static FrozenDictionary<string, Type> Build()
    {
        var eventTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => typeof(IDomainEvent).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .ToList();

        var duplicates = eventTypes
            .GroupBy(t => t.Name)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(t => t.FullName))})")
            .ToList();

        if (duplicates.Count > 0)
        {
            // Fail at startup rather than at dispatch time. A collision here means outbox messages
            // would deserialize into whichever type won the race — a data corruption bug that would
            // otherwise surface much later and much more confusingly.
            throw new InvalidOperationException(
                "Domain event type names must be unique across the assembly, because the outbox " +
                "stores the short name. Duplicates: " + string.Join("; ", duplicates));
        }

        return eventTypes.ToFrozenDictionary(t => t.Name, t => t);
    }
}
