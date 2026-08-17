using BoardSync.Api.Data;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using Microsoft.Extensions.Caching.Hybrid;
using StackExchange.Redis;

namespace BoardSync.Api.Modules.Rbac.Services.Implementations;

/// <summary>
/// Caches access snapshots and scope positions, in memory and in Redis.
/// </summary>
/// <remarks>
/// <para>
/// Role assignments are the most-read and least-written data in the system: every authorized action
/// asks, and almost nothing changes.
/// </para>
/// <para>
/// <b>One entry per user, not one per question.</b> The previous design cached a decision under
/// <c>(user, scope, scopeId, minimumRole)</c>, so the same user generated a separate entry for every
/// rank anyone happened to ask about and every scope they touched. Caching the grants instead means
/// one entry per user per generation, and the question is answered from it in memory.
/// </para>
/// <para>
/// <b>Only grants are cached here.</b> A scope's position in the tree is read fresh — it is a
/// primary-key lookup, and the per-request memo above this already collapses repeats within a
/// request. Caching it distributed would be a small saving bought with a real hole: reassigning a
/// project to a different team changes who may see it, and every other instance would keep serving
/// the old parent out of its own L1 until that copy expired, with no way to reach in and evict it.
/// A grant is safe to cache because its generation counter can be advanced atomically; the tree has
/// no such counter, so it is not cached.
/// </para>
/// </remarks>
public sealed class CachingAccessResolver : IAccessResolver
{
    private readonly IAccessResolver _inner;
    private readonly HybridCache _cache;
    private readonly IAccessCacheVersion _version;
    private readonly ITransactionState _transaction;
    private readonly ILogger<CachingAccessResolver> _logger;

    private static readonly HybridCacheEntryOptions SnapshotOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromSeconds(30)
    };

    public CachingAccessResolver(
        IAccessResolver inner,
        HybridCache cache,
        IAccessCacheVersion version,
        ITransactionState transaction,
        ILogger<CachingAccessResolver> logger)
    {
        _inner = inner;
        _cache = cache;
        _version = version;
        _transaction = transaction;
        _logger = logger;
    }

    public async Task<AccessSnapshot> GetSnapshotAsync(Guid userId, CancellationToken ct = default)
    {
        // Never cache inside an explicit transaction. HybridCache invokes its factory outside the
        // ambient execution-strategy scope, and EF refuses to start a retriable operation while a
        // user transaction is open — so a cache miss in that position throws rather than querying.
        // Bypassing is also right on its own merits: code inside a transaction is mid-write, and it
        // wants the current answer rather than a cached one.
        if (_transaction.InTransaction)
            return await _inner.GetSnapshotAsync(userId, ct);

        long version;

        try
        {
            version = await _version.GetAsync(userId);
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            // Without a readable generation there is no way to know whether a cached snapshot is
            // current, so bypass the cache entirely rather than risk honouring a revoked grant.
            _logger.LogWarning(ex,
                "Could not read the access cache generation; falling back to the database.");

            return await _inner.GetSnapshotAsync(userId, ct);
        }

        return await _cache.GetOrCreateAsync(
            $"rbac:{AccessCacheVersion.KeyVersion}:snap:{userId}:{version}",
            (Inner: _inner, userId),
            static (state, token) => new ValueTask<AccessSnapshot>(
                state.Inner.GetSnapshotAsync(state.userId, token)),
            SnapshotOptions,
            cancellationToken: ct);
    }

    // ── Scope tree ────────────────────────────────────────────────────────────
    // Pass-through, deliberately. See the note on the class.

    public Task<ProjectLocation?> GetProjectLocationAsync(Guid projectId, CancellationToken ct = default)
        => _inner.GetProjectLocationAsync(projectId, ct);

    public Task<Guid?> GetTeamOrganizationIdAsync(Guid teamId, CancellationToken ct = default)
        => _inner.GetTeamOrganizationIdAsync(teamId, ct);
}
