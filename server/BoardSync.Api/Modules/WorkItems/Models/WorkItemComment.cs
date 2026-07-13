using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.WorkItems.Models;

/// <summary>
/// A comment on a work item.
/// </summary>
public class WorkItemComment : BaseEntity
{
    public Guid WorkItemId { get; set; }

    /// <summary>Author user ID.</summary>
    public Guid AuthorId { get; set; }

    public string Body { get; set; } = string.Empty;

    public bool IsEdited { get; set; } = false;

    // Navigation
    public virtual WorkItem WorkItem { get; set; } = null!;
}
