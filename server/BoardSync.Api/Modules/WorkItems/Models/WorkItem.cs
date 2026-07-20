using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.WorkItems.Models;

/// <summary>
/// Work item types supported in the system (Epic → Feature → Story → Task/Bug).
/// </summary>
public enum WorkItemType
{
    Epic,
    Feature,
    UserStory,
    Task,
    Bug
}

/// <summary>
/// Fixed state machine for MVP: New → Active → Resolved → Closed.
/// </summary>
public enum WorkItemState
{
    New,
    Active,
    Resolved,
    Closed
}

/// <summary>
/// Priority levels for a work item.
/// </summary>
public enum WorkItemPriority
{
    Critical = 1,
    High = 2,
    Medium = 3,
    Low = 4
}

/// <summary>
/// A trackable unit of work scoped to a project.
/// Supports the full hierarchy: Epic → Feature → Story → Task/Bug.
/// </summary>
public class WorkItem : BaseEntity
{
    public Guid ProjectId { get; set; }

    /// <summary>Optional team scope (for board/sprint assignment).</summary>
    public Guid? TeamId { get; set; }

    /// <summary>Optional parent work item (for hierarchy).</summary>
    public Guid? ParentId { get; set; }

    public WorkItemType Type { get; set; }
    public WorkItemState State { get; set; } = WorkItemState.New;
    public WorkItemPriority Priority { get; set; } = WorkItemPriority.Medium;

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>User ID of the assigned user (nullable — no cross-module nav property).</summary>
    public Guid? AssigneeId { get; set; }

    /// <summary>Story points / effort estimate.</summary>
    public int? StoryPoints { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation
    public virtual WorkItem? Parent { get; set; }
    public virtual ICollection<WorkItem> Children { get; set; } = new List<WorkItem>();
    public virtual ICollection<WorkItemComment> Comments { get; set; } = new List<WorkItemComment>();
    public virtual ICollection<WorkItemHistory> History { get; set; } = new List<WorkItemHistory>();
    public virtual ICollection<WorkItemLink> LinksFrom { get; set; } = new List<WorkItemLink>();
    public virtual ICollection<WorkItemLink> LinksTo { get; set; } = new List<WorkItemLink>();
    public virtual ICollection<WorkItemTag> Tags { get; set; } = new List<WorkItemTag>();
}
