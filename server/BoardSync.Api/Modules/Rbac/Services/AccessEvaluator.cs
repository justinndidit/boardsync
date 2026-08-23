using BoardSync.Api.Modules.Rbac.Models;

namespace BoardSync.Api.Modules.Rbac.Services;

/// <summary>
/// Decides whether a user's grants permit something at one scope. Pure: no I/O, no state, no
/// dependencies.
/// </summary>
/// <remarks>
/// <para>
/// This is where the scope tree is interpreted. The tree, as the schema defines it:
/// </para>
/// <code>
/// Organization
/// └── Team                      Team.OrganizationId
///     └── Project               Project.AssignedTeamId  (required, exactly one team per project)
///         ├── Board
///         ├── Sprint            Sprint.ProjectId
///         └── WorkItem
/// </code>
/// <para>
/// Inheritance runs <b>down</b> the tree only. A project role never reaches the project's team —
/// a team can serve several projects, so letting one project's administrator reach the shared team
/// would hand them the sprints of every sibling project.
/// </para>
/// <para>
/// Answers are computed by <b>union</b>, never by comparison. A user may hold several roles at one
/// scope, and holding one role does not imply another: a Scrum Master and a Product Owner are peers.
/// What each role permits is declared in <see cref="RolePermissions"/>.
/// </para>
/// <para>
/// Being pure is what makes this testable without a database and what lets the cached and uncached
/// paths share one definition of the rules. Anything needing I/O — loading the snapshot, locating a
/// project in the tree — happens before this is called.
/// </para>
/// </remarks>
public static class AccessEvaluator
{
    /// <summary>
    /// Whether the user may do <paramref name="permission"/> at an organization.
    /// </summary>
    /// <remarks>
    /// Organizations are the root, so there is nothing above them to inherit from — a direct
    /// assignment is the only way to hold anything here.
    /// </remarks>
    public static bool GrantsAtOrganization(
        AccessSnapshot snapshot, string permission, Guid organizationId)
    {
        foreach (var role in AccessSnapshot.RolesAt(snapshot.OrganizationRoles, organizationId))
            if (RolePermissions.ForOrganization(role).Contains(permission))
                return true;

        return false;
    }

    /// <summary>
    /// Whether the user may do <paramref name="permission"/> on a team.
    /// </summary>
    /// <param name="snapshot">The user's grants.</param>
    /// <param name="permission">The capability being asked about.</param>
    /// <param name="teamId">The team being asked about.</param>
    /// <param name="organizationId">
    /// The team's owning organization, or null when it could not be resolved (a team that no longer
    /// exists). Null simply means org-admin inheritance cannot be applied, so the answer falls back
    /// to direct grants.
    /// </param>
    public static bool GrantsAtTeam(
        AccessSnapshot snapshot, string permission, Guid teamId, Guid? organizationId)
    {
        foreach (var role in AccessSnapshot.RolesAt(snapshot.TeamRoles, teamId))
            if (RolePermissions.ForTeam(role).Contains(permission))
                return true;

        return organizationId is Guid org
            && GrantsAtOrganization(snapshot, permission, org);
    }

    /// <summary>
    /// Whether the user may do <paramref name="permission"/> <em>anywhere at all</em>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the handful of endpoints that are not keyed on a scope and cannot be — looking a user up
    /// by email during an invite, for instance, where the whole point is to find someone who is not
    /// yet in any of your organizations. There is no scope to name, so the question becomes whether
    /// the caller holds this authority somewhere.
    /// </para>
    /// <para>
    /// A weaker guarantee than the scoped checks, and deliberately so: it establishes that the caller
    /// administers <em>something</em>, not that they administer the thing they are asking about. Use
    /// it only where no scope exists to check, never as a shortcut for one that does.
    /// </para>
    /// </remarks>
    public static bool GrantsAnywhere(AccessSnapshot snapshot, string permission)
    {
        foreach (var roles in snapshot.OrganizationRoles.Values)
            foreach (var role in roles)
                if (RolePermissions.ForOrganization(role).Contains(permission))
                    return true;

        foreach (var roles in snapshot.TeamRoles.Values)
            foreach (var role in roles)
                if (RolePermissions.ForTeam(role).Contains(permission)
                    || RolePermissions.ForProjectViaTeam(role).Contains(permission))
                    return true;

        foreach (var roles in snapshot.ProjectRoles.Values)
            foreach (var role in roles)
                if (RolePermissions.ForProject(role).Contains(permission))
                    return true;

        return false;
    }

