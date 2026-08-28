using BoardSync.Api.Modules.WorkItems.Domain;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.GitSync.Domain;

/// <summary>
/// How far forward a git event may carry a work item, and by which hops.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>GitTransitionService</c> because it is the rule, not the plumbing — a pure
/// function over the workflow, and the part most likely to be wrong in a way nothing else notices.
/// </para>
/// <para>
/// It exists because a single-hop check stranded work. A pull request opening on an item still in
/// <c>New</c> was refused — <c>New</c> reaches only <c>Active</c> in one step — and the refusal left
/// the item in <c>New</c>, so every later event for it failed identically. One missed push, and the
/// item never moved again: a branch pushed before the repository was linked, a work item created
/// after the branch, a delivery that failed while the integration held no grant.
/// </para>
/// <para>
/// The monotonic invariant forbids moving an item <i>backwards</i>. It never said an event may not
/// carry one more than a single state forward, and an event reporting a review is also evidence the
/// work was started.
/// </para>
/// </remarks>
public static class TransitionPath
{
    /// <summary>
    /// Where a state sits in the workflow.
    /// </summary>
    /// <remarks>
    /// A separate ordering from the enum's own values, and deliberately so — this is the one place
    /// states are compared rather than matched, and burying that in the enum's numbering would make
    /// it look like the numbers mean something everywhere else. They do not; only this orders them.
    /// </remarks>
    public static int Rank(WorkItemState state) => state switch
    {
        WorkItemState.New => 0,
        WorkItemState.Active => 1,
        WorkItemState.InReview => 2,
        WorkItemState.Resolved => 3,
        WorkItemState.Closed => 4,
        _ => 0
    };

    /// <summary>
    /// The legal forward hops carrying an item from one state to another; empty when none do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Breadth-first over <see cref="WorkItemStateMachine.AllowedFrom"/>, restricted to hops that
    /// increase <see cref="Rank"/> and never overshoot the destination. Shortest-path is what stops
    /// the walk inventing history.
    /// </para>
    /// <para>
    /// <b>An intermediate state is never fabricated where the workflow allows skipping it.</b>
    /// <c>Active → Resolved</c> is a legal single hop — work that needed no pull request — so a
    /// merge on an Active item records exactly that, not a detour through <c>InReview</c> that
    /// nobody did. Cycle time is reconstructed from these rows, and a fabricated "reached In Review"
    /// would put a figure on a review that never happened.
    /// </para>
    /// <para>
    /// The hops a walk does record share one timestamp. That is honest rather than precise: reaching
    /// <c>InReview</c> from <c>New</c> tells us the work was started and reviewed, and nothing about
    /// when it was started. The alternative was leaving it in <c>New</c> forever.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<WorkItemState> Forward(WorkItemState from, WorkItemState to)
    {
        if (from == to) return [];

        var queue = new Queue<WorkItemState>();
        var cameFrom = new Dictionary<WorkItemState, WorkItemState>();

        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var next in WorkItemStateMachine.AllowedFrom(current))
            {
                // The monotonic rule applies to each hop, not only to the net move — and a route
                // past the destination is not a route to it.
                if (Rank(next) <= Rank(current) || Rank(next) > Rank(to)) continue;

                if (!cameFrom.TryAdd(next, current)) continue;

                if (next == to)
                {
                    var path = new List<WorkItemState>();

                    for (var state = to; state != from; state = cameFrom[state])
                        path.Add(state);

                    path.Reverse();

                    return path;
                }

                queue.Enqueue(next);
            }
        }

        return [];
    }
}
