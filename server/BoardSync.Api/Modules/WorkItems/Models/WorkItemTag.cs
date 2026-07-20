using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.WorkItems.Models;

/// <summary>
/// A label/tag attached to a work item. Stored per-item (not a shared tag registry for MVP).
/// </summary>
public class WorkItemTag : BaseEntity
{
    public Guid WorkItemId { get; set; }
    public string Name { get; set; } = string.Empty;

    // Navigation
    public virtual WorkItem WorkItem { get; set; } = null!;
}
