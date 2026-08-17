using StackExchange.Redis;

namespace BoardSync.Api.Modules.Rbac.Services;

/// <summary>
/// The generation counter that makes a revocation take effect immediately.
/// </summary>
/// <remarks>
/// <para>
/// Each user has a counter in Redis that is folded into every cache key holding something about
/// them. Advancing it makes everything previously cached about that user unreachable in one atomic
/// operation, and the orphans expire on their own.
/// </para>
/// <para>
/// Deliberately not tag-based eviction: that depends on the cache provider actually implementing tag
/// removal across both tiers, and a silently unimplemented eviction here means a revoked user keeps
/// their access. A key nobody can name is a key nobody can read.
/// </para>
/// </remarks>
public interface IAccessCacheVersion
{
    /// <summary>
    /// The user's current generation.
    /// </summary>
    /// <exception cref="RedisConnectionException">
    /// The counter could not be read. Callers must treat this as "cache unusable" and go to the
    /// database — serving an entry whose generation cannot be confirmed risks honouring a grant
    /// that has since been revoked.
    /// </exception>
    Task<long> GetAsync(Guid userId);

    /// <summary>
    /// Advances the user's generation, orphaning everything cached about them.
    /// </summary>
    /// <remarks>
    /// Failures are not swallowed. If the generation cannot be advanced, previously cached entries
    /// stay reachable and a revoked user keeps their access until those entries expire — the caller
    /// needs the write to fail rather than to quietly not take effect.
    /// </remarks>
    Task BumpAsync(Guid userId);
}

/// <inheritdoc />
public sealed class AccessCacheVersion : IAccessCacheVersion
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<AccessCacheVersion> _logger;

    /// <summary>
    /// Bumped when the cached shape or its meaning changes. A deploy that changes how access is
    /// computed must not read entries written by the previous one.
    /// </summary>
    public const string KeyVersion = "v3";

    public AccessCacheVersion(IConnectionMultiplexer redis, ILogger<AccessCacheVersion> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// Absent means zero — a user nobody has ever revoked anything from starts at generation 0.
    /// </summary>
    public async Task<long> GetAsync(Guid userId)
    {
        var value = await _redis.GetDatabase().StringGetAsync(Key(userId));
        return value.HasValue && value.TryParse(out long version) ? version : 0;
    }

    public async Task BumpAsync(Guid userId)
    {
        var version = await _redis.GetDatabase().StringIncrementAsync(Key(userId));

        _logger.LogDebug("Access cache for user {UserId} advanced to generation {Version}",
            userId, version);
    }

    private static string Key(Guid userId) => $"rbac:{KeyVersion}:ver:{userId}";
}

/// <summary>
/// The generation counter when there is no Redis to hold it.
/// </summary>
/// <remarks>
/// Without Redis nothing is cached across requests, so there is no generation to advance and
/// nothing for a bump to orphan. Registering this rather than leaving the dependency null keeps the
/// write path identical in both configurations — in particular it keeps the per-request memo being
/// dropped on every write, which matters whether or not a distributed cache exists.
/// </remarks>
public sealed class NullAccessCacheVersion : IAccessCacheVersion
{
    public Task<long> GetAsync(Guid userId) => Task.FromResult(0L);

    public Task BumpAsync(Guid userId) => Task.CompletedTask;
}
