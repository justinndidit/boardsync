using BoardSync.Api.Modules.Rbac.Models;

namespace BoardSync.Api.Modules.Rbac.Services.Interfaces;

public interface IRbacService
{
    /// <summary>
    /// Assign a role to a user at the given scope.
    /// Idempotent — if an identical assignment already exists it is returned unchanged.
    /// </summary>
    /// <param name="userId">The principal receiving the grant — a user id, or an installation id.</param>
    /// <param name="role">What to grant.</param>
    /// <param name="scope">Where it applies.</param>
    /// <param name="scopeId">Which organization, team or project.</param>
    /// <param name="assignedBy">Who did it, for the audit trail.</param>
    /// <param name="principalType">
    /// What kind of thing is receiving it. Defaults to <c>User</c>, so every existing call site means
    /// what it always did; the git integration is the only caller that passes anything else.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<RoleAssignment> AssignRoleAsync(
        Guid userId,
        RoleType role,
        RoleScope scope,
        Guid scopeId,
        Guid? assignedBy = null,
        PrincipalType principalType = PrincipalType.User,
        CancellationToken ct = default);

    /// <summary>Remove a specific role assignment.</summary>
    Task RemoveRoleAsync(Guid userId, RoleType role, RoleScope scope, Guid scopeId, CancellationToken ct = default);
    Task RemoveAllRolesAsync(Guid userId, RoleScope scope, Guid scopeId, CancellationToken ct = default);

    /// <summary>
    /// Remove every role a user holds anywhere inside an organization — at the organization itself
    /// and at every team and project belonging to it.
    /// </summary>
    /// <remarks>
    /// The counterpart to losing organization membership. Revoking only the organization-scope rows
    /// leaves project and team grants behind that keep working, because every check below the
    /// organization resolves against its own scope and never consults membership.
    /// </remarks>
    Task RemoveAllRolesInOrganizationAsync(Guid userId, Guid organizationId, CancellationToken ct = default);

    /// <summary>
    /// Whether a user may do <paramref name="permission"/> at the given scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single authorization question in the system. Grants reach a scope by three routes — a
    /// direct assignment, a role on the team a project is assigned to, or OrgAdmin of the owning
    /// organization — and any one of them suffices. What each role permits is declared in
    /// <see cref="Models.RolePermissions"/>.
    /// </para>
    /// <para>
    /// Replaced a rank check (<c>HasRoleAsync(minimumRole)</c>), which could not express questions
    /// whose answer is a set of peers rather than a threshold — "may start a sprint" is permitted to
    /// a Scrum Master and a Product Owner, neither of whom outranks the other.
    /// </para>
    /// </remarks>
    Task<bool> HasPermissionAsync(
        Guid userId,
        string permission,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default);

    /// <summary>
    /// Whether a user holds <paramref name="permission"/> at any scope at all.
    /// </summary>
    /// <remarks>
    /// Only for endpoints with no scope to check — looking a user up by email during an invite is
    /// the case it exists for, since the person being looked up is by definition not yet in your
    /// organization. It answers "does this caller administer something", which is weaker than "may
    /// they do this here", so it is never a substitute for
    /// <see cref="HasPermissionAsync"/> where a scope is available.
    /// </remarks>
    Task<bool> HasPermissionAnywhereAsync(
        Guid userId,
        string permission,
        CancellationToken ct = default);

    /// <summary>
    /// Which projects a user may do <paramref name="permission"/> to, in a form a query can filter on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart to <see cref="HasPermissionAsync"/> for reads that span everything a user can
    /// see rather than naming one scope — global search, the notification feed, the workspace
    /// dashboard. Those endpoints cannot carry a <c>[RequirePermission]</c> attribute, because there
    /// is no route parameter to resolve, so they have to do the scoping themselves; this is what they
    /// scope with.
    /// </para>
    /// <para>
    /// The result reaches SQL as a predicate rather than an id list — see
    /// <see cref="ProjectVisibility"/>.
    /// </para>
    /// </remarks>
    Task<ProjectVisibility> GetProjectVisibilityAsync(
        Guid userId,
        string permission,
        CancellationToken ct = default);

    /// <summary>
    /// Which organizations a user may do <paramref name="permission"/> to.
    /// </summary>
    /// <remarks>
    /// Use this rather than reading <c>OrganizationMemberships</c> directly. Membership and
    /// <c>org:read</c> coincide today, and a scoping rule that relies on them continuing to coincide
    /// is one refactor away from being wrong in the permissive direction.
    /// </remarks>
    Task<Guid[]> GetVisibleOrganizationIdsAsync(
        Guid userId,
        string permission,
        CancellationToken ct = default);

    /// <summary>
    /// Everything a user may do at one scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HasPermissionAsync"/> asked once per permission would work and would be wrong to
    /// build on: twenty-odd questions, each re-resolving the same scope. This resolves the snapshot
    /// and the scope's position in the tree once, then answers every permission from them in memory.
    /// </para>
    /// <para>
    /// A scope the caller cannot see, and one that does not exist, both come back empty. That is the
    /// same posture as the 404-on-denial rule in <c>PermissionAuthorizationFilter</c>: an empty
    /// answer must not distinguish "no such project" from "not yours".
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<string>> GetPermissionsAtAsync(
        Guid userId,
        ScopeRef scope,
        CancellationToken ct = default);

    /// <summary>
    /// The people who may do <paramref name="permission"/> on a project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inverse of <see cref="HasPermissionAsync"/>, and the only way to answer "who should be
    /// told about this?" — the notification for work reaching the QA lane has to reach whoever can
    /// certify it, and nothing else can work that out.
    /// </para>
    /// <para>
    /// Which roles carry the permission is derived from <see cref="Models.RolePermissions"/> rather
    /// than listed, so this cannot fall out of step with what the guards actually allow. People
    /// only: an integration principal has nobody to notify.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<Guid>> GetUsersWithPermissionOnProjectAsync(
        Guid projectId,
        string permission,
        CancellationToken ct = default);

    /// <summary>Return all role assignments for a user.</summary>
    Task<IReadOnlyList<RoleAssignment>> GetUserRolesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Return all role assignments for a specific scope resource.</summary>
    Task<IReadOnlyList<RoleAssignment>> GetScopeRolesAsync(RoleScope scope, Guid scopeId, CancellationToken ct = default);

    /// <summary>
    /// Hands a team position to <paramref name="toUserId"/>, taking it from whoever holds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One operation rather than revoke-then-assign, because a handover is a single act: doing it as
    /// two calls leaves a window with no Scrum Master, and a half-finished transfer if the second
    /// one fails.
    /// </para>
    /// <para>
    /// Returns the previous holder, or null if the position was vacant, so the caller can report
    /// what actually changed.
    /// </para>
    /// </remarks>
    Task<Guid?> TransferTeamPositionAsync(
        Guid teamId,
        RoleType position,
        Guid toUserId,
        Guid assignedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Leaves a team position vacant.
    /// </summary>
    /// <returns>The user who held it, or null if it was already vacant.</returns>
    Task<Guid?> VacateTeamPositionAsync(
        Guid teamId,
        RoleType position,
        CancellationToken ct = default);
}
