using BoardSync.Api.Data;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Rbac.Services.Implementations;

public class RbacService : IRbacService
{
    private readonly BoardSyncDbContext _context;
    private readonly ILogger<RbacService> _logger;

    public RbacService(BoardSyncDbContext context, ILogger<RbacService> logger)
    {
        _context = context;
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
        var existing = await _context.RoleAssignments.FirstOrDefaultAsync(
            ra => ra.UserId == userId && ra.Role == role && ra.Scope == scope && ra.ScopeId == scopeId, ct);

        if (existing != null)
            return existing;

        var assignment = new RoleAssignment
        {
            UserId = userId,
            Role = role,
            Scope = scope,
            ScopeId = scopeId,
            CreatedBy = assignedBy
        };

        _context.RoleAssignments.Add(assignment);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Assigned role {Role} to user {UserId} at {Scope}:{ScopeId}",
            role, userId, scope, scopeId);

        return assignment;
    }

    public async Task RemoveRoleAsync(Guid userId, RoleType role, RoleScope scope, Guid scopeId, CancellationToken ct = default)
    {
        var assignment = await _context.RoleAssignments.FirstOrDefaultAsync(
            ra => ra.UserId == userId && ra.Role == role && ra.Scope == scope && ra.ScopeId == scopeId, ct);

        if (assignment == null) return;

        _context.RoleAssignments.Remove(assignment);
        await _context.SaveChangesAsync(ct);

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
        // Load all role assignments for this user at this scope into memory,
        // then do the numeric privilege comparison in C#.
        // We cannot use (int)ra.Role in SQL because the column is stored as a
        // character varying (HasConversion<string>) and Postgres rejects integer casts on it.
        var assignments = await _context.RoleAssignments
            .Where(ra => ra.UserId == userId
                         && ra.Scope == scope
                         && ra.ScopeId == scopeId)
            .Select(ra => ra.Role)
            .ToListAsync(ct);

        var directMatch = assignments.Any(role => (int)role <= (int)minimumRole);
        if (directMatch) return true;

        // OrgAdmin implicitly satisfies any project/team scope check within that org.
        if (scope == RoleScope.Project || scope == RoleScope.Team)
        {
            var isOrgAdmin = await IsOrgAdminForScopeAsync(userId, scope, scopeId, ct);
            if (isOrgAdmin) return true;
        }

        return false;
    }

    public async Task<IReadOnlyList<RoleAssignment>> GetUserRolesAsync(Guid userId, CancellationToken ct = default)
    {
        return await _context.RoleAssignments
            .Where(ra => ra.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RoleAssignment>> GetScopeRolesAsync(RoleScope scope, Guid scopeId, CancellationToken ct = default)
    {
        return await _context.RoleAssignments
            .Where(ra => ra.Scope == scope && ra.ScopeId == scopeId)
            .ToListAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<bool> IsOrgAdminForScopeAsync(Guid userId, RoleScope scope, Guid scopeId, CancellationToken ct)
    {
        // Get all orgs where this user is OrgAdmin
        var orgAdminOrgIds = await _context.RoleAssignments
            .Where(ra => ra.UserId == userId && ra.Role == RoleType.OrgAdmin && ra.Scope == RoleScope.Organization)
            .Select(ra => ra.ScopeId)
            .ToListAsync(ct);

        if (orgAdminOrgIds.Count == 0) return false;

        if (scope == RoleScope.Project)
        {
            return await _context.Projects
                .AnyAsync(p => p.Id == scopeId && orgAdminOrgIds.Contains(p.OrganizationId), ct);
        }

        if (scope == RoleScope.Team)
        {
            return await _context.Teams
                .Include(t => t.Project)
                .AnyAsync(t => t.Id == scopeId && orgAdminOrgIds.Contains(t.Project.OrganizationId), ct);
        }

        return false;
    }
}
