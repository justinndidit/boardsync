using System.Threading.RateLimiting;
using StackExchange.Redis;

namespace BoardSync.Api.Shared.Kernel.RateLimiting;

/// <summary>
/// A fixed-window rate limiter whose counter lives in Redis, so the budget is shared by every
/// instance instead of being per-process.
/// </summary>
/// <remarks>
/// <para>
/// The in-process limiter multiplies the effective limit by the number of instances: the password
/// policy at 5 attempts per 5 minutes quietly becomes 15 across three replicas. That matters
/// because it is a brute-force control, and it fails open in exactly the situation — more capacity,
/// more traffic — where it should not.
/// </para>
/// <para>
/// Counting is done by a Lua script so the increment and the expiry are one atomic step. Doing them
/// as two round trips leaves a window where a crash between them creates a key with no TTL, which
/// would lock a caller out permanently.
/// </para>
/// <para>
/// <b>Fails open.</b> If Redis is unreachable the request is allowed through. A cache outage should
/// degrade a protection, not take the whole API down with it — the alternative is that losing Redis
/// rejects every request in the deployment.
/// </para>
/// </remarks>
public sealed class RedisFixedWindowRateLimiter : RateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _key;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly ILogger _logger;

    /// <summary>
    /// Increments the window's counter and sets its expiry on first use, atomically.
    /// Returns the count after incrementing.
    /// </summary>
    private const string IncrementScript = """
        local current = redis.call('INCR', KEYS[1])
        if current == 1 then
            redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
        return current
        """;

    public RedisFixedWindowRateLimiter(
        IConnectionMultiplexer redis,
        string key,
        int permitLimit,
        TimeSpan window,
        ILogger logger)
    {
        _redis = redis;
        _key = key;
        _permitLimit = permitLimit;
        _window = window;
        _logger = logger;
    }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        try
        {
            var db = _redis.GetDatabase();

            var count = (long)await db.ScriptEvaluateAsync(
                IncrementScript,
                [_key],
                [(long)_window.TotalMilliseconds]);

            if (count <= _permitLimit)
                return new Lease(isAcquired: true, retryAfter: null);

            // Only the remaining TTL is a useful Retry-After; the full window would overstate it
            // for a caller who arrived late in one.
            var ttl = await db.KeyTimeToLiveAsync(_key) ?? _window;

            return new Lease(isAcquired: false, retryAfter: ttl);
        }
        catch (RedisConnectionException ex)
        {
            _logger.LogWarning(ex,
                "Rate limiter could not reach Redis; allowing the request through. " +
                "Limits are not being enforced while this persists.");

            return new Lease(isAcquired: true, retryAfter: null);
        }
        catch (RedisTimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Rate limiter timed out talking to Redis; allowing the request through.");

            return new Lease(isAcquired: true, retryAfter: null);
        }
    }

    /// <summary>
    /// Always declines, which routes every decision through <see cref="AcquireAsyncCore"/>.
    /// </summary>
    /// <remarks>
    /// This must not grant. The counter lives across the network and cannot be consulted without
    /// awaiting, and ASP.NET Core's rate limiting middleware tries the synchronous path first and
    /// only falls back to the async one when it does not acquire. A lease granted here would
    /// therefore short-circuit the middleware into allowing every request without Redis ever being
    /// asked — the limiter would look wired up and enforce nothing.
    /// </remarks>
    protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
        new Lease(isAcquired: false, retryAfter: null);

    private sealed class Lease : RateLimitLease
    {
        private readonly TimeSpan? _retryAfter;

        public Lease(bool isAcquired, TimeSpan? retryAfter)
        {
            IsAcquired = isAcquired;
            _retryAfter = retryAfter;
        }

        public override bool IsAcquired { get; }

        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == MetadataName.RetryAfter.Name && _retryAfter.HasValue)
            {
                metadata = _retryAfter.Value;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
