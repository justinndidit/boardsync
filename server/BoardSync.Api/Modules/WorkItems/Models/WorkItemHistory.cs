using BoardSync.Api.Modules.Rbac.Models;
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

    /// <summary>The principal that made the change.</summary>
    public Guid ChangedBy { get; set; }

    /// <summary>
    /// Whether a person or an integration made this change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things need it. The feed renders "moved to In Review by GitHub" differently from the same
    /// sentence about a colleague; and the git transition rules have to know whether a human has
    /// overridden the board since an event happened, which is unanswerable if every actor looks the
    /// same.
    /// </para>
    /// <para>
    /// Defaults to <c>User</c>, so every row written before integrations existed reads correctly.
    /// </para>
    /// </remarks>
    public PrincipalType ActorType { get; set; } = PrincipalType.User;

    /// <summary>
    /// The person an integration was acting on behalf of, when one can be identified.
    /// </summary>
    /// <remarks>
    /// <b>Attribution, never authority.</b> What the integration may do comes from its own grant;
    /// this is only who to name in the feed. Null when the commit author matches no BoardSync user,
    /// which is normal — external contributors and bots both commit.
    /// </remarks>
    public Guid? AttributedToUserId { get; set; }

    /// <summary>Name of the field that changed (e.g., "State", "AssigneeId", "Title").</summary>
    public string FieldName { get; set; } = string.Empty;

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    // Navigation
    public virtual WorkItem WorkItem { get; set; } = null!;
}
