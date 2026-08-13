using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using Microsoft.Extensions.Caching.Hybrid;
using StackExchange.Redis;

namespace BoardSync.Api.Modules.Rbac.Services.Implementations;

/// <summary>
/// Caches permission decisions across requests, in memory and in Redis.
/// </summary>
/// <remarks>
/// <para>
/// Role assignments are the most-read and least-written data in the system: every authorized action
/// asks, and almost nothing changes. Before this, each ask cost a query — two when the direct match
/// failed and org-admin inheritance had to be tested.
/// </para>
/// <para>
/// <b>Invalidation is by version stamp, not by deleting keys.</b> Each user has a counter in Redis
/// that is included in every decision key; bumping it makes every previously cached decision about
/// that user unreachable in one atomic operation, and the orphans expire on their own. This is the
/// pattern the scaling design calls for, and it is deliberately not tag-based eviction: that would
/// depend on the cache provider actually implementing tag removal across both tiers, and a silently
/// unimplemented eviction here means a revoked user keeps their access.
/// </para>
/// <para>
/// The version is read from Redis on every check, which is the cost of making revocation take
/// effect immediately rather than at the end of a TTL. It trades one Postgres round trip — often
/// two — for one Redis GET, and keeps the security-critical direction exact.
/// </para>
/// </remarks>
public sealed class CachingRbacService : IRbacService
{
    private readonly IRbacService _inner;
    private readonly HybridCache _cache;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<CachingRbacService> _logger;

    /// <summary>
    /// Bumped when the cached shape or its meaning changes. A deploy that changes how a decision is
    /// computed must not read entries written by the previous one.
    /// </summary>
    private const string KeyVersion = "v2";

    private static readonly HybridCacheEntryOptions DecisionOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromSeconds(30)
    };

    public CachingRbacService(
        IRbacService inner,
        HybridCache cache,
        IConnectionMultiplexer redis,
        ILogger<CachingRbacService> logger)
    {
        _inner = inner;
        _cache = cache;
        _redis = redis;
        _logger = logger;
    }

    public async Task<bool> HasRoleAsync(
        Guid userId,
        RoleType minimumRole,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
    {
        long version;

        try
        {
            version = await GetVersionAsync(userId);
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            // Without a readable version there is no way to know whether a cached decision is
            // current, so bypass the cache entirely rather than risk serving a revoked permission.
            _logger.LogWarning(ex,
                "Could not read the permission cache version; falling back to the database.");

            return await _inner.HasRoleAsync(userId, minimumRole, scope, scopeId, ct);
        }

        var key = $"rbac:{KeyVersion}:{userId}:{version}:{scope}:{scopeId}:{minimumRole}";

        return await _cache.GetOrCreateAsync(
            key,
            (Inner: _inner, userId, minimumRole, scope, scopeId),
            static (state, token) => new ValueTask<bool>(
                state.Inner.HasRoleAsync(state.userId, state.minimumRole, state.scope, state.scopeId, token)),
            DecisionOptions,
            cancellationToken: ct);
    }

    // ── Writes ────────────────────────────────────────────────────────────────

    public async Task<RoleAssignment> AssignRoleAsync(
        Guid userId,
        RoleType role,
        RoleScope scope,
        Guid scopeId,
        Guid? assignedBy = null,
        CancellationToken ct = default)
    {
        var assignment = await _inner.AssignRoleAsync(userId, role, scope, scopeId, assignedBy, ct);
        await InvalidateAsync(userId);
        return assignment;
    }

    public async Task RemoveRoleAsync(
        Guid userId,
        RoleType role,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
    {
        await _inner.RemoveRoleAsync(userId, role, scope, scopeId, ct);
        await InvalidateAsync(userId);
    }

    public async Task RemoveAllRolesAsync(
        Guid userId,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
    {
        await _inner.RemoveAllRolesAsync(userId, scope, scopeId, ct);
        await InvalidateAsync(userId);
    }

    // ── Pass-through reads ────────────────────────────────────────────────────
    // Not cached: both return entity instances rather than a decision, and they are called by
    // administrative screens rather than on the hot path.

    public Task<IReadOnlyList<RoleAssignment>> GetUserRolesAsync(Guid userId, CancellationToken ct = default)
        => _inner.GetUserRolesAsync(userId, ct);

    public Task<IReadOnlyList<RoleAssignment>> GetScopeRolesAsync(
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
        => _inner.GetScopeRolesAsync(scope, scopeId, ct);

    // ── Version stamp ─────────────────────────────────────────────────────────

    /// <summary>
    /// The user's current cache generation. Absent means zero — a user nobody has ever revoked
    /// anything from simply starts at generation 0.
    /// </summary>
    private async Task<long> GetVersionAsync(Guid userId)
    {
        var value = await _redis.GetDatabase().StringGetAsync(VersionKey(userId));
        return value.HasValue && value.TryParse(out long version) ? version : 0;
    }

    /// <summary>
    /// Advances the user's generation, orphaning every decision cached about them.
    /// </summary>
    /// <remarks>
    /// Failures are not swallowed. If the version cannot be advanced, previously cached decisions
    /// stay reachable and a revoked user keeps their access until those entries expire — the caller
    /// needs the write to fail rather than to quietly not take effect.
    /// </remarks>
    private async Task InvalidateAsync(Guid userId)
    {
        var version = await _redis.GetDatabase().StringIncrementAsync(VersionKey(userId));

        _logger.LogDebug("Permission cache for user {UserId} advanced to generation {Version}",
            userId, version);
    }

    private static string VersionKey(Guid userId) => $"rbac:{KeyVersion}:ver:{userId}";
}
