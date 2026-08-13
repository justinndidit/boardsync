using StackExchange.Redis;

namespace BoardSync.Api.Shared.Realtime;

/// <summary>
/// Who is currently looking at a topic.
/// </summary>
/// <remarks>
/// <para>
/// Redis only — presence is not worth a database write. It is the most disposable state in the
/// system: losing all of it costs a stale avatar row for a few seconds.
/// </para>
/// <para>
/// Membership is a sorted set scored by the time each user was last seen, rather than a plain set
/// of who joined. That difference matters: a browser tab that is closed, crashes, or drops off wifi
/// never sends a leave, and a plain set would show that person as present forever. Scoring by
/// timestamp lets stale entries be dropped on read, so presence self-heals without depending on
/// clients behaving well.
/// </para>
/// </remarks>
public interface IPresenceTracker
{
    /// <summary>Records that a user is watching a topic, or refreshes them if already there.</summary>
    /// <returns>True if this was a new arrival, false if it only refreshed an existing one.</returns>
    Task<bool> JoinAsync(string topic, Guid userId);

    /// <summary>Removes a user from a topic. Removing someone not present is not an error.</summary>
    /// <returns>True if they were actually present.</returns>
    Task<bool> LeaveAsync(string topic, Guid userId);

    /// <summary>Everyone seen on a topic within the freshness window.</summary>
    Task<IReadOnlyList<Guid>> GetPresentAsync(string topic);
}

/// <inheritdoc />
public class PresenceTracker : IPresenceTracker
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<PresenceTracker> _logger;

    /// <summary>
    /// How long a user counts as present without being seen again. Clients refresh well inside
    /// this, so it only comes into play when one disappears without saying goodbye.
    /// </summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(90);

    public PresenceTracker(IConnectionMultiplexer redis, ILogger<PresenceTracker> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<bool> JoinAsync(string topic, Guid userId)
    {
        try
        {
            var db = _redis.GetDatabase();
            var key = Key(topic);

            // SortedSetAdd returns true only when the member is new, which is exactly the signal
            // needed to decide whether this is worth broadcasting — a heartbeat should not tell
            // everyone that somebody "arrived" every thirty seconds.
            var isNew = await db.SortedSetAddAsync(key, userId.ToString(), Now());

            // The whole key expires if the topic goes quiet, so abandoned topics do not accumulate.
            await db.KeyExpireAsync(key, Freshness + TimeSpan.FromMinutes(5));

            return isNew;
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            _logger.LogWarning(ex, "Presence join failed for {Topic}; continuing without presence.", topic);
            return false;
        }
    }

    public async Task<bool> LeaveAsync(string topic, Guid userId)
    {
        try
        {
            return await _redis.GetDatabase().SortedSetRemoveAsync(Key(topic), userId.ToString());
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            _logger.LogWarning(ex, "Presence leave failed for {Topic}.", topic);
            return false;
        }
    }

    public async Task<IReadOnlyList<Guid>> GetPresentAsync(string topic)
    {
        try
        {
            var db = _redis.GetDatabase();
            var key = Key(topic);

            // Prune before reading, so a stale entry is never reported even once.
            await db.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, Cutoff());

            var members = await db.SortedSetRangeByScoreAsync(key, Cutoff(), double.PositiveInfinity);

            return members
                .Select(m => Guid.TryParse(m.ToString(), out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToList();
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            _logger.LogWarning(ex, "Presence read failed for {Topic}; reporting nobody present.", topic);
            return [];
        }
    }

    private static string Key(string topic) => $"presence:{topic}";

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static double Cutoff() =>
        DateTimeOffset.UtcNow.Subtract(Freshness).ToUnixTimeMilliseconds();
}
