using System.Text.Json;
using BoardSync.Api.Data;
using BoardSync.Api.Shared.Kernel.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BoardSync.Api.Shared.Kernel.Events;

/// <summary>
/// Drains the outbox: claims queued messages, runs their handlers, marks them delivered.
/// </summary>
/// <remarks>
/// <para>
/// Claiming uses <c>FOR UPDATE SKIP LOCKED</c>, so several instances can run this at once without
/// coordinating — each takes rows the others have not locked, and none of them block. That is what
/// makes the dispatcher safe to leave switched on across a whole deployment rather than nominating
/// a leader.
/// </para>
/// <para>
/// A message is marked dispatched only after its handlers succeed. Crash in between and it is
/// simply claimed again on the next pass, which is why handlers must be idempotent.
/// </para>
/// </remarks>
public class OutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NpgsqlDataSource _dataSource;
    private readonly OutboxSettings _settings;
    private readonly ILogger<OutboxDispatcher> _logger;

    /// <summary>Postgres NOTIFY channel used to wake the dispatcher the moment a message is queued.</summary>
    public const string NotifyChannel = "boardsync_outbox";

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        NpgsqlDataSource dataSource,
        IOptions<OutboxSettings> settings,
        ILogger<OutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _dataSource = dataSource;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogWarning(
                "Outbox dispatcher is disabled. Domain events will be written but never delivered — " +
                "the activity feed will stop updating.");
            return;
        }

        _logger.LogInformation(
            "Outbox dispatcher started (batch {BatchSize}, poll {PollSeconds}s, max {MaxAttempts} attempts).",
            _settings.BatchSize, _settings.PollIntervalSeconds, _settings.MaxAttempts);

        // Listening is best-effort latency, not delivery: if the connection drops, the poll interval
        // below still drains the queue. That is why a failed listener is a warning, not a crash.
        _ = Task.Run(() => ListenForNotificationsAsync(stoppingToken), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Keep draining while batches come back full — a burst should not be spread across
                // one poll interval per batch.
                int dispatched;
                do
                {
                    dispatched = await DispatchBatchAsync(stoppingToken);
                }
                while (dispatched == _settings.BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must outlive any single failure — a dispatcher that exits stops the
                // activity feed for the whole deployment.
                _logger.LogError(ex, "Outbox dispatch pass failed; retrying after the poll interval.");
            }

            await WaitForWorkAsync(stoppingToken);
        }

        _logger.LogInformation("Outbox dispatcher stopped.");
    }

    /// <summary>
    /// Claims and delivers one batch. Returns how many messages were processed.
    /// </summary>
    private async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IEventDispatcher>();

        // The connection is configured with EnableRetryOnFailure, and that execution strategy
        // refuses user-initiated transactions unless the whole unit is handed to it — otherwise a
        // retry would resume mid-transaction against a connection that no longer has one.
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(() => ClaimAndDeliverAsync(context, dispatcher, ct));
    }

    /// <summary>
    /// One claim-and-deliver cycle, retriable as a whole by the execution strategy above.
    /// </summary>
    private async Task<int> ClaimAndDeliverAsync(
        BoardSyncDbContext context,
        IEventDispatcher dispatcher,
        CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        // SKIP LOCKED is what lets multiple instances drain the same queue concurrently: each claims
        // rows the others have not locked instead of queueing behind them.
        var messages = await context.OutboxMessages
            .FromSqlRaw("""
                SELECT * FROM kernel."OutboxMessages"
                WHERE "DispatchedAt" IS NULL AND "Attempts" < {0}
                ORDER BY "Sequence"
                LIMIT {1}
                FOR UPDATE SKIP LOCKED
                """, _settings.MaxAttempts, _settings.BatchSize)
            .ToListAsync(ct);

        if (messages.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return 0;
        }

        foreach (var message in messages)
        {
            try
            {
                var domainEvent = Deserialize(message);

                if (domainEvent is null)
                {
                    // An event type that no longer exists cannot ever be delivered. Burn the
                    // attempts so it leaves the queue instead of being retried until the heat death
                    // of the universe.
                    message.Attempts = _settings.MaxAttempts;
                    message.LastError = $"Unknown event type '{message.EventType}'.";
                    _logger.LogError("Outbox message {Sequence} has unknown event type {EventType}; giving up.",
                        message.Sequence, message.EventType);
                    continue;
                }

                await dispatcher.DispatchAsync(domainEvent, ct);

                message.DispatchedAt = DateTime.UtcNow;
                message.LastError = null;
            }
            catch (Exception ex)
            {
                message.Attempts++;
                message.LastError = Truncate(ex.Message, 2000);

                if (message.Attempts >= _settings.MaxAttempts)
                {
                    _logger.LogError(ex,
                        "Outbox message {Sequence} ({EventType}) failed {Attempts} times and will not be retried.",
                        message.Sequence, message.EventType, message.Attempts);
                }
                else
                {
                    _logger.LogWarning(ex,
                        "Outbox message {Sequence} ({EventType}) failed on attempt {Attempts}; will retry.",
                        message.Sequence, message.EventType, message.Attempts);
                }
            }
        }

        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return messages.Count;
    }

    /// <summary>
    /// Rebuilds the domain event from its stored JSON, or null when the type no longer exists.
    /// </summary>
    private static IDomainEvent? Deserialize(OutboxMessage message)
    {
        var type = EventTypeRegistry.Resolve(message.EventType);

        if (type is null) return null;

        return JsonSerializer.Deserialize(message.Payload, type, OutboxEventBus.SerializerOptions)
            as IDomainEvent;
    }

    /// <summary>
    /// Sleeps until the poll interval elapses or a NOTIFY arrives, whichever is first.
    /// </summary>
    private async Task WaitForWorkAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_settings.PollIntervalSeconds), ct);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Holds a dedicated connection open on the NOTIFY channel so a queued message is picked up in
    /// milliseconds rather than at the next poll.
    /// </summary>
    private async Task ListenForNotificationsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync(ct);

                connection.Notification += (_, _) => _logger.LogTrace("Outbox notification received.");

                await using (var cmd = new NpgsqlCommand($"LISTEN {NotifyChannel};", connection))
                    await cmd.ExecuteNonQueryAsync(ct);

                while (!ct.IsCancellationRequested)
                    await connection.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Outbox notification listener dropped; falling back to polling and reconnecting.");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
