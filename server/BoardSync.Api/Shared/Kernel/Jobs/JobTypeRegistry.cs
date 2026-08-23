using System.Collections.Frozen;
using System.Reflection;

namespace BoardSync.Api.Shared.Kernel.Jobs;

/// <summary>
/// Maps the job type names stored in <c>kernel.Jobs</c> back to CLR payload types.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="Events.EventTypeRegistry"/>, and different in one way: a job's name
/// is declared explicitly on <see cref="IJobPayload.JobType"/> rather than taken from the class
/// name. Renaming the class is then free, which matters more here than for events — a job can sit
/// in the queue across a deploy that renames it, and a backfill can sit there for hours.
/// </para>
/// <para>
/// Names must still be unique. <see cref="Resolve"/> refuses to guess between duplicates rather than
/// picking one and deserializing into the wrong shape, and the check runs at startup so a collision
/// is a boot failure rather than a corruption discovered later.
/// </para>
/// </remarks>
public static class JobTypeRegistry
{
    private static readonly FrozenDictionary<string, Type> ByName = Build();

    /// <summary>The payload type for a stored job name, or null if nothing matches it.</summary>
    public static Type? Resolve(string jobTypeName) =>
        ByName.TryGetValue(jobTypeName, out var type) ? type : null;

    /// <summary>Every job type known to this build. Exposed for the startup self-check.</summary>
    public static IReadOnlyCollection<string> KnownJobTypes => ByName.Keys;

    private static FrozenDictionary<string, Type> Build()
    {
        var payloadTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => typeof(IJobPayload).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => (Type: t, Name: NameOf(t)))
            .ToList();

        var duplicates = payloadTypes
            .GroupBy(p => p.Name)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(p => p.Type.FullName))})")
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "Job type names must be unique, because kernel.Jobs stores the name. " +
                "Duplicates: " + string.Join("; ", duplicates));
        }

        return payloadTypes.ToFrozenDictionary(p => p.Name, p => p.Type);
    }

    /// <summary>
    /// Reads the static abstract <c>JobType</c> off a payload type.
    /// </summary>
    /// <remarks>
    /// Reflection rather than a generic call, because this walks types discovered at runtime and has
    /// no <c>TPayload</c> to work with.
    /// </remarks>
    private static string NameOf(Type payloadType) =>
        (string)payloadType
            .GetProperty(nameof(IJobPayload.JobType),
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)!
            .GetValue(null)!;
}
