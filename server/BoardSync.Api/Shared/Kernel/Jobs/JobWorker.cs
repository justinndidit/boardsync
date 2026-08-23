using System.Reflection;
using System.Text.Json;
using BoardSync.Api.Data;
using BoardSync.Api.Shared.Kernel.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BoardSync.Api.Shared.Kernel.Jobs;

/// <summary>
/// Drains <c>kernel.Jobs</c>: claims a job under a lease, runs its handler outside the claiming
/// transaction, then completes or reschedules it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the claim and the work are separate transactions.</b> The outbox delivers inside its
/// claiming transaction, which is right for millisecond handlers — a crash rolls the claim back and
/// nothing is lost. A job runs for minutes, and holding a Postgres transaction open that long
/// blocks vacuum, pins the connection, and makes any conflicting write wait on it. So a job is
/// claimed in one short transaction that writes a lease, worked outside it, and finished in another
/// short one.
/// </para>
/// <para>
/// The lease is what makes that safe. A worker that dies mid-job leaves a lease that expires, and
/// the next sweep reclaims the row — which is why handlers must be idempotent, and why
/// <c>SKIP LOCKED</c> alone is not enough here.
/// </para>
/// <para>
/// One job at a time per worker, deliberately. Concurrency comes from running more instances, which
/// is already how the outbox scales and needs no in-process scheduler to reason about. Per-type
/// caps are the next thing this grows if one job type starves another.
/// </para>
/// </remarks>
public class JobWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JobSettings _settings;
    private readonly ILogger<JobWorker> _logger;

    /// <summary>Identifies this worker in <see cref="Job.LeasedBy"/>. Diagnostic only.</summary>
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    public JobWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<JobSettings> settings,
        ILogger<JobWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogWarning(
                "Job worker is disabled. Queued work — webhook processing, backfills — will " +
                "accumulate and never run.");
            return;
        }

        _logger.LogInformation(
            "Job worker {WorkerId} started (lease {LeaseSeconds}s, poll {PollSeconds}s, max {MaxAttempts} attempts).",
            _workerId, _settings.LeaseSeconds, _settings.PollIntervalSeconds, _settings.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            bool worked;

            try
            {
                worked = await RunOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must outlive any single failure: a worker that exits stops every
                // background feature in the product with no signal beyond this line.
                _logger.LogError(ex, "Job worker pass failed; retrying after the poll interval.");
                worked = false;
            }

            // Straight back round while there is work, so a burst drains at full speed rather than
            // one job per poll interval.
            if (worked) continue;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Job worker {WorkerId} stopped.", _workerId);
    }

    /// <summary>Claims, runs and finishes one job. Returns false when the queue is empty.</summary>
    private async Task<bool> RunOneAsync(CancellationToken ct)
    {
        var claimed = await ClaimAsync(ct);

        if (claimed is null) return false;

        try
        {
            await ExecuteJobAsync(claimed, ct);
            await CompleteAsync(claimed.JobId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down mid-job. The lease expires and another worker picks it up; this is
            // exactly the case handler idempotency exists for.
            throw;
        }
        catch (Exception ex)
        {
            await FailAsync(claimed, ex, ct);
        }

        return true;
    }

    /// <summary>
    /// Takes the next runnable job and writes a lease, in one short transaction.
    /// </summary>
    /// <remarks>
    /// <c>SKIP LOCKED</c> is what lets several workers drain the same queue without coordinating:
    /// each takes rows the others have not locked instead of queueing behind them. The predicate is
    /// the whole runnability rule — not finished, not dead, due, and either unleased or holding an
    /// expired lease.
    /// </remarks>
    private async Task<Job?> ClaimAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>();

        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            var now = DateTime.UtcNow;

            var jobs = await context.Jobs
                .FromSqlRaw("""
                    SELECT * FROM kernel."Jobs"
                    WHERE "CompletedAt" IS NULL
                      AND "DeadAt" IS NULL
                      AND "VisibleAt" <= {0}
                      AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" < {0})
                    ORDER BY "Priority", "Sequence"
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                    """, now)
                .ToListAsync(ct);

            if (jobs.Count == 0)
            {
                await transaction.CommitAsync(ct);
                return null;
            }

            var job = jobs[0];

            job.LeaseExpiresAt = now.AddSeconds(_settings.LeaseSeconds);
            job.LeasedBy = _workerId;
            job.Attempts++;

            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return job;
        });
    }

    /// <summary>
    /// Deserializes the payload and invokes its handler.
    /// </summary>
    /// <remarks>
    /// Handlers are registered against the closed interface (<c>IJobHandler&lt;ProcessGitDelivery&gt;</c>)
    /// but only the type name survives in the row, so the closed type is rebuilt reflectively to
    /// look one up — the same shape as <c>EventDispatcher</c>.
    /// </remarks>
    private async Task ExecuteJobAsync(Job job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        var payloadType = JobTypeRegistry.Resolve(job.JobType)
            ?? throw new InvalidOperationException(
                $"No payload type is registered for job type '{job.JobType}'.");

        var payload = JsonSerializer.Deserialize(job.Payload, payloadType, JobQueue.SerializerOptions)
            ?? throw new InvalidOperationException($"Job {job.JobId} has an empty payload.");

        var handlerType = typeof(IJobHandler<>).MakeGenericType(payloadType);

        var handler = scope.ServiceProvider.GetService(handlerType)
            ?? throw new InvalidOperationException(
                $"No handler is registered for job type '{job.JobType}'.");

        // GetMethods().Single() rather than a name: IJobPayload has a static abstract member and so
        // cannot be used as a type argument, which rules out nameof(IJobHandler<...>.HandleAsync),
        // and a bare string would silently break on a rename. The interface has exactly one method.
        var method = handlerType.GetMethods().Single();

        try
        {
            await (Task)method.Invoke(handler, [payload, ct])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Reflection wraps whatever the handler threw. Unwrap it so the row records the real
            // failure rather than a meaningless "Exception has been thrown by the target".
            throw ex.InnerException;
        }
    }

    private async Task CompleteAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>();

        await context.Jobs
            .Where(j => j.JobId == jobId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.CompletedAt, DateTime.UtcNow)
                .SetProperty(j => j.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(j => j.LastError, (string?)null), ct);
    }

    /// <summary>
    /// Records a failure and either schedules a retry or gives up.
    /// </summary>
    /// <remarks>
    /// Backoff is exponential on the attempt count and capped, so a job failing for a reason that
    /// will not resolve — a bad payload, a revoked credential — stops occupying the worker while
    /// still being retried often enough to recover from an outage on its own.
    /// </remarks>
    private async Task FailAsync(Job job, Exception ex, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BoardSyncDbContext>();

        var exhausted = job.Attempts >= _settings.MaxAttempts;
        var error = Truncate(ex.Message, 2000);

        if (exhausted)
        {
            _logger.LogError(ex,
                "Job {JobId} ({JobType}) failed {Attempts} times and will not be retried.",
                job.JobId, job.JobType, job.Attempts);
        }
        else
        {
            _logger.LogWarning(ex,
                "Job {JobId} ({JobType}) failed on attempt {Attempts}; will retry.",
                job.JobId, job.JobType, job.Attempts);
        }

        var retryAt = DateTime.UtcNow.AddSeconds(
            Math.Min(_settings.MaxBackoffSeconds, _settings.BackoffSeconds * Math.Pow(2, job.Attempts - 1)));

        // Split rather than a ternary over the row: an exhausted job keeps its VisibleAt, and
        // expressing "leave this column alone" inside one ExecuteUpdate is more contortion than
        // two statements are worth.
        if (exhausted)
        {
            await context.Jobs
                .Where(j => j.JobId == job.JobId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(j => j.LastError, error)
                    .SetProperty(j => j.LeaseExpiresAt, (DateTime?)null)
                    .SetProperty(j => j.DeadAt, DateTime.UtcNow), ct);

            return;
        }

        await context.Jobs
            .Where(j => j.JobId == job.JobId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.LastError, error)
                .SetProperty(j => j.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(j => j.VisibleAt, retryAt), ct);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
