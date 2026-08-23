using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.Rbac.Models;

/// <summary>
/// Assigns a role to a user within a specific scope (org/project/team).
/// One user can hold different roles at different scopes.
/// </summary>
public class RoleAssignment : BaseEntity
{
    /// <summary>
    /// The principal this assignment belongs to.
    /// </summary>
    /// <remarks>
    /// Named <c>UserId</c> still, because every grant was a user's until integrations arrived and
    /// renaming the column would touch every RBAC query for no behavioural gain. What it holds is a
    /// principal id — a user id when <see cref="PrincipalType"/> is <c>User</c>, a
    /// <c>GitProviderInstallation</c> id when it is <c>Integration</c>.
    /// </remarks>
    public Guid UserId { get; set; }

    /// <summary>
    /// Whether a person or an integration holds this grant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recorded so an audit can answer "what is this row for?" and so the check constraint can keep
    /// the <c>Integration</c> role away from people. <b>Access resolution does not filter on it</b>
    /// — a snapshot is loaded by principal id, and ids are random GUIDs, so a person cannot collide
    /// with an installation. Threading the type through the whole resolver would buy no additional
    /// safety.
    /// </para>
    /// <para>
    /// Defaults to <c>User</c>, so every grant written before integrations existed reads correctly.
    /// </para>
    /// </remarks>
    public PrincipalType PrincipalType { get; set; } = PrincipalType.User;

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
    // public Guid ScopeId { get; set; }
    public Guid? OrganizationId {get; set;}
    public Guid? ProjectId {get; set;}
    public Guid? TeamId {get; set;}
}
