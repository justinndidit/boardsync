using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.Reporting.Domain;

/// <summary>One recorded state change, stripped to what a timeline needs.</summary>
/// <param name="WorkItemId">The item.</param>
/// <param name="To">The state it moved to.</param>
/// <param name="At">When.</param>
public readonly record struct StateChange(Guid WorkItemId, WorkItemState To, DateTime At);

/// <summary>
/// Reconstructs how long one work item spent in each part of the workflow.
/// </summary>
/// <remarks>
/// <para>
/// Pure, so the arithmetic is testable without a database — the same reason
/// <c>AccessEvaluator</c> and <c>WorkItemStateMachine</c> are. What it measures is when the board
/// says something happened, which is a stronger claim here than in most trackers: the board moves
/// itself from git, so these timestamps are pushes and merges rather than somebody remembering to
/// drag a card at the end of the day.
/// </para>
/// <para>
/// <b>First entry into a state, not last.</b> Work bounces — a pull request gets changes requested,
/// QA sends something back — and measuring the last entry would report the final attempt rather than
/// the elapsed time. "How long until somebody started this" means the first time anybody did.
/// </para>
/// </remarks>
public static class StateTimeline
{
    /// <summary>The spans one item spent in each stage, in hours.</summary>
    /// <param name="Pickup">New → Active.</param>
    /// <param name="Development">Active → Resolved.</param>
    /// <param name="VerificationWait">Resolved → Closed.</param>
    /// <param name="Total">First recorded state → Closed.</param>
    public readonly record struct Spans(
        double? Pickup, double? Development, double? VerificationWait, double? Total);

    /// <summary>
    /// Measures one item from its ordered state changes.
    /// </summary>
    /// <remarks>
    /// Only items that reached <c>Closed</c> are measured. An open item has no end, and including it
    /// with "so far" as its duration would make cycle time fall every time somebody creates a work
    /// item.
    /// </remarks>
    public static Spans? Measure(IReadOnlyList<StateChange> changes)
    {
        if (changes.Count == 0) return null;

        DateTime? First(WorkItemState state)
        {
            foreach (var change in changes)
                if (change.To == state)
                    return change.At;

            return null;
        }

        if (First(WorkItemState.Closed) is not { } closed) return null;

        var created = changes[0].At;
        var active = First(WorkItemState.Active);
        var resolved = First(WorkItemState.Resolved);

        return new Spans(
            Pickup: Hours(created, active),
            Development: Hours(active, resolved),
            VerificationWait: Hours(resolved, closed),
            Total: Hours(created, closed));
    }

    /// <summary>
    /// The middle value, or null when there is nothing to take a middle of.
    /// </summary>
    /// <remarks>
    /// A median rather than a mean, because cycle-time distributions are reliably skewed: one item
    /// that sat in a backlog for three months drags an average somewhere nobody recognises, and a
    /// figure nobody recognises gets ignored.
    /// </remarks>
    public static double? Median(IEnumerable<double?> values)
    {
        var sorted = values.Where(v => v.HasValue).Select(v => v!.Value).Order().ToList();

        if (sorted.Count == 0) return null;

        var middle = sorted.Count / 2;

        var median = sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;

        // Rounded here rather than at the edge, so every caller reports the same precision and
        // nobody compares two figures that were rounded differently.
        return Math.Round(median, 2);
    }

    /// <remarks>
    /// A negative span means the history is out of order — possible, since git events can arrive
    /// late and be recorded with the timestamp of the event rather than of the write. Reported as
    /// null rather than negative: "we cannot tell" is honest, a negative duration is not.
    /// </remarks>
    private static double? Hours(DateTime? from, DateTime? to)
    {
        if (from is not { } start || to is not { } end) return null;

        var hours = (end - start).TotalHours;

        return hours < 0 ? null : hours;
    }
}
