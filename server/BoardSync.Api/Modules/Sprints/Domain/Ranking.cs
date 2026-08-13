namespace BoardSync.Api.Modules.Sprints.Domain;

/// <summary>
/// Computes fractional sort keys for backlog ordering.
/// </summary>
/// <remarks>
/// The whole point is that placing an item never requires touching its neighbours. Given the ranks
/// either side of a target position, the new rank is simply a value between them.
/// </remarks>
public static class Ranking
{
    /// <summary>Distance between ranks when appending, and the rank of the first item.</summary>
    public const decimal Step = 1024m;

    /// <summary>
    /// Below this gap, repeated midpoints are close enough to exhausting <c>decimal</c> precision
    /// that the backlog should be rebalanced.
    /// </summary>
    public const decimal MinimumGap = 0.0000001m;

    /// <summary>
    /// A rank that sorts between <paramref name="before"/> and <paramref name="after"/>.
    /// </summary>
    /// <param name="before">Rank of the item above the target slot, or null when moving to the top.</param>
    /// <param name="after">Rank of the item below the target slot, or null when moving to the end.</param>
    /// <remarks>
    /// Both null means an empty backlog. Open ends step by <see cref="Step"/> rather than halving
    /// toward zero, so repeatedly moving items to the top does not converge on a floor.
    /// </remarks>
    public static decimal Between(decimal? before, decimal? after) => (before, after) switch
    {
        (null, null) => Step,
        (null, { } first) => first - Step,
        ({ } last, null) => last + Step,
        ({ } lo, { } hi) => lo + (hi - lo) / 2m
    };

    /// <summary>
    /// Whether the gap between two adjacent ranks has collapsed far enough that further
    /// subdivision risks losing precision, and the backlog should be renumbered.
    /// </summary>
    public static bool NeedsRebalance(decimal? before, decimal? after) =>
        before is { } lo && after is { } hi && Math.Abs(hi - lo) < MinimumGap;

    /// <summary>Evenly spaced ranks for renumbering a whole backlog.</summary>
    public static decimal RankAt(int index) => (index + 1) * Step;
}
