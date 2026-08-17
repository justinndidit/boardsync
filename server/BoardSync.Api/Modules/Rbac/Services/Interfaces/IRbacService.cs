using BoardSync.Api.Modules.Rbac.Models;

namespace BoardSync.Api.Modules.Rbac.Services.Interfaces;

public interface IRbacService
{
    /// <summary>
    /// Assign a role to a user at the given scope.
    /// Idempotent — if an identical assignment already exists it is returned unchanged.
    /// </summary>
    Task<RoleAssignment> AssignRoleAsync(
        Guid userId,
        RoleType role,
        RoleScope scope,
        Guid scopeId,
        Guid? assignedBy = null,
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
    /// Check whether a user holds at least <paramref name="minimumRole"/> at the given scope.
    /// A more-privileged role (lower enum value) satisfies a less-privileged requirement.
    /// OrgAdmin implicitly satisfies any project or team scope check within that org.
    /// </summary>
    Task<bool> HasRoleAsync(Guid userId, RoleType minimumRole, RoleScope scope, Guid scopeId, CancellationToken ct = default);

    /// <summary>Return all role assignments for a user.</summary>
    Task<IReadOnlyList<RoleAssignment>> GetUserRolesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Return all role assignments for a specific scope resource.</summary>
    Task<IReadOnlyList<RoleAssignment>> GetScopeRolesAsync(RoleScope scope, Guid scopeId, CancellationToken ct = default);
}
