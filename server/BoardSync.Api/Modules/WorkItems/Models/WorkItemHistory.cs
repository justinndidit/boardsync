using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.WorkItems.Models;

/// <summary>
/// Immutable audit trail entry recording a field change on a work item.
/// </summary>
public class WorkItemHistory : BaseEntity
{
    public Guid WorkItemId { get; set; }

    /// <summary>User who made the change.</summary>
    public Guid ChangedBy { get; set; }

    /// <summary>Name of the field that changed (e.g., "State", "AssigneeId", "Title").</summary>
    public string FieldName { get; set; } = string.Empty;

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    // Navigation
    public virtual WorkItem WorkItem { get; set; } = null!;
}
