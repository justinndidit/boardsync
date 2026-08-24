using System.Collections.Frozen;
using System.Reflection;
using BoardSync.Api.Shared.Metadata;

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

    /// <summary>
    /// Contribution plus the authority to certify. What a Tester carries on a project.
    /// </summary>
    /// <remarks>
    /// A tester contributes as well — they file bugs, comment, and reorder the sprint — so this is
    /// contribution plus <c>workitem:verify</c> rather than a read-only role with one extra power.
    /// It stops there: certifying work says nothing about administering the project or deciding what
    /// its sprint commits to.
    /// </remarks>
    private static readonly string[] ProjectTester =
    [
        .. ProjectContributor,
        Permissions.WorkItemVerify
    ];

    /// <summary>
    /// What a git installation may do on a project it feeds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Contribution without certification: it moves work through the states a git signal can
    /// identify, comments, and reads what it needs to decide. It carries no <c>workitem:verify</c>,
    /// no <c>workitem:delete</c>, no <c>sprint:scope</c> and nothing administrative.
    /// </para>
    /// <para>
    /// This list is the QA gate. Adding <c>workitem:verify</c> here would let a merge close a work
    /// item, which is precisely what the product promises it cannot do — so it is the one entry in
    /// this file worth guarding in review.
    /// </para>
    /// </remarks>
    private static readonly string[] ProjectIntegration =
    [
        Permissions.ProjectRead,
        Permissions.BoardRead,
        Permissions.WorkItemRead,
        Permissions.WorkItemWrite,
        Permissions.WorkItemComment,
        Permissions.SprintRead
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

    /// <summary>
    /// Sprint authority plus certification. What a Product Owner carries onto their team's projects.
    /// </summary>
    /// <remarks>
    /// <b>The Product Owner certifies; the Scrum Master does not.</b> In Scrum the Product Owner
    /// accepts the increment — deciding whether what was built is what was asked for is the same act
    /// as certifying it — while the Scrum Master owns the process rather than the acceptance. Both
    /// run the sprint; only one signs work off.
    /// <para>
    /// This is build_context.md §11 decision 1, and it is deliberately the narrow reading. Giving the
    /// Scrum Master certification too is one line here if a team wants it; taking it back once people
    /// rely on it is not.
    /// </para>
    /// </remarks>
    private static readonly string[] ProjectSprintOwner =
    [
        .. ProjectSprintRunner,
        Permissions.WorkItemVerify
    ];

    private static readonly string[] ProjectAdministrator =
    [
        .. ProjectContributor,
        Permissions.ProjectAdmin,
        Permissions.ProjectMemberManage,
        Permissions.BoardConfigure,
        Permissions.WorkItemDelete,
        Permissions.WorkItemVerify,
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

            // Like the positions above, what sits at team scope is the appointment itself; the
            // authority to certify reaches the team's projects through the edge below, because a
            // work item is a project-scope object.
            [RoleType.Tester] = new[] { Permissions.TeamRead }.ToFrozenSet(),

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
            [RoleType.Tester] = ProjectTester.ToFrozenSet(),
            [RoleType.Viewer] = ProjectViewer.ToFrozenSet(),

            // Assignable only to an Integration principal. AssignableAt still lists it, because the
            // check constraint has to permit it — the endpoints that hand out roles filter it out
            // separately, and PrincipalType is what actually keeps it away from people.
            [RoleType.Integration] = ProjectIntegration.ToFrozenSet()
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
            // Certification is "higher authority in the team", so a Team Lead carries it onto the
            // team's projects. Their job is the people and the work being right; the sprint is the
            // Scrum Master's and the Product Owner's.
            [RoleType.TeamLead] = ProjectTester.ToFrozenSet(),

            // The two roles whose job is the sprint carry it onto every project their team serves.
            // That is the point of the appointment: a Scrum Master runs the sprints of the team's
            // work, and which project a given sprint sits in is not something they should have to
            // be granted separately. It stops at the sprint — see ProjectSprintRunner.
            [RoleType.ScrumMaster] = ProjectSprintRunner.ToFrozenSet(),
            [RoleType.ProductOwner] = ProjectSprintOwner.ToFrozenSet(),

            [RoleType.TeamMember] = ProjectContributor.ToFrozenSet(),

            // The role that exists for this: testing every project the team serves.
            [RoleType.Tester] = ProjectTester.ToFrozenSet(),

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
    /// what the "valid roles are …" rejection message enumerates, and what
    /// <c>GET /api/metadata</c> publishes.
    /// </para>
    /// <para>
    /// Sorted by <b>declared display order</b>, not by enum value. The numeric values are
    /// deliberately meaningless (see <see cref="RoleType"/>), so ordering by them is arbitrary — and
    /// it visibly diverged the moment a role was added: <c>Tester</c> took the next free number and
    /// so sorted below <c>Viewer</c>, which would have shown users one order in a role picker and a
    /// different one in the rejection message listing the same roles.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<RoleType> AssignableAt(RoleScope scope) =>
    [
        .. RolesAt(scope).Keys
            .OrderBy(role => typeof(RoleType).GetField(role.ToString())
                ?.GetCustomAttribute<DisplayMetadataAttribute>()?.Order ?? int.MaxValue)
            .ThenBy(role => role.ToString(), StringComparer.Ordinal)
    ];

    /// <summary>
    /// Roles that may be granted to a <em>person</em> at a scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AssignableAt"/> answers a different question — what the database check constraint
    /// permits — and the two stopped coinciding when <see cref="RoleType.Integration"/> arrived. It
    /// has to be valid at project scope, because a git installation genuinely holds it there; it must
    /// never appear in a role picker or be accepted by an endpoint that hands roles to people.
    /// </para>
    /// <para>
    /// Conflating the two would have let a project administrator grant <c>Integration</c> to a
    /// colleague. That grants less than <c>Contributor</c>, so it is not an escalation — but it is a
    /// role nobody can explain the presence of, and the kind of confusion that erodes trust in the
    /// whole model.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<RoleType> GrantableToUsersAt(RoleScope scope) =>
        [.. AssignableAt(scope).Where(role => !HeldOnlyByIntegrations(role))];

    /// <summary>Whether a role exists for a non-human principal and must never be handed to a person.</summary>
    public static bool HeldOnlyByIntegrations(RoleType role) => role is RoleType.Integration;

    private static FrozenDictionary<RoleType, FrozenSet<string>> RolesAt(RoleScope scope) => scope switch
    {
        RoleScope.Organization => AtOrganization,
        RoleScope.Team => AtTeam,
        RoleScope.Project => AtProject,
        _ => FrozenDictionary<RoleType, FrozenSet<string>>.Empty
    };
}