    /// <summary>
    /// Whether the user may do <paramref name="permission"/> on a project.
    /// </summary>
    /// <param name="snapshot">The user's grants.</param>
    /// <param name="permission">The capability being asked about.</param>
    /// <param name="projectId">The project being asked about.</param>
    /// <param name="location">
    /// Where the project sits in the tree, or null when it could not be resolved (a project that no
    /// longer exists). Null falls back to direct project grants only.
    /// </param>
    /// <remarks>
    /// Three routes, and any one of them suffices: a direct project assignment, a grant on the team
    /// the project is assigned to, or OrgAdmin of the owning organization.
    /// </remarks>
    public static bool GrantsAtProject(
        AccessSnapshot snapshot, string permission, Guid projectId, ProjectLocation? location)
    {
        foreach (var role in AccessSnapshot.RolesAt(snapshot.ProjectRoles, projectId))
            if (RolePermissions.ForProject(role).Contains(permission))
                return true;

        if (location is null)
            return false;

        foreach (var role in AccessSnapshot.RolesAt(snapshot.TeamRoles, location.AssignedTeamId))
            if (RolePermissions.ForProjectViaTeam(role).Contains(permission))
                return true;

        return GrantsAtOrganization(snapshot, permission, location.OrganizationId);
    }

    // ── Set-shaped questions ──────────────────────────────────────────────────
    //
    // The questions above take a scope and answer yes or no. These take no scope and answer "which
    // ones?", for the reads that span everything a user can see — search, the notification feed, the
    // workspace dashboard. Those cannot name a scope in an attribute, so before these existed they
    // each invented their own scoping rule, and all three invented the same wrong one: they treated
    // organization membership as access to everything inside the organization, which is precisely
    // what RolePermissions says it is not.

    /// <summary>
    /// Which projects the user may do <paramref name="permission"/> to, as the grants that reach
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same three routes as <see cref="GrantsAtProject"/>, read in the other direction: instead
    /// of taking a project and asking which grant covers it, this takes the grants and describes the
    /// projects they cover. That correspondence is exact and is asserted as a property test — for
    /// every snapshot, permission and project, <c>VisibleProjects(…).Includes(p)</c> equals
    /// <c>GrantsAtProject(…, p)</c>.
    /// </para>
    /// <para>
    /// Deliberately returns grant ids rather than project ids; see <see cref="ProjectVisibility"/>
    /// for why.
    /// </para>
    /// </remarks>
    public static ProjectVisibility VisibleProjects(AccessSnapshot snapshot, string permission)
    {
        // OrgAdmin is the only organization role that reaches below itself today, but this asks the
        // table rather than naming it, so a future organization role that grants downward is picked
        // up here without anyone remembering to come back.
        var organizations = ScopesWhere(
            snapshot.OrganizationRoles, permission, RolePermissions.ForOrganization);

        var teams = ScopesWhere(
            snapshot.TeamRoles, permission, RolePermissions.ForProjectViaTeam);

        var projects = ScopesWhere(
            snapshot.ProjectRoles, permission, RolePermissions.ForProject);

        return new ProjectVisibility(organizations, teams, projects);
    }

    /// <summary>
    /// Which organizations the user may do <paramref name="permission"/> to directly.
    /// </summary>
    /// <remarks>
    /// Organizations are the root of the tree, so unlike projects there is no inheritance to expand —
    /// this is just the scopes in the snapshot whose roles carry the permission. It exists so callers
    /// stop reaching for <c>OrganizationMemberships</c>, which answers a different question: being a
    /// member is not the same as holding <c>org:read</c>, even though today every member does.
    /// </remarks>
    public static Guid[] VisibleOrganizations(AccessSnapshot snapshot, string permission) =>
        ScopesWhere(snapshot.OrganizationRoles, permission, RolePermissions.ForOrganization);

    /// <summary>
    /// The scope ids whose roles carry <paramref name="permission"/> under <paramref name="permits"/>.
    /// </summary>
    private static Guid[] ScopesWhere(
        Dictionary<Guid, List<RoleType>> grants,
        string permission,
        Func<RoleType, System.Collections.Frozen.FrozenSet<string>> permits)
    {
        if (grants.Count == 0) return [];

        List<Guid>? matched = null;

        foreach (var (scopeId, roles) in grants)
        {
            foreach (var role in roles)
            {
                if (!permits(role).Contains(permission)) continue;

                // Union, as everywhere else: one role carrying it is enough, and the rest of this
                // scope's roles cannot take it away.
                (matched ??= []).Add(scopeId);
                break;
            }
        }

        return matched?.ToArray() ?? [];
    }
}
