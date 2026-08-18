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

    /// <summary>What contributing to a project means — read, write, comment, but not administer.</summary>
    private static readonly string[] ProjectContributor =
    [
        Permissions.ProjectRead,
        Permissions.BoardRead,
        Permissions.WorkItemRead,
        Permissions.WorkItemWrite,
        Permissions.WorkItemComment
    ];

    private static readonly string[] ProjectViewer =
    [
        Permissions.ProjectRead,
        Permissions.BoardRead,
        Permissions.WorkItemRead
    ];

    private static readonly string[] ProjectAdministrator =
    [
        .. ProjectContributor,
        Permissions.ProjectAdmin,
        Permissions.ProjectMemberManage,
        Permissions.BoardConfigure,
        Permissions.WorkItemDelete
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
            // Leads the people: composition, and who holds the other positions. Also carries the
            // sprint rights, because a lead standing in for an absent Scrum Master is the ordinary
            // case rather than an escalation.
            [RoleType.TeamLead] = new[]
            {
                Permissions.TeamRead, Permissions.TeamManage, Permissions.TeamMemberManage,
                Permissions.TeamRoleAssign,
                Permissions.SprintRead, Permissions.SprintManage, Permissions.SprintScope,
                Permissions.SprintOrder
            }.ToFrozenSet(),

            // Owns the process: runs the sprint lifecycle. No authority over team composition.
            [RoleType.ScrumMaster] = new[]
            {
                Permissions.TeamRead,
                Permissions.SprintRead, Permissions.SprintManage, Permissions.SprintScope,
                Permissions.SprintOrder
            }.ToFrozenSet(),

            // Owns the backlog: decides what a sprint commits to. Currently indistinguishable from
            // ScrumMaster in permissions — see the note in the design doc (§4.3.1); the difference
            // appears when acceptance and prioritisation endpoints exist.
            [RoleType.ProductOwner] = new[]
            {
                Permissions.TeamRead,
                Permissions.SprintRead, Permissions.SprintManage, Permissions.SprintScope,
                Permissions.SprintOrder
            }.ToFrozenSet(),

            // Contributes. Orders the sprint but does not decide what it commits to; adding items
            // is allowed only as decomposition of work already committed, which is enforced in
            // SprintService rather than here because it depends on the item, not the role.
            [RoleType.TeamMember] = new[]
            {
                Permissions.TeamRead, Permissions.SprintRead, Permissions.SprintOrder
            }.ToFrozenSet(),

            [RoleType.Viewer] = new[]
            {
                Permissions.TeamRead, Permissions.SprintRead
            }.ToFrozenSet()
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
    /// Every team role confers contribution and nothing more — deliberately flat. A team can serve
    /// several projects, so letting a position administer them would hand its holder authority over
    /// every sibling project at once. Project administration stays a direct project grant.
    /// </remarks>
    private static readonly FrozenDictionary<RoleType, FrozenSet<string>> TeamToProject =
        new Dictionary<RoleType, FrozenSet<string>>
        {
            [RoleType.TeamLead] = ProjectContributor.ToFrozenSet(),
            [RoleType.ScrumMaster] = ProjectContributor.ToFrozenSet(),
            [RoleType.ProductOwner] = ProjectContributor.ToFrozenSet(),
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
