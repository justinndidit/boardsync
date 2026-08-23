using System.Text.Json;
using BoardSync.Api.Data;
using BoardSync.Api.Shared.Kernel.Configuration;
using BoardSync.Api.Modules.Sprints.Services;
using BoardSync.Api.Shared.Realtime;
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

    /// <summary>
    /// Raised by the NOTIFY listener, awaited by the dispatch loop.
    /// </summary>
    /// <remarks>
    /// Bounded at one, so a burst of notifications collapses into a single wake rather than queueing
    /// one pass per message. That is the behaviour you want: the loop already drains until a batch
    /// comes back short, so one wake covers everything the burst enqueued.
    /// </remarks>
    private readonly SemaphoreSlim _workAvailable = new(0, 1);

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
        var notifier = scope.ServiceProvider.GetRequiredService<IRealtimeNotifier>();
        var boardVersion = scope.ServiceProvider.GetService<IBoardCacheVersion>();

        // The connection is configured with EnableRetryOnFailure, and that execution strategy
        // refuses user-initiated transactions unless the whole unit is handed to it — otherwise a
        // retry would resume mid-transaction against a connection that no longer has one.
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            () => ClaimAndDeliverAsync(context, dispatcher, notifier, boardVersion, ct));
    }

    /// <summary>
    /// One claim-and-deliver cycle, retriable as a whole by the execution strategy above.
    /// </summary>
    private async Task<int> ClaimAndDeliverAsync(
        BoardSyncDbContext context,
        IEventDispatcher dispatcher,
        IRealtimeNotifier notifier,
        IBoardCacheVersion? boardVersion,
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

                // After the handlers, so a client is never told about a change before the state it
                // describes has been recorded. NotifyAsync does not throw: a hub failure must not
                // retry the message and re-run handlers that already succeeded.
                await notifier.NotifyAsync(message, ct);

                // Anything on a project topic can change what that project's board renders, so the
                // board's generation advances here rather than at each of the dozen call sites that
                // could have caused it. One place to get right instead of twelve.
                await InvalidateBoardsAsync(boardVersion, message);

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

    public override void Dispose()
    {
        _workAvailable.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Wakes the dispatch loop, if it is waiting.
    /// </summary>
    /// <remarks>
    /// Releasing a full semaphore throws, and here that simply means "a wake is already pending",
    /// which is not a problem worth propagating out of an event handler running on the Npgsql
    /// connection thread.
    /// </remarks>
    private void SignalWorkAvailable()
    {
        try
        {
            _workAvailable.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already pending; one is enough.
        }
    }

    /// <summary>
    /// Waits until a NOTIFY arrives or the poll interval elapses, whichever is first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be an unconditional <c>Task.Delay</c>, while the listener below merely wrote a
    /// trace line. The whole NOTIFY path was therefore decorative: the trigger fired, the listener
    /// received it, and nothing woke. Delivery latency was a uniform 0–5s rather than the
    /// milliseconds claimed here, in <c>OutboxSettings</c> and in the README — and every downstream
    /// feature inherited it, the activity feed, live board updates and the notification bell alike.
    /// </para>
    /// <para>
    /// The timeout stays. It is the safety net the documentation always described: if the listener
    /// connection drops, or a NOTIFY is lost between the trigger firing and the listener
    /// reconnecting, the queue still drains within one interval.
    /// </para>
    /// </remarks>
    private async Task WaitForWorkAsync(CancellationToken ct)
    {
        try
        {
            await _workAvailable.WaitAsync(TimeSpan.FromSeconds(_settings.PollIntervalSeconds), ct);
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

                connection.Notification += (_, _) => SignalWorkAvailable();

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

    /// <summary>
    /// Advances the board cache generation for every project this message touched.
    /// </summary>
    private static async Task InvalidateBoardsAsync(IBoardCacheVersion? boardVersion, OutboxMessage message)
    {
        if (boardVersion is null) return;

        foreach (var topic in message.Topics)
        {
            if (Topic.TryParse(topic, out var kind, out var id) && kind == TopicKind.Project)
                await boardVersion.BumpAsync(id);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
