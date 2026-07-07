using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.Rbac.Models;

/// <summary>
/// Assigns a role to a user within a specific scope (org/project/team).
/// One user can hold different roles at different scopes.
/// </summary>
public class RoleAssignment : BaseEntity
{
    /// <summary>The user this assignment belongs to.</summary>
    public Guid UserId { get; set; }

    /// <summary>Role granted to the user.</summary>
    public RoleType Role { get; set; }

    /// <summary>Scope level at which this role applies.</summary>
    public RoleScope Scope { get; set; }

    /// <summary>
    /// The resource ID for the scope.
    /// For Scope=Organization this is the OrgId,
    /// for Scope=Project this is the ProjectId,
    /// for Scope=Team this is the TeamId.
    /// </summary>
    public Guid ScopeId { get; set; }
}
