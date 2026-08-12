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
    /// Just the roles a user holds at one scope.
    /// </summary>
    /// <remarks>
    /// Returns the roles rather than a yes/no answer because the privilege comparison cannot happen
    /// in SQL: <see cref="RoleType"/> is persisted with <c>HasConversion&lt;string&gt;()</c>, so a
    /// database-side <c>&lt;=</c> would compare the *names*. 'TeamMember' &lt;= 'Reader' is false
    /// and 'Reader' &lt;= 'TeamMember' is true, which would both deny team members read access and
    /// let readers perform team-member writes.
    /// </remarks>
    Task<IReadOnlyList<RoleType>> GetRolesAtScopeAsync(
        Guid userId,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default);

    /// <summary>Every role assignment held by a user, across all scopes.</summary>
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
    /// Whether the user is an OrgAdmin of the organization owning the given project or team.
    /// </summary>
    /// <remarks>
    /// Org admins implicitly satisfy every project and team check inside their organization, so
    /// this is the fallback consulted when no direct assignment matches. Resolved in one query
    /// rather than fetching the admin's organizations and testing them in memory.
    /// </remarks>
    Task<bool> IsOrgAdminForScopeAsync(
        Guid userId,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default);

    void Add(RoleAssignment assignment);
    void Remove(RoleAssignment assignment);
    void RemoveRange(IEnumerable<RoleAssignment> assignments);

    /// <summary>Persists everything staged since the last save.</summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
