using StackExchange.Redis;

namespace BoardSync.Api.Modules.Sprints.Services;

/// <summary>
/// Generation counter for a project's board snapshot.
/// </summary>
/// <remarks>
/// <para>
/// A board is assembled from four tables — board, columns, the project's active sprint, and
/// that sprint's work items with their tags — so almost anything in a project can invalidate it.
/// Trying to delete the right cache keys after each write means re-deriving which keys those were,
/// which is more work and more ways to be subtly wrong than simply making the old ones unreachable.
/// </para>
/// <para>
/// So the version goes <em>in the key</em>. Bumping it orphans every snapshot of that project at
/// once, atomically, and the orphans expire on their own. There is no delete storm, and no race
/// where an invalidation lands before the write it was meant to invalidate.
/// </para>
/// </remarks>
public interface IBoardCacheVersion
{
    /// <summary>Current generation for a project. Absent means zero.</summary>
    Task<long> GetAsync(Guid projectId);

    /// <summary>Advances the generation, orphaning every cached snapshot of this project.</summary>
    Task BumpAsync(Guid projectId);
}

/// <inheritdoc />
public class BoardCacheVersion : IBoardCacheVersion
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<BoardCacheVersion> _logger;

    private const string KeyVersion = "v1";

    public BoardCacheVersion(IConnectionMultiplexer redis, ILogger<BoardCacheVersion> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<long> GetAsync(Guid projectId)
    {
        try
        {
            var value = await _redis.GetDatabase().StringGetAsync(Key(projectId));
            return value.HasValue && value.TryParse(out long version) ? version : 0;
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            // Without a readable generation there is no way to know whether a cached snapshot is
            // current. Returning a value nobody has cached under forces a miss, which costs a
            // database read and never serves a stale board.
            _logger.LogWarning(ex, "Board cache version unreadable for {ProjectId}; bypassing the cache.",
                projectId);

            return -DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    public async Task BumpAsync(Guid projectId)
    {
        try
        {
            await _redis.GetDatabase().StringIncrementAsync(Key(projectId));
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            // Logged loudly: a failed bump means readers keep seeing the previous snapshot until it
            // expires, which is the one way this design can show stale data.
            _logger.LogError(ex,
                "Failed to advance board cache version for {ProjectId}; " +
                "cached boards may be stale until they expire.", projectId);
        }
    }

    private static string Key(Guid projectId) => $"board:{KeyVersion}:ver:{projectId}";
}
