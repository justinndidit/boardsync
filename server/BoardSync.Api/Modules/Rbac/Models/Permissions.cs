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

    public const string OrgRead = "org:read";
    public const string OrgAdmin = "org:admin";
    public const string OrgMemberManage = "org:member:manage";

    // ── Team ──────────────────────────────────────────────────────────────────

    public const string TeamRead = "team:read";

    /// <summary>Rename or archive the team.</summary>
    public const string TeamManage = "team:manage";

    /// <summary>Add and remove team members.</summary>
    public const string TeamMemberManage = "team:member:manage";

    /// <summary>Appoint, transfer and vacate the team's positions.</summary>
    public const string TeamRoleAssign = "team:role:assign";

    // ── Sprint ────────────────────────────────────────────────────────────────

    public const string SprintRead = "sprint:read";

    /// <summary>Create, update, start, complete and delete sprints.</summary>
    public const string SprintManage = "sprint:manage";

    /// <summary>
    /// Decide what a sprint contains. Distinct from <see cref="SprintOrder"/> because what is in a
    /// sprint is a commitment, while how it is ordered is execution.
    /// </summary>
    public const string SprintScope = "sprint:scope";

    /// <summary>Move and reorder items within a sprint.</summary>
    public const string SprintOrder = "sprint:order";

    // ── Project ───────────────────────────────────────────────────────────────

    public const string ProjectRead = "project:read";

    /// <summary>Rename, archive, and reassign the project's team.</summary>
    public const string ProjectAdmin = "project:admin";

    public const string ProjectMemberManage = "project:member:manage";

    public const string BoardRead = "board:read";
    public const string BoardConfigure = "board:configure";

    public const string WorkItemRead = "workitem:read";
    public const string WorkItemWrite = "workitem:write";
    public const string WorkItemComment = "workitem:comment";
    public const string WorkItemDelete = "workitem:delete";
}
