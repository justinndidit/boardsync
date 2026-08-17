using System.Collections.Concurrent;

namespace BoardSync.Api.Shared.Realtime;

/// <summary>
/// Which connections this instance is holding, and what each of them is subscribed to.
/// </summary>
/// <remarks>
/// <para>
/// SignalR offers no way to enumerate connections or their groups, so anything that needs to act on
/// existing subscriptions — re-checking that they are still allowed, for instance — has to keep its
/// own record.
/// </para>
/// <para>
/// Deliberately per-instance and in-memory. A connection lives on exactly one instance, so the
/// instance holding it is the only one that can drop it from a group; there is nothing to share.
/// Instances are told <em>when</em> to re-check over Redis, but each acts on its own connections.
/// </para>
/// </remarks>
public interface IConnectionRegistry
{
    /// <summary>Records a new connection and who it belongs to.</summary>
    void Track(string connectionId, Guid userId);

    /// <summary>Forgets a connection and everything it was subscribed to.</summary>
    void Forget(string connectionId);

    /// <summary>Notes that a connection joined a topic.</summary>
    void AddTopic(string connectionId, string topic);

    /// <summary>Notes that a connection left a topic.</summary>
    void RemoveTopic(string connectionId, string topic);

    /// <summary>This instance's connections for one user, with their current topics.</summary>
    IReadOnlyList<TrackedConnection> ForUser(Guid userId);

    /// <summary>Every connection this instance is holding.</summary>
    IReadOnlyList<TrackedConnection> All();
}

/// <summary>A live connection and what it is currently watching.</summary>
public record TrackedConnection(string ConnectionId, Guid UserId, IReadOnlyList<string> Topics);

/// <inheritdoc />
public class ConnectionRegistry : IConnectionRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _connections = new();

    private sealed record Entry(Guid UserId, ConcurrentDictionary<string, byte> Topics);

    public void Track(string connectionId, Guid userId) =>
        _connections[connectionId] = new Entry(userId, new ConcurrentDictionary<string, byte>());

    public void Forget(string connectionId) => _connections.TryRemove(connectionId, out _);

    public void AddTopic(string connectionId, string topic)
    {
        if (_connections.TryGetValue(connectionId, out var entry))
            entry.Topics[topic] = 0;
    }

    public void RemoveTopic(string connectionId, string topic)
    {
        if (_connections.TryGetValue(connectionId, out var entry))
            entry.Topics.TryRemove(topic, out _);
    }

    public IReadOnlyList<TrackedConnection> ForUser(Guid userId) =>
        _connections
            .Where(kv => kv.Value.UserId == userId)
            .Select(kv => new TrackedConnection(kv.Key, kv.Value.UserId, kv.Value.Topics.Keys.ToList()))
            .ToList();

    public IReadOnlyList<TrackedConnection> All() =>
        _connections
            .Select(kv => new TrackedConnection(kv.Key, kv.Value.UserId, kv.Value.Topics.Keys.ToList()))
            .ToList();
}
