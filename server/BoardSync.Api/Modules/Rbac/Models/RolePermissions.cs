using System.Collections.Frozen;

namespace BoardSync.Api.Modules.Rbac.Models;

/// <summary>
/// What each role permits, and how a grant at one scope reaches the scopes beneath it.
/// </summary>
/// <remarks>
/// <para>
/// This table is the permission model. Everything else — the resolver, the evaluator, the endpoint
/// guards — is machinery for applying it, so a question about who may do what is answered by reading
/// this file rather than by tracing call sites.
/// </para>
/// <para>
/// A user may hold several roles at one scope, and the answer is the <b>union</b> of what they
/// permit. Never a comparison: the roles are not ordered (see <see cref="RoleType"/>).
/// </para>
/// </remarks>
public static class RolePermissions
{
    // ── Building blocks ───────────────────────────────────────────────────────

    /// <summary>
    /// What contributing to a project means — read, write, comment, and order the sprint, but
    /// neither administer the project nor decide what its sprint commits to.
    /// </summary>
    private static readonly string[] ProjectContributor =
    [
        Permissions.ProjectRead,
        Permissions.BoardRead,
        Permissions.WorkItemRead,
        Permissions.WorkItemWrite,
        Permissions.WorkItemComment,
        Permissions.SprintRead,
        Permissions.SprintOrder
    ];

    private static readonly string[] ProjectViewer =
    [
        Permissions.ProjectRead,
        Permissions.BoardRead,
        Permissions.WorkItemRead,
        Permissions.SprintRead
    ];

    /// <summary>
    /// Contribution plus authority over the sprint itself — the lifecycle and what it commits to,
    /// but nothing else about the project.
    /// </summary>
    /// <remarks>
    /// What a Scrum Master or Product Owner carries onto their team's projects. Deliberately not
    /// <see cref="ProjectAdministrator"/>: running a sprint is their job, renaming the project,
    /// reconfiguring its board or handing out its roles is not. The distinction is the whole reason
    /// sprint authority is separable from project administration.
    /// </remarks>
    private static readonly string[] ProjectSprintRunner =
    [
        .. ProjectContributor,
        Permissions.SprintManage,
        Permissions.SprintScope
    ];

    private static readonly string[] ProjectAdministrator =
    [
        .. ProjectContributor,
        Permissions.ProjectAdmin,
        Permissions.ProjectMemberManage,
        Permissions.BoardConfigure,
        Permissions.WorkItemDelete,
        Permissions.SprintManage,
        Permissions.SprintScope
    ];

    /// <summary>
    /// Everything. Held only by OrgAdmin, which by definition administers everything in its
    /// organization, at every scope beneath it.
    /// </summary>
    private static readonly FrozenSet<string> Everything = ((string[])
    [
        Permissions.OrgRead, Permissions.OrgAdmin, Permissions.OrgMemberManage,
        Permissions.TeamRead, Permissions.TeamManage, Permissions.TeamMemberManage,
        Permissions.TeamRoleAssign,
        Permissions.SprintRead, Permissions.SprintManage, Permissions.SprintScope,
        Permissions.SprintOrder,
        .. ProjectAdministrator
    ]).ToFrozenSet();

    // ── Organization scope ────────────────────────────────────────────────────

    private static readonly FrozenDictionary<RoleType, FrozenSet<string>> AtOrganization =
        new Dictionary<RoleType, FrozenSet<string>>
        {
            [RoleType.OrgAdmin] = Everything,

            // What organization membership itself confers, and the whole of it. Access to anything
            // inside the organization is a team or project grant; see the note on Everything.
            [RoleType.Member] = new[] { Permissions.OrgRead }.ToFrozenSet()
        }.ToFrozenDictionary();

    // ── Team scope ────────────────────────────────────────────────────────────

    private static readonly FrozenDictionary<RoleType, FrozenSet<string>> AtTeam =
        new Dictionary<RoleType, FrozenSet<string>>
        {
            // Leads the people: composition, and who holds the other positions.
            [RoleType.TeamLead] = new[]
            {
                Permissions.TeamRead, Permissions.TeamManage, Permissions.TeamMemberManage,
                Permissions.TeamRoleAssign
            }.ToFrozenSet(),

            // Owns the process, and owns the backlog. Their sprint authority is not listed here
            // because a sprint is not a team-scope object any more: it reaches the sprints through
            // the team → project edge below, which is where both are given sprint:manage and
            // sprint:scope over every project the team serves. What sits at team scope is the
            // appointment itself.
            [RoleType.ScrumMaster] = new[] { Permissions.TeamRead }.ToFrozenSet(),
            [RoleType.ProductOwner] = new[] { Permissions.TeamRead }.ToFrozenSet(),

            [RoleType.TeamMember] = new[] { Permissions.TeamRead }.ToFrozenSet(),

            [RoleType.Viewer] = new[] { Permissions.TeamRead }.ToFrozenSet()
        }.ToFrozenDictionary();

    // ── Project scope ─────────────────────────────────────────────────────────

