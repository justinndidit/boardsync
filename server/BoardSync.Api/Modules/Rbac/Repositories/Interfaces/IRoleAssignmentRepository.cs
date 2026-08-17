using BoardSync.Api.Modules.Rbac.Models;

namespace BoardSync.Api.Modules.Rbac.Repositories.Interfaces;

/// <summary>
/// Data access for role assignments — the <c>iam.RoleAssignments</c> table.
/// </summary>
public interface IRoleAssignmentRepository
{
    /// <summary>
    /// A user's exact role assignment at one scope, tracked for mutation, or null.
    /// </summary>
    Task<RoleAssignment?> GetAsync(
        Guid userId,
        RoleType role,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default);

    /// <summary>
    /// Every role assignment held by a user, across all scopes — the raw material for an
    /// <see cref="Models.AccessSnapshot"/>.
    /// </summary>
    /// <remarks>
    /// Returns the assignments rather than a yes/no answer because the privilege comparison cannot
    /// happen in SQL: <see cref="RoleType"/> is persisted with <c>HasConversion&lt;string&gt;()</c>,
    /// so a database-side <c>&lt;=</c> would compare the *names*. 'TeamMember' &lt;= 'Reader' is
    /// false and 'Reader' &lt;= 'TeamMember' is true, which would both deny team members read access
    /// and let readers perform team-member writes. See <c>AccessEvaluator.Satisfies</c>.
    /// </remarks>
    Task<IReadOnlyList<RoleAssignment>> GetForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Every role assignment attached to one scope resource.</summary>
    Task<IReadOnlyList<RoleAssignment>> GetForScopeAsync(
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default);

    /// <summary>All of a user's assignments at one scope, tracked — used when revoking in bulk.</summary>
    Task<IReadOnlyList<RoleAssignment>> GetUserAssignmentsAtScopeAsync(
        Guid userId,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes every assignment a user holds anywhere inside one organization — the organization
    /// itself, and every team and project belonging to it.
    /// </summary>
    /// <returns>How many rows were deleted.</returns>
    /// <remarks>
    /// <para>
    /// Used when someone leaves an organization. Org membership is what every grant underneath it
    /// hangs off, so losing it has to take the whole subtree with it.
    /// </para>
    /// <para>
    /// Unlike the rest of this interface this executes immediately rather than staging a change for
    /// <see cref="SaveChangesAsync"/>. The alternative is loading every assignment across every
    /// project and team in the organization only to mark each one deleted, which is unbounded work
    /// for a result nobody reads. Callers must therefore run it inside their own transaction, which
    /// it enlists in, and must not be holding a tracked instance of anything it deletes.
    /// </para>
    /// </remarks>
    Task<int> RemoveAllInOrganizationAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken ct = default);

    /// <summary>
    /// The teams a user is a member of, across every organization.
    /// </summary>
    /// <remarks>
    /// Membership is a grant in its own right — it is what gives someone access to the projects
    /// their team works on — so resolving access has to read it alongside the role table. It lives
    /// on this interface, rather than reaching into the OrgProject module's repositories, for the
    /// same reason <see cref="GetProjectLocationAsync"/> queries Projects directly: the RBAC module
    /// owns its own reads.
    /// </remarks>
    Task<IReadOnlyList<Guid>> GetMemberTeamIdsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The users who are members of one team.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="GetMemberTeamIdsAsync"/>, needed when something changes the tree
    /// rather than a grant — reassigning a project moves it under a different team, which changes
    /// what both teams' members may see without touching anyone's role assignments.
    /// </remarks>
    Task<IReadOnlyList<Guid>> GetTeamMemberUserIdsAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Where a project sits in the scope tree, or null if it does not exist.
    /// </summary>
    Task<ProjectLocation?> GetProjectLocationAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// The organization owning a team, or null if the team does not exist.
    /// </summary>
    Task<Guid?> GetTeamOrganizationIdAsync(Guid teamId, CancellationToken ct = default);

    void Add(RoleAssignment assignment);
    void Remove(RoleAssignment assignment);
    void RemoveRange(IEnumerable<RoleAssignment> assignments);

    /// <summary>Persists everything staged since the last save.</summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
