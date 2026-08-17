using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.Backlog.Models;

/// <summary>
/// Tracks the rank of a work item in a project's product backlog.
/// One row per work item — when an item is assigned to a sprint this row stays
/// so rank is preserved if the item is returned to the backlog later.
/// </summary>
public class BacklogItem : BaseEntity
{
    public Guid ProjectId { get; set; }
    public Guid WorkItemId { get; set; }

    /// <summary>
    /// Optional team filter — null means the item appears in every team's backlog view
    /// for this project. Set when the item is explicitly scoped to a team.
    /// </summary>
    public Guid? TeamId { get; set; }

    /// <summary>
    /// Fractional sort key within the project backlog — lower sorts higher.
    /// </summary>
    /// <remarks>
    /// Fractional rather than a 0-based index, matching <c>SprintWorkItem.Rank</c> and computed by
    /// the same <c>Ranking</c> helper. Consecutive integers force a reorder to rewrite every row,
    /// which makes two people dragging different cards silently revert each other — the product
    /// backlog is the most concurrently-edited list in the system, so it wants this most.
    /// </remarks>
    public decimal Rank { get; set; }

    /// <summary>
    /// When set the item has been pulled into a sprint and should be hidden from
    /// the unscheduled backlog view.
    /// </summary>
    public Guid? SprintId { get; set; }
}