    private static readonly FrozenDictionary<RoleType, FrozenSet<string>> AtProject =
        new Dictionary<RoleType, FrozenSet<string>>
        {
            // No OrgAdmin entry. An organization administrator reaches a project through
            // GrantsAtProject's final hop to the owning organization, never through a project-scope
            // row — the check constraint forbids one. Listing it here would grant nothing extra and
            // would make IsValidAt disagree with the database about what may be assigned.
            [RoleType.ProjectAdmin] = ProjectAdministrator.ToFrozenSet(),
            [RoleType.Contributor] = ProjectContributor.ToFrozenSet(),
            [RoleType.Viewer] = ProjectViewer.ToFrozenSet()
        }.ToFrozenDictionary();

    // ── Team → project inheritance ────────────────────────────────────────────

    /// <summary>
    /// What a role held on a team permits on that team's projects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every team role confers contribution and nothing more — deliberately flat. A team can serve
    /// several projects, so letting a position administer them would hand its holder authority over
    /// every sibling project at once. Project administration stays a direct project grant.
    /// </para>
    /// <para>
    /// This edge is also how a team reaches its sprints, since a sprint belongs to a project.
    /// Contribution carries <c>sprint:read</c> and <c>sprint:order</c>, so anyone on the team can
    /// see and reorder the sprint of any project the team serves.
    /// </para>
    /// <para>
    /// <b>Scrum Master and Product Owner carry more.</b> They additionally get
    /// <c>sprint:manage</c> and <c>sprint:scope</c> on those projects, because running the sprint is
    /// the appointment, and a sprint's project is an implementation detail of where the team's work
    /// happens rather than a separate thing to be granted. This is the one place the edge is not
    /// flat, and it stays narrow on purpose: sprint authority, never project administration. A
    /// Scrum Master still cannot rename the project, reconfigure its board, or grant anyone a role
    /// on it.
    /// </para>
    /// </remarks>
    private static readonly FrozenDictionary<RoleType, FrozenSet<string>> TeamToProject =
        new Dictionary<RoleType, FrozenSet<string>>
        {
            [RoleType.TeamLead] = ProjectContributor.ToFrozenSet(),

            // The two roles whose job is the sprint carry it onto every project their team serves.
            // That is the point of the appointment: a Scrum Master runs the sprints of the team's
            // work, and which project a given sprint sits in is not something they should have to
            // be granted separately. It stops at the sprint — see ProjectSprintRunner.
            [RoleType.ScrumMaster] = ProjectSprintRunner.ToFrozenSet(),
            [RoleType.ProductOwner] = ProjectSprintRunner.ToFrozenSet(),

            [RoleType.TeamMember] = ProjectContributor.ToFrozenSet(),
            [RoleType.Viewer] = ProjectViewer.ToFrozenSet()
        }.ToFrozenDictionary();

    // ── Lookups ───────────────────────────────────────────────────────────────

    private static readonly FrozenSet<string> None = FrozenSet<string>.Empty;

    /// <summary>What this role permits when held directly at an organization.</summary>
    public static FrozenSet<string> ForOrganization(RoleType role) =>
        AtOrganization.GetValueOrDefault(role, None);

    /// <summary>What this role permits when held directly at a team.</summary>
    public static FrozenSet<string> ForTeam(RoleType role) =>
        AtTeam.GetValueOrDefault(role, None);

    /// <summary>What this role permits when held directly at a project.</summary>
    public static FrozenSet<string> ForProject(RoleType role) =>
        AtProject.GetValueOrDefault(role, None);

    /// <summary>What this role, held at a team, permits on that team's projects.</summary>
    public static FrozenSet<string> ForProjectViaTeam(RoleType role) =>
        TeamToProject.GetValueOrDefault(role, None);

    /// <summary>
    /// Whether a role may be assigned at a scope at all. Mirrors the database check constraint, so
    /// a request is rejected before it reaches a constraint violation.
    /// </summary>
    public static bool IsValidAt(RoleType role, RoleScope scope) =>
        RolesAt(scope).ContainsKey(role);

    /// <summary>
    /// Every role that means something at a scope, ordered by enum value so the list is stable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The endpoints that hand out roles validate against this rather than against a list of their
    /// own. Two copies of "which roles belong at project scope" is one copy too many: the table
    /// above, the check constraint and the endpoint have to agree, and a hand-maintained third list
    /// is the one that silently falls behind — as the organization list did, still advertising
    /// <c>ProjectAdmin</c> and <c>TeamMember</c> in its documentation long after both were rejected.
    /// </para>
    /// <para>
    /// Team scope includes the positions, which are appointed through
    /// <c>/api/teams/{teamId}/positions/{position}</c> rather than granted, and <c>TeamMember</c>,
    /// which team membership confers. There is deliberately no general role-assignment endpoint at
    /// team scope for this to feed.
    /// </para>
    /// <para>
    /// Sorted rather than taken in the table's order: <see cref="FrozenDictionary{TKey,TValue}"/>
    /// does not promise to enumerate keys in insertion order, and this list reaches users — it is
    /// what the "valid roles are …" rejection message enumerates.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<RoleType> AssignableAt(RoleScope scope) =>
        [.. RolesAt(scope).Keys.Order()];

    private static FrozenDictionary<RoleType, FrozenSet<string>> RolesAt(RoleScope scope) => scope switch
    {
        RoleScope.Organization => AtOrganization,
        RoleScope.Team => AtTeam,
        RoleScope.Project => AtProject,
        _ => FrozenDictionary<RoleType, FrozenSet<string>>.Empty
    };
}
