namespace BoardSync.Api.Modules.Rbac.Models;

/// <summary>
/// Roles available in the system, ordered from most to least privileged within a scope.
/// </summary>
public enum RoleType
{
    /// <summary>Can manage the entire organization: create/delete projects, manage all members.</summary>
    OrgAdmin = 10,

    /// <summary>Can manage a project: configure board, manage team members, create iterations.</summary>
    ProjectAdmin = 20,

    /// <summary>Full contributor on a team: create/edit/move work items, manage sprints.</summary>
    TeamMember = 30,

    /// <summary>Read-only stakeholder: can view boards, backlogs, and reports but cannot mutate.</summary>
    Reader = 40
}

/// <summary>
/// The scope at which a role assignment applies.
/// </summary>
public enum RoleScope
{
    Organization,
    Project,
    Team
}
