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

    /// <summary>0-based display rank within the project backlog (lower = higher priority).</summary>
    public int Rank { get; set; }

    /// <summary>
    /// When set the item has been pulled into a sprint and should be hidden from
    /// the unscheduled backlog view.
    /// </summary>
    public Guid? SprintId { get; set; }
}
