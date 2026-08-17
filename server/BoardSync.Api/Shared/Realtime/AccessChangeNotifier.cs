using BoardSync.Api.Shared.Kernel.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BoardSync.Api.Shared.Realtime;

/// <summary>
/// Tells every instance that one user's access has changed and their subscriptions need re-checking.
/// </summary>
/// <remarks>
/// <para>
/// A role change is processed by whichever instance happened to dispatch the event, but the affected
/// user's connections could be held by any of them. Redis pub/sub is the fan-out: every instance
/// hears, and each acts on the connections it actually holds.
/// </para>
/// <para>
/// Not the SignalR backplane, which delivers to <em>clients</em>. This has to reach instance-side
/// code, because dropping someone from a group is a server decision — asking the client to give up
/// its own access would be no security boundary at all.
/// </para>
/// </remarks>
public interface IAccessChangeNotifier
{
    /// <summary>Announces that a user's permissions changed.</summary>
    Task AnnounceAsync(Guid userId, CancellationToken ct = default);
}

/// <inheritdoc />
public class AccessChangeNotifier : IAccessChangeNotifier
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly ISubscriptionAuditor _auditor;
    private readonly ILogger<AccessChangeNotifier> _logger;

    /// <summary>Channel carrying user ids whose access changed.</summary>
    public static readonly RedisChannel Channel = RedisChannel.Literal("boardsync:access-changed");

    public AccessChangeNotifier(
        ISubscriptionAuditor auditor,
        ILogger<AccessChangeNotifier> logger,
        IConnectionMultiplexer? redis = null)
    {
        _auditor = auditor;
        _logger = logger;
        _redis = redis;
    }

    public async Task AnnounceAsync(Guid userId, CancellationToken ct = default)
    {
        if (_redis is null)
        {
            // Single instance: this process holds every connection there is, so acting locally is
            // the complete answer rather than a fallback.
            await _auditor.AuditUserAsync(userId, ct);
            return;
        }

        try
        {
            // Published rather than handled locally, because the subscriber below runs on every
            // instance including this one — handling it here as well would audit twice.
            await _redis.GetSubscriber().PublishAsync(Channel, userId.ToString());
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            // Falling back to a local audit still covers this instance's connections; the periodic
            // sweep catches everyone else within its interval.
            _logger.LogWarning(ex,
                "Could not announce an access change for {UserId}; auditing locally instead. " +
                "Other instances will catch up on their next sweep.", userId);

            await _auditor.AuditUserAsync(userId, ct);
        }
    }
}

/// <summary>
/// Keeps subscriptions honest: re-checks everything periodically, and immediately when a user's
/// access changes.
/// </summary>
/// <remarks>
/// Two mechanisms because they fail differently. The announcement is fast but depends on Redis and
/// on the event actually being raised; the sweep is slower but depends on nothing and will find a
/// stale subscription whatever caused it — including a permission changed directly in the database.
/// </remarks>
public class SubscriptionAuditService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionMultiplexer? _redis;
    private readonly RealtimeSettings _settings;
    private readonly ILogger<SubscriptionAuditService> _logger;

    public SubscriptionAuditService(
        IServiceScopeFactory scopeFactory,
        IOptions<RealtimeSettings> settings,
        ILogger<SubscriptionAuditService> logger,
        IConnectionMultiplexer? redis = null)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
        _redis = redis;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled) return;

        await SubscribeToAccessChangesAsync(stoppingToken);

        _logger.LogInformation(
            "Subscription auditor started (sweeping every {Seconds}s).",
            _settings.ReauthorizationIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_settings.ReauthorizationIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var auditor = scope.ServiceProvider.GetRequiredService<ISubscriptionAuditor>();

                var revoked = await auditor.AuditAllAsync(stoppingToken);

                if (revoked > 0)
                    _logger.LogInformation("Periodic sweep revoked {Count} stale subscription(s).", revoked);
            }
            catch (Exception ex)
            {
                // The loop must outlive any single failure — stopping it would silently leave every
                // revoked subscription in place until the process restarted.
                _logger.LogError(ex, "Subscription sweep failed; retrying next interval.");
            }
        }
    }

    private async Task SubscribeToAccessChangesAsync(CancellationToken ct)
    {
        if (_redis is null) return;

        try
        {
            var subscriber = _redis.GetSubscriber();

            await subscriber.SubscribeAsync(AccessChangeNotifier.Channel, async (_, value) =>
            {
                if (!Guid.TryParse(value.ToString(), out var userId)) return;

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var auditor = scope.ServiceProvider.GetRequiredService<ISubscriptionAuditor>();

                    await auditor.AuditUserAsync(userId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to audit subscriptions for {UserId} after an access change.", userId);
                }
            });

            _logger.LogInformation("Listening for access changes on {Channel}.", AccessChangeNotifier.Channel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not subscribe to access changes; revocations will only take effect on the " +
                "periodic sweep.");
        }
    }
}
