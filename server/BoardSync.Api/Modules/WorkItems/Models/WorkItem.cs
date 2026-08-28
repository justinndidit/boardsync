using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Metadata;

namespace BoardSync.Api.Modules.WorkItems.Models;

/// <summary>
/// Work item types supported in the system (Epic → Feature → Story → Task/Bug).
/// </summary>
public enum WorkItemType
{
    [DisplayMetadata("Epic", 10, Description = "A large body of work spanning several features.")]
    Epic,

    [DisplayMetadata("Feature", 20, Description = "A shippable capability within an epic.")]
    Feature,

    [DisplayMetadata("User Story", 30, Description = "A unit of user-visible value.")]
    UserStory,

    [DisplayMetadata("Task", 40, Description = "A piece of work needed to deliver a story.")]
    Task,

    [DisplayMetadata("Bug", 50, Description = "Something that does not work as intended.")]
    Bug
}

/// <summary>
/// The work item lifecycle: New → Active → InReview → Resolved → Closed.
/// </summary>
/// <remarks>
/// <para>
/// Each state is one a git signal can identify, which is the point: a branch's first commit makes it
/// Active, an opened pull request makes it InReview, and a merge into the default branch makes it
/// Resolved. See build_context.md §4.
/// </para>
/// <para>
/// <b>Resolved means "merged, awaiting test"</b>, not "done" — which is why it is labelled
/// "Awaiting QA" rather than by its enum name. Only <c>workitem:verify</c> moves anything out of it.
/// </para>
/// <para>
/// Stored by name (<c>HasConversion&lt;string&gt;</c>), so inserting a value in the middle is safe:
/// nothing depends on the ordinal.
/// </para>
/// </remarks>
public enum WorkItemState
{
    [DisplayMetadata("New", 10, Group = "Pending", Description = "Created, not yet started.")]
    New,

    [DisplayMetadata("Active", 20, Group = "InProgress", Description = "Being worked on.")]
    Active,

    /// <summary>A pull request is open against this work. Set by the git integration.</summary>
    [DisplayMetadata("In Review", 25, Group = "Review",
        Description = "A pull request is open and awaiting review.")]
    InReview,

    // The label is not the enum name on purpose. "Resolved" says nothing about what happens next;
    // what this state means is that the work is done and waiting on someone to test it, and the
    // person looking at the board needs to know that rather than to learn the vocabulary.
    [DisplayMetadata("Awaiting QA", 30, Group = "Review",
        Description = "Work is complete and waiting to be verified.")]
    Resolved,

    [DisplayMetadata("Closed", 40, Group = "Done", Description = "Verified and finished.")]
    Closed
}

/// <summary>
/// Priority levels for a work item.
/// </summary>
public enum WorkItemPriority
{
    [DisplayMetadata("Critical", 10, Description = "Drop everything.")]
    Critical = 1,

    [DisplayMetadata("High", 20, Description = "Ahead of normal work.")]
    High = 2,

    [DisplayMetadata("Medium", 30, Description = "Normal priority.")]
    Medium = 3,

    [DisplayMetadata("Low", 40, Description = "When there is room.")]
    Low = 4
}

/// <summary>
/// A trackable unit of work scoped to a project.
/// Supports the full hierarchy: Epic → Feature → Story → Task/Bug.
/// </summary>
public class WorkItem : BaseEntity
{
    /// <summary>
    /// Row version, mapped to Postgres' <c>xmin</c> system column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Postgres bumps <c>xmin</c> on every update by itself, so this needs no column, no trigger
    /// and no migration — the value is already there on every row.
    /// </para>
    /// <para>
    /// It exists so that two people editing the same work item cannot silently overwrite each
    /// other. Note that EF's own check only spans load-to-save inside one request, which is not
    /// where the conflict lives: the real race is A reads, B saves, A saves. Closing that needs the
    /// client to send back the version it read — see
    /// <c>IWorkItemService.UpdateAsync</c>.
    /// </para>
    /// </remarks>
    public uint Version { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>
    /// This item's number within its project: the <c>142</c> in <c>BS-142</c>.
    /// </summary>
    /// <remarks>
    /// Per project, not global, so the key carries meaning — <c>BS-1</c> and <c>PAY-1</c> are both a
    /// project's first item, which is what people expect and what makes the key worth typing.
    /// Allocated from <c>Project.NextWorkItemNumber</c> in the creating transaction.
    /// </remarks>
    public int Number { get; set; }

    /// <summary>
    /// Title and description, indexed for full-text search.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A generated column, computed by Postgres and never assigned here — writing to it would be
    /// rejected, and there is no moment where it could go stale.
    /// </para>
    /// <para>
    /// Search matched <c>LOWER(title) LIKE '%term%'</c> before this, which no index can serve: every
    /// search read every work item the caller could see. It also ranked by creation date, so the
    /// best match and the newest were the same answer only by chance.
    /// </para>
    /// </remarks>
    public NpgsqlTypes.NpgsqlTsVector? SearchVector { get; private set; }

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
