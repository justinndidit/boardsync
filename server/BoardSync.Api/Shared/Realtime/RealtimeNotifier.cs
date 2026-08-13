using System.Text.Json;
using BoardSync.Api.Shared.Kernel.Events;
using Microsoft.AspNetCore.SignalR;

namespace BoardSync.Api.Shared.Realtime;

/// <summary>
/// Pushes a delivered event out to everyone watching the topics it belongs to.
/// </summary>
public interface IRealtimeNotifier
{
    /// <summary>
    /// Sends one outbox message to its topics.
    /// </summary>
    /// <remarks>
    /// Never throws. Real-time delivery is an optimisation on top of state that is already
    /// committed and already readable over REST — a hub failure must not fail the outbox message
    /// and send the whole thing round again, because that would duplicate the push to everyone who
    /// did receive it.
    /// </remarks>
    Task NotifyAsync(OutboxMessage message, CancellationToken ct = default);
}

/// <inheritdoc />
public class SignalRNotifier : IRealtimeNotifier
{
    private readonly IHubContext<WorkspaceHub> _hub;
    private readonly ILogger<SignalRNotifier> _logger;

    public SignalRNotifier(IHubContext<WorkspaceHub> hub, ILogger<SignalRNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task NotifyAsync(OutboxMessage message, CancellationToken ct = default)
    {
        if (message.Topics.Length == 0)
        {
            // Routable events should route somewhere. Silence here usually means EventTopics has a
            // gap rather than that nobody cares, so it is worth being able to grep for.
            _logger.LogDebug("Outbox message {Sequence} ({EventType}) has no topics; nothing to push.",
                message.Sequence, message.EventType);
            return;
        }

        JsonElement payload;

        try
        {
            payload = JsonDocument.Parse(message.Payload).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Outbox message {Sequence} has unparseable payload; not pushing.",
                message.Sequence);
            return;
        }

        foreach (var topic in message.Topics)
        {
            try
            {
                var envelope = new RealtimeMessage(
                    message.Sequence, topic, message.EventType, payload, message.OccurredAt);

                await _hub.Clients.Group(topic).SendAsync("Message", envelope, ct);
            }
            catch (Exception ex)
            {
                // One topic failing must not stop the others — and must not fail the message.
                _logger.LogWarning(ex,
                    "Failed to push outbox message {Sequence} to topic {Topic}; " +
                    "subscribers will recover on their next reconnect.",
                    message.Sequence, topic);
            }
        }
    }
}
