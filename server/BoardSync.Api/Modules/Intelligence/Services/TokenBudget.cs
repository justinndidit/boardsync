using System.Collections.Concurrent;

namespace BoardSync.Api.Modules.Intelligence.Services;

/// <summary>
/// How much an organization may spend on narration.
/// </summary>
/// <remarks>
/// A ceiling per organization rather than per user or per request: the cost is the organization's,
/// and a per-request cap does nothing to stop somebody refreshing a report a thousand times.
/// </remarks>
public interface ITokenBudget
{
    /// <summary>Whether this organization has allowance left in the current period.</summary>
    Task<bool> HasRemainingAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>Charges tokens against the allowance.</summary>
    Task RecordAsync(Guid organizationId, int tokens, CancellationToken ct = default);

    /// <summary>What remains, for reporting it back to an administrator.</summary>
    Task<int> RemainingAsync(Guid organizationId, CancellationToken ct = default);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// <b>In memory, and deliberately so for now.</b> The allowance resets daily, is per instance, and
/// is lost on restart — so with several instances the effective ceiling is the limit times the
/// instance count, and a restart forgives the day's spend.
/// </para>
/// <para>
/// That is an acceptable shape for a cost guard and an unacceptable one for a quota somebody has
/// paid for. It is written down here rather than discovered later: making it exact means a row per
/// organization per period and a transaction around the read-modify-write, which is worth doing the
/// moment narration is something customers are billed for and is not worth doing before then.
/// </para>
/// </remarks>
public sealed class InMemoryTokenBudget : ITokenBudget
{
    private readonly record struct Period(Guid OrganizationId, DateOnly Day);

    private readonly ConcurrentDictionary<Period, int> _spent = new();
    private readonly int _dailyLimit;

    public InMemoryTokenBudget(IConfiguration configuration)
    {
        // Generous by default: a narrative is a couple of thousand tokens, so this is hundreds of
        // reports a day. It exists to stop a runaway loop, not to ration ordinary use.
        _dailyLimit = configuration.GetValue("Intelligence:DailyTokenLimit", 500_000);
    }

    private static Period Today(Guid organizationId) =>
        new(organizationId, DateOnly.FromDateTime(DateTime.UtcNow));

    public Task<bool> HasRemainingAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult(_spent.GetValueOrDefault(Today(organizationId)) < _dailyLimit);

    public Task RecordAsync(Guid organizationId, int tokens, CancellationToken ct = default)
    {
        if (tokens > 0)
            _spent.AddOrUpdate(Today(organizationId), tokens, (_, used) => used + tokens);

        return Task.CompletedTask;
    }

    public Task<int> RemainingAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult(Math.Max(
            0, _dailyLimit - _spent.GetValueOrDefault(Today(organizationId))));
}
