using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Repositories.Interfaces;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;

namespace BoardSync.Api.Modules.Rbac.Services.Implementations;

public class RbacService : IRbacService
{
    private readonly IRoleAssignmentRepository _repository;
    private readonly IAccessResolver _resolver;
    private readonly ILogger<RbacService> _logger;

    public RbacService(
        IRoleAssignmentRepository repository,
        IAccessResolver resolver,
        ILogger<RbacService> logger)
    {
        _repository = repository;
        _resolver = resolver;
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

    /// <remarks>
    /// Loads the user's grants once, then locates the scope in the tree so
    /// <see cref="AccessEvaluator"/> can apply inheritance to it. Organization questions skip the
    /// lookup entirely — the organization is the root, so there is nothing above it to resolve.
    /// </remarks>
    public async Task<bool> HasPermissionAsync(
        Guid userId,
        string permission,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
    {
        var snapshot = await _resolver.GetSnapshotAsync(userId, ct);

        switch (scope)
        {
            case RoleScope.Organization:
                return AccessEvaluator.GrantsAtOrganization(snapshot, permission, scopeId);

            case RoleScope.Team:
            {
                // Skip locating the team when the user holds nothing anywhere that could inherit
                // down to it. Saves a query on every check made by someone with no org grants.
                var organizationId = snapshot.OrganizationRoles.Count == 0
                    ? null
                    : await _resolver.GetTeamOrganizationIdAsync(scopeId, ct);

                return AccessEvaluator.GrantsAtTeam(snapshot, permission, scopeId, organizationId);
            }

            case RoleScope.Project:
            {
                // Same shortcut: without an organization or team grant, only a direct project
                // assignment can grant anything, and that needs no lookup.
                var location = snapshot.OrganizationRoles.Count == 0 && snapshot.TeamRoles.Count == 0
                    ? null
                    : await _resolver.GetProjectLocationAsync(scopeId, ct);

                return AccessEvaluator.GrantsAtProject(snapshot, permission, scopeId, location);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unhandled RoleScope value.");
        }
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

    public async Task<bool> HasPermissionAnywhereAsync(
        Guid userId,
        string permission,
        CancellationToken ct = default)
    {
        // Answered entirely from the snapshot — no scope to locate, so no tree lookup.
        var snapshot = await _resolver.GetSnapshotAsync(userId, ct);

        return AccessEvaluator.GrantsAnywhere(snapshot, permission);
    }

    public async Task<Guid?> TransferTeamPositionAsync(
        Guid teamId,
        RoleType position,
        Guid toUserId,
        Guid assignedBy,
        CancellationToken ct = default)
    {
        if (!TeamPositions.Includes(position))
            throw new ArgumentOutOfRangeException(
                nameof(position), position, "Not a team position.");

        var holders = await _repository.GetHoldersOfTeamPositionAsync(teamId, position, ct);
        var previous = holders.FirstOrDefault(h => h.UserId != toUserId);

        // Remove every current holder, not just the one we happened to read first. The unique index
        // should make more than one impossible, but a position silently held by two people is
        // exactly the state this operation exists to prevent, so it is cleared rather than assumed.
        foreach (var holder in holders.Where(h => h.UserId != toUserId))
            _repository.Remove(holder);

        var alreadyHeld = holders.Any(h => h.UserId == toUserId);

        if (!alreadyHeld)
        {
            var assignment = new RoleAssignment
            {
                UserId = toUserId,
                Role = position,
                Scope = RoleScope.Team,
                TeamId = teamId,
                CreatedBy = assignedBy
            };

            _repository.Add(assignment);
        }

        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Team {TeamId} position {Position} transferred from {Previous} to {UserId} by {ActingUser}",
            teamId, position, previous?.UserId, toUserId, assignedBy);

        return previous?.UserId;
    }

    public async Task<Guid?> VacateTeamPositionAsync(
        Guid teamId,
        RoleType position,
        CancellationToken ct = default)
    {
        if (!TeamPositions.Includes(position))
            throw new ArgumentOutOfRangeException(
                nameof(position), position, "Not a team position.");

        var holders = await _repository.GetHoldersOfTeamPositionAsync(teamId, position, ct);

        if (holders.Count == 0) return null;

        _repository.RemoveRange(holders);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("Team {TeamId} position {Position} vacated", teamId, position);

        return holders[0].UserId;
    }

    public async Task RemoveAllRolesInOrganizationAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken ct = default)
    {
        // Deletes in the database rather than through the change tracker, so there is nothing left
        // to save afterwards.
        var removed = await _repository.RemoveAllInOrganizationAsync(userId, organizationId, ct);

        if (removed > 0)
            _logger.LogInformation(
                "Removed {Count} role(s) from user {UserId} across organization {OrgId} and everything in it",
                removed, userId, organizationId);
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
}
