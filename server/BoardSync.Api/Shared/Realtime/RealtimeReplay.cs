using System.Text.Json;
using BoardSync.Api.Data;
using BoardSync.Api.Shared.Kernel.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BoardSync.Api.Shared.Realtime;

/// <summary>
/// Catches a reconnecting client up on what it missed.
/// </summary>
/// <remarks>
/// The outbox is already a durable, ordered, per-topic log of everything that happened, so replay
/// reads from it directly. No second event store is needed, and there is no way for the replayed
/// history to disagree with what was originally sent — it is the same rows.
/// </remarks>
public interface IRealtimeReplay
{
    /// <summary>The newest sequence in the log. A client with this value is fully caught up.</summary>
    Task<long> GetCurrentSequenceAsync(CancellationToken ct = default);

    /// <summary>
    /// Messages on a topic after <paramref name="afterSequence"/>, oldest first.
    /// </summary>
    /// <returns>
    /// The missed messages, or <c>null</c> when the gap exceeds the replay bound and the client
    /// must refetch instead.
    /// </returns>
    Task<IReadOnlyList<RealtimeMessage>?> GetMissedAsync(
        string topic,
        long afterSequence,
        CancellationToken ct = default);
}

/// <inheritdoc />
public class RealtimeReplay : IRealtimeReplay
{
    private readonly BoardSyncDbContext _context;
    private readonly RealtimeSettings _settings;
    private readonly ILogger<RealtimeReplay> _logger;

    public RealtimeReplay(
        BoardSyncDbContext context,
        IOptions<RealtimeSettings> settings,
        ILogger<RealtimeReplay> logger)
    {
        _context = context;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<long> GetCurrentSequenceAsync(CancellationToken ct = default) =>
        await _context.OutboxMessages
            .OrderByDescending(m => m.Sequence)
            .Select(m => m.Sequence)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<RealtimeMessage>?> GetMissedAsync(
        string topic,
        long afterSequence,
        CancellationToken ct = default)
    {
        // Ask for one more than the bound. If it comes back, the gap is too wide — which is cheaper
        // to learn than counting the whole backlog first.
        var probe = _settings.MaxReplayMessages + 1;

        var rows = await _context.OutboxMessages
            .Where(m => m.Sequence > afterSequence && m.Topics.Contains(topic))
            .OrderBy(m => m.Sequence)
            .Take(probe)
            .ToListAsync(ct);

        if (rows.Count > _settings.MaxReplayMessages)
        {
            _logger.LogDebug(
                "Replay for {Topic} from {AfterSequence} exceeds {Max} messages; asking client to resync.",
                topic, afterSequence, _settings.MaxReplayMessages);

            return null;
        }

        // A client whose resume point predates retention would otherwise be told "nothing missed"
        // when the truth is "everything you missed has been deleted". Detect that by checking
        // whether its position still exists in the log at all.
        if (rows.Count == 0 && afterSequence > 0)
        {
            var oldestRetained = await _context.OutboxMessages
                .OrderBy(m => m.Sequence)
                .Select(m => m.Sequence)
                .FirstOrDefaultAsync(ct);

            if (oldestRetained > afterSequence + 1)
            {
                _logger.LogDebug(
                    "Replay for {Topic} from {AfterSequence} predates retention (oldest {Oldest}); resync.",
                    topic, afterSequence, oldestRetained);

                return null;
            }
        }

        return rows.Select(m => new RealtimeMessage(
            m.Sequence,
            topic,
            m.EventType,
            JsonDocument.Parse(m.Payload).RootElement.Clone(),
            m.OccurredAt)).ToList();
    }
}
