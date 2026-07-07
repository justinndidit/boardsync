using BoardSync.Api.Modules.Rbac.Models;

namespace BoardSync.Api.Modules.Rbac.Services;

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
