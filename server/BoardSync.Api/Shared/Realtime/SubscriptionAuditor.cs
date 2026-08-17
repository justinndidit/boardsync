using BoardSync.Api.Shared.Kernel.Events;
using Microsoft.AspNetCore.SignalR;

namespace BoardSync.Api.Shared.Realtime;

/// <summary>
/// Re-checks live subscriptions and drops the ones that are no longer permitted.
/// </summary>
/// <remarks>
/// Subscriptions are authorized when they are made, and a permission granted then can be taken away
/// later. Without this, someone removed from a project would keep receiving its cards for as long
/// as they left the tab open — the socket has no reason to notice.
/// </remarks>
public interface ISubscriptionAuditor
{
    /// <summary>Re-checks one user's connections on this instance.</summary>
    /// <returns>How many subscriptions were revoked.</returns>
    Task<int> AuditUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Re-checks every connection on this instance.</summary>
    /// <returns>How many subscriptions were revoked.</returns>
    Task<int> AuditAllAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public class SubscriptionAuditor : ISubscriptionAuditor
{
    private readonly IConnectionRegistry _registry;
    private readonly IHubContext<WorkspaceHub> _hub;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SubscriptionAuditor> _logger;

    public SubscriptionAuditor(
        IConnectionRegistry registry,
        IHubContext<WorkspaceHub> hub,
        IServiceScopeFactory scopeFactory,
        ILogger<SubscriptionAuditor> logger)
    {
        _registry = registry;
        _hub = hub;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task<int> AuditUserAsync(Guid userId, CancellationToken ct = default) =>
        AuditAsync(_registry.ForUser(userId), ct);

    public Task<int> AuditAllAsync(CancellationToken ct = default) =>
        AuditAsync(_registry.All(), ct);

    private async Task<int> AuditAsync(IReadOnlyList<TrackedConnection> connections, CancellationToken ct)
    {
        if (connections.Count == 0) return 0;

        // One scope for the whole sweep: the authorizer and the RBAC chain beneath it are scoped,
        // and this runs from a background service where there is no ambient request scope.
        using var scope = _scopeFactory.CreateScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<ITopicAuthorizer>();

        var revoked = 0;

        foreach (var connection in connections)
        {
            foreach (var topic in connection.Topics)
            {
                if (ct.IsCancellationRequested) return revoked;

                // A user's own topic is theirs by identity and cannot be revoked, so it is skipped
                // rather than re-checked on every sweep.
                if (topic == Topic.User(connection.UserId)) continue;

                bool stillAllowed;

                try
                {
                    stillAllowed = await authorizer.CanSubscribeAsync(connection.UserId, topic, ct);
                }
                catch (Exception ex)
                {
                    // Fail open on an infrastructure error. A database blip must not disconnect
                    // everyone from everything; the next sweep will re-check.
                    _logger.LogWarning(ex,
                        "Could not re-authorize {Topic} for user {UserId}; leaving the subscription in place.",
                        topic, connection.UserId);
                    continue;
                }

                if (stillAllowed) continue;

                await RevokeAsync(connection, topic, ct);
                revoked++;
            }
        }

        return revoked;
    }

    private async Task RevokeAsync(TrackedConnection connection, string topic, CancellationToken ct)
    {
        await _hub.Groups.RemoveFromGroupAsync(connection.ConnectionId, topic, ct);
        _registry.RemoveTopic(connection.ConnectionId, topic);

        // Tell the client explicitly. Silently going quiet would leave it showing whatever it had
        // when access was withdrawn, with no reason to refresh or navigate away.
        await _hub.Clients.Client(connection.ConnectionId)
            .SendAsync("SubscriptionRevoked", new { topic }, ct);

        _logger.LogInformation(
            "Revoked subscription to {Topic} for user {UserId} — no longer permitted.",
            topic, connection.UserId);
    }
}
