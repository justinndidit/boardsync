using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.WorkItems.Models;

/// <summary>
/// Relationship type between two linked work items.
/// </summary>
public enum WorkItemLinkType
{
    /// <summary>Source blocks target from progressing.</summary>
    Blocks,

    /// <summary>Items are related without a dependency direction.</summary>
    RelatedTo,

    /// <summary>Source duplicates target.</summary>
    Duplicates
}

/// <summary>
/// A directed link between two work items (distinct from the parent-child hierarchy).
/// </summary>
public class WorkItemLink : BaseEntity
{
    public Guid SourceId { get; set; }
    public Guid TargetId { get; set; }
    public WorkItemLinkType LinkType { get; set; }

    // Navigation
    public virtual WorkItem Source { get; set; } = null!;
    public virtual WorkItem Target { get; set; } = null!;
}
