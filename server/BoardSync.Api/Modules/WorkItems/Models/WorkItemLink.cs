using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Metadata;

namespace BoardSync.Api.Modules.WorkItems.Models;

/// <summary>
/// Relationship type between two linked work items.
/// </summary>
public enum WorkItemLinkType
{
    /// <summary>Source blocks target from progressing.</summary>
    [DisplayMetadata("Blocks", 10, Inverse = "Blocked by")]
    Blocks,

    /// <summary>Items are related without a dependency direction.</summary>
    [DisplayMetadata("Related to", 20, Inverse = "Related to")]
    RelatedTo,

    /// <summary>Source duplicates target.</summary>
    [DisplayMetadata("Duplicates", 30, Inverse = "Duplicated by")]
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
