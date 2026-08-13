using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Repositories.Interfaces;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;

namespace BoardSync.Api.Modules.Rbac.Services.Implementations;

public class RbacService : IRbacService
{
    private readonly IRoleAssignmentRepository _repository;
    private readonly ILogger<RbacService> _logger;

    public RbacService(IRoleAssignmentRepository repository, ILogger<RbacService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<RoleAssignment> AssignRoleAsync(
        Guid userId,
        RoleType role,
        RoleScope scope,
        Guid scopeId,
        Guid? assignedBy = null,
        CancellationToken ct = default)
    {
        var existing = await _repository.GetAsync(userId, role, scope, scopeId, ct);

        if (existing != null)
            return existing;

        var assignment = CreateAssignment(userId, role, scope, scopeId, assignedBy);

        _repository.Add(assignment);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Assigned role {Role} to user {UserId} at {Scope}:{ScopeId}",
            role, userId, scope, scopeId);

        return assignment;
    }

    public async Task RemoveRoleAsync(
        Guid userId,
        RoleType role,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
    {
        var assignment = await _repository.GetAsync(userId, role, scope, scopeId, ct);

        if (assignment == null) return;

        _repository.Remove(assignment);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Removed role {Role} from user {UserId} at {Scope}:{ScopeId}",
            role, userId, scope, scopeId);
    }

    public async Task<bool> HasRoleAsync(
        Guid userId,
        RoleType minimumRole,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
    {
        var assignments = await _repository.GetRolesAtScopeAsync(userId, scope, scopeId, ct);

        // A role satisfies the requirement if its numeric value is <= minimumRole (lower value =
        // more privileged). The comparison MUST be resolved here and matched by identity — see
        // IRoleAssignmentRepository.GetRolesAtScopeAsync for why it cannot happen in SQL.
        var satisfyingRoles = RolesSatisfying(minimumRole);

        if (assignments.Any(satisfyingRoles.Contains))
            return true;

        // OrgAdmin implicitly satisfies any project/team scope check within that org.
        if (scope is RoleScope.Project or RoleScope.Team)
            return await _repository.IsOrgAdminForScopeAsync(userId, scope, scopeId, ct);

        return false;
    }

    public Task<IReadOnlyList<RoleAssignment>> GetUserRolesAsync(Guid userId, CancellationToken ct = default)
        => _repository.GetForUserAsync(userId, ct);

    public Task<IReadOnlyList<RoleAssignment>> GetScopeRolesAsync(
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
        => _repository.GetForScopeAsync(scope, scopeId, ct);

    public async Task RemoveAllRolesAsync(
        Guid userId,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
    {
        var assignments = await _repository.GetUserAssignmentsAtScopeAsync(userId, scope, scopeId, ct);

        if (assignments.Count == 0) return;

        _repository.RemoveRange(assignments);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Removed {Count} role(s) from user {UserId} at {Scope}:{ScopeId}",
            assignments.Count, userId, scope, scopeId);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a RoleAssignment with exactly the scope column populated that
    /// matches <paramref name="scope"/>, leaving the other two null.
    /// </summary>
    private static RoleAssignment CreateAssignment(
        Guid userId, RoleType role, RoleScope scope, Guid scopeId, Guid? assignedBy)
    {
        var assignment = new RoleAssignment
        {
            UserId = userId,
            Role = role,
            Scope = scope,
            CreatedBy = assignedBy
        };

        switch (scope)
        {
            case RoleScope.Organization: assignment.OrganizationId = scopeId; break;
            case RoleScope.Project: assignment.ProjectId = scopeId; break;
            case RoleScope.Team: assignment.TeamId = scopeId; break;
            default: throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unhandled RoleScope value.");
        }

        return assignment;
    }

    /// <summary>
    /// Every role at least as privileged as <paramref name="minimumRole"/>. Lower enum value means
    /// more privileged, so this is every role whose value is &lt;= the requirement.
    /// </summary>
    private static RoleType[] RolesSatisfying(RoleType minimumRole) =>
        Enum.GetValues<RoleType>()
            .Where(role => (int)role <= (int)minimumRole)
            .ToArray();
}
