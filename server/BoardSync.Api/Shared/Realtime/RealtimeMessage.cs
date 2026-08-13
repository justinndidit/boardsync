using System.Text.Json;

namespace BoardSync.Api.Shared.Realtime;

/// <summary>
/// What a client receives on the wire.
/// </summary>
/// <param name="Sequence">
/// Global, monotonically increasing. The client stores the highest it has processed per topic and
/// sends it back on reconnect to be caught up.
/// </param>
/// <param name="Topic">Which subscription this arrived on. A client may hold several.</param>
/// <param name="Type">
/// The domain event name, e.g. <c>WorkItemStateChanged</c>. Switch on this to decide how to apply
/// the payload.
/// </param>
/// <param name="Payload">
/// The event itself. Self-sufficient by design — enough to patch local state without a refetch.
/// </param>
/// <param name="OccurredAt">When the change happened, not when it was delivered.</param>
/// <remarks>
/// Messages carry deltas rather than "something changed, go refetch". A bare invalidation turns
/// every write by one user into a read by every other user watching, which is the fanout that hurts
/// most exactly when a board is busiest.
/// </remarks>
public record RealtimeMessage(
    long Sequence,
    string Topic,
    string Type,
    JsonElement Payload,
    DateTime OccurredAt);
