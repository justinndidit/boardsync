using BoardSync.Api.Modules.Rbac.Models;

namespace BoardSync.Api.Modules.Rbac.Services;

/// <summary>
/// Turns a user's grants into an answer about one scope. Pure: no I/O, no state, no dependencies.
/// </summary>
/// <remarks>
/// <para>
/// This is where the scope tree is interpreted. The tree, as the schema defines it:
/// </para>
/// <code>
/// Organization
/// └── Team                      Team.OrganizationId
///     ├── Sprint                Sprint.TeamId
///     └── Project               Project.AssignedTeamId  (required, exactly one team per project)
///         ├── Board
///         └── WorkItem
/// </code>
/// <para>
/// Inheritance runs <b>down</b> the tree only. A project role never reaches the project's team —
/// a team can serve several projects, so letting one project's administrator reach the shared team
/// would hand them the sprints of every sibling project.
/// </para>
/// <para>
/// Being pure is what makes this testable without a database and what lets both the cached and
/// uncached paths share one definition of the rules. Anything needing I/O — loading the snapshot,
/// locating a project in the tree — happens before this is called.
/// </para>
/// </remarks>
public static class AccessEvaluator
{
    /// <summary>
    /// The user's effective role at an organization, or null if they hold none.
    /// </summary>
    /// <remarks>
    /// Organizations are the root, so there is nothing above them to inherit from — a direct
    /// assignment is the only way to hold a role here.
    /// </remarks>
    public static RoleType? EffectiveOrganizationRole(AccessSnapshot snapshot, Guid organizationId) =>
        snapshot.OrganizationRoles.TryGetValue(organizationId, out var role) ? role : null;

    /// <summary>
    /// The user's effective role on a team, or null if they hold none.
    /// </summary>
    /// <param name="snapshot">The user's grants.</param>
    /// <param name="teamId">The team being asked about.</param>
    /// <param name="organizationId">
    /// The team's owning organization, or null when it could not be resolved (a team that no longer
    /// exists). Null simply means org-admin inheritance cannot be applied, so the answer falls back
    /// to direct grants.
    /// </param>
    public static RoleType? EffectiveTeamRole(
        AccessSnapshot snapshot,
        Guid teamId,
        Guid? organizationId)
    {
        var best = snapshot.TeamRoles.TryGetValue(teamId, out var direct) ? direct : (RoleType?)null;

        if (organizationId is Guid org && IsOrgAdmin(snapshot, org))
            best = MostPrivileged(best, RoleType.OrgAdmin);

        return best;
    }

    /// <summary>
    /// The user's effective role on a project, or null if they hold none.
    /// </summary>
    /// <param name="snapshot">The user's grants.</param>
    /// <param name="projectId">The project being asked about.</param>
    /// <param name="location">
    /// Where the project sits in the tree, or null when it could not be resolved (a project that no
    /// longer exists). Null falls back to direct project grants only.
    /// </param>
    /// <remarks>
    /// Three ways to hold a role on a project, and the most privileged wins:
    /// a direct project assignment, OrgAdmin of the owning organization, or a grant on the team the
    /// project is assigned to.
    /// </remarks>
    public static RoleType? EffectiveProjectRole(
        AccessSnapshot snapshot,
        Guid projectId,
        ProjectLocation? location)
    {
        var best = snapshot.ProjectRoles.TryGetValue(projectId, out var direct) ? direct : (RoleType?)null;

        if (location is null)
            return best;

        if (IsOrgAdmin(snapshot, location.OrganizationId))
            best = MostPrivileged(best, RoleType.OrgAdmin);

        if (snapshot.TeamRoles.TryGetValue(location.AssignedTeamId, out var teamRole))
            best = MostPrivileged(best, ProjectRoleFromTeamRole(teamRole));

        return best;
    }

    /// <summary>
    /// Whether <paramref name="held"/> satisfies a requirement of <paramref name="minimumRole"/>.
    /// </summary>
    /// <remarks>
    /// Lower <see cref="RoleType"/> value means more privileged, so a role satisfies a requirement
    /// when its value is less than or equal to it. This comparison stays in memory on purpose:
    /// <c>RoleType</c> is persisted with <c>HasConversion&lt;string&gt;()</c>, so the same
    /// comparison in SQL would order the <em>names</em> — under which 'TeamMember' &lt;= 'Reader'
    /// is false and 'Reader' &lt;= 'TeamMember' is true, denying team members read access and
    /// letting readers perform team-member writes.
    /// </remarks>
    public static bool Satisfies(RoleType? held, RoleType minimumRole) =>
        held is RoleType role && (int)role <= (int)minimumRole;

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static bool IsOrgAdmin(AccessSnapshot snapshot, Guid organizationId) =>
        snapshot.OrganizationRoles.TryGetValue(organizationId, out var role) && role == RoleType.OrgAdmin;

    /// <summary>
    /// What a grant on a team confers on that team's projects.
    /// </summary>
    /// <remarks>
    /// Clamped so it can never exceed <see cref="RoleType.TeamMember"/> — contribution, not
    /// administration. Two reasons. A team can serve several projects, so anything higher would let
    /// a grant on the shared team administer every one of them. And nothing today assigns a role
    /// more privileged than TeamMember at team scope, so a row that says otherwise is corrupt data
    /// rather than an intention worth honouring. Raising this ceiling is a deliberate act — it is
    /// what introducing a team-lead role will mean.
    /// </remarks>
    private static RoleType ProjectRoleFromTeamRole(RoleType teamRole) =>
        (int)teamRole < (int)RoleType.TeamMember ? RoleType.TeamMember : teamRole;

    /// <summary>The more privileged of two roles, treating null as "nothing held".</summary>
    private static RoleType MostPrivileged(RoleType? a, RoleType b) =>
        a is RoleType held && (int)held < (int)b ? held : b;
}
