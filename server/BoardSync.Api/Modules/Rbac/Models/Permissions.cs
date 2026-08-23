using System.Reflection;
using BoardSync.Api.Shared.Metadata;

namespace BoardSync.Api.Modules.Rbac.Models;

/// <summary>
/// Every capability the system gates on, named.
/// </summary>
/// <remarks>
/// <para>
/// These replaced a rank ladder. A ladder can express "at least a TeamMember" but cannot express
/// "may start a sprint" when the people who may do that — a Scrum Master and a Product Owner — are
/// peers rather than one being above the other. Roles are now bundles of these; see
/// <see cref="RolePermissions"/>.
/// </para>
/// <para>
/// Constants rather than an enum so they can sit in an attribute argument and read the same way in
/// the code as they do in the design doc. The value is what appears in logs, so it is the name that
/// matters, not the identifier.
/// </para>
/// </remarks>
public static class Permissions
{
    // ── Organization ──────────────────────────────────────────────────────────

    [DisplayMetadata("View the organization", 10, Group = "Organization")]
    public const string OrgRead = "org:read";
    [DisplayMetadata("Administer the organization", 20, Group = "Organization")]
    public const string OrgAdmin = "org:admin";
    [DisplayMetadata("Manage organization members", 30, Group = "Organization")]
    public const string OrgMemberManage = "org:member:manage";

    // ── Team ──────────────────────────────────────────────────────────────────

    [DisplayMetadata("View the team", 40, Group = "Team")]
    public const string TeamRead = "team:read";

    /// <summary>Rename or archive the team.</summary>
    [DisplayMetadata("Rename or archive the team", 50, Group = "Team")]
    public const string TeamManage = "team:manage";

    /// <summary>Add and remove team members.</summary>
    [DisplayMetadata("Manage team members", 60, Group = "Team")]
    public const string TeamMemberManage = "team:member:manage";

    /// <summary>Appoint, transfer and vacate the team's positions.</summary>
    [DisplayMetadata("Appoint team positions", 70, Group = "Team")]
    public const string TeamRoleAssign = "team:role:assign";

    // ── Sprint ────────────────────────────────────────────────────────────────

    [DisplayMetadata("View sprints", 80, Group = "Sprint")]
    public const string SprintRead = "sprint:read";

    /// <summary>Create, update, start, complete and delete sprints.</summary>
    [DisplayMetadata("Run the sprint lifecycle", 90, Group = "Sprint")]
    public const string SprintManage = "sprint:manage";

    /// <summary>
    /// Decide what a sprint contains. Distinct from <see cref="SprintOrder"/> because what is in a
    /// sprint is a commitment, while how it is ordered is execution.
    /// </summary>
    [DisplayMetadata("Decide what a sprint commits to", 100, Group = "Sprint")]
    public const string SprintScope = "sprint:scope";

    /// <summary>Move and reorder items within a sprint.</summary>
    [DisplayMetadata("Reorder work within a sprint", 110, Group = "Sprint")]
    public const string SprintOrder = "sprint:order";

    // ── Project ───────────────────────────────────────────────────────────────

    [DisplayMetadata("View the project", 120, Group = "Project")]
    public const string ProjectRead = "project:read";

    /// <summary>Rename, archive, and reassign the project's team.</summary>
    [DisplayMetadata("Administer the project", 130, Group = "Project")]
    public const string ProjectAdmin = "project:admin";

    [DisplayMetadata("Manage project roles", 140, Group = "Project")]
    public const string ProjectMemberManage = "project:member:manage";

    [DisplayMetadata("View the board", 150, Group = "Board")]
    public const string BoardRead = "board:read";
    [DisplayMetadata("Configure board columns", 160, Group = "Board")]
    public const string BoardConfigure = "board:configure";

    [DisplayMetadata("View work items", 170, Group = "Work items")]
    public const string WorkItemRead = "workitem:read";
    [DisplayMetadata("Create and edit work items", 180, Group = "Work items")]
    public const string WorkItemWrite = "workitem:write";
    [DisplayMetadata("Comment on work items", 190, Group = "Work items")]
    public const string WorkItemComment = "workitem:comment";
    [DisplayMetadata("Delete work items", 200, Group = "Work items")]
    public const string WorkItemDelete = "workitem:delete";

    /// <summary>
    /// Certify that finished work meets its acceptance criteria, or send it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only permission that reaches <c>Closed</c>, and the only one that moves work back out of
    /// the QA lane. Deliberately not part of <see cref="WorkItemWrite"/>: writing the code and
    /// declaring it correct are different authorities, and every contributor holds the first.
    /// </para>
    /// <para>
    /// The whole value of the git integration rests on this separation. The integration principal
    /// will hold write and never hold this, so no amount of automation — and no bug in the webhook
    /// handler — can close a work item. The QA gate is a permission the integration lacks, not a rule
    /// it is trusted to follow.
    /// </para>
    /// </remarks>
    [DisplayMetadata("Certify work as done", 210, Group = "Work items")]
    public const string WorkItemVerify = "workitem:verify";

    /// <summary>
    /// Every permission above, in declared display order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reflected over this class's own constants rather than hand-listed, because a hand-listed copy
    /// is the one that falls behind — the same argument <see cref="RolePermissions.AssignableAt"/>
    /// makes about role lists. Adding a constant above adds it here, to
    /// <c>GET /api/metadata</c>, and to what <c>GET /api/me/capabilities</c> reports on, with no
    /// second edit.
    /// </para>
    /// <para>
    /// Computed once at type initialization. It is read per request by the capabilities endpoint,
    /// which evaluates each entry against a snapshot in memory, so it must not be a per-call
    /// reflection walk.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> All =
        typeof(Permissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => new
            {
                Value = (string)f.GetRawConstantValue()!,
                Order = f.GetCustomAttribute<DisplayMetadataAttribute>()?.Order ?? int.MaxValue
            })
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Value, StringComparer.Ordinal)
            .Select(x => x.Value)
            .ToArray();
}
