using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.WorkItems.Models;

/// <summary>
/// Immutable audit trail entry recording a field change on a work item.
/// </summary>
public class WorkItemHistory : BaseEntity
{
    public Guid WorkItemId { get; set; }

    /// <summary>
    /// The owning project, copied from the work item at write time.
    /// </summary>
    /// <remarks>
    /// Denormalized on purpose. The workspace notification feed asks for "the most recent changes
    /// across these projects, newest first", and reaching the project through
    /// <see cref="WorkItem"/> makes that a join whose sort no index can serve — it degrades with
    /// total history volume forever. Carrying the project here lets one composite index answer the
    /// filter and the ordering together. Work items never move between projects, so the copy cannot
    /// drift from its source.
    /// </remarks>
    public Guid ProjectId { get; set; }

    /// <summary>User who made the change.</summary>
    public Guid ChangedBy { get; set; }

    /// <summary>Name of the field that changed (e.g., "State", "AssigneeId", "Title").</summary>
    public string FieldName { get; set; } = string.Empty;

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    // Navigation
    public virtual WorkItem WorkItem { get; set; } = null!;
}
