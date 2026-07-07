using BoardSync.Api.Data;
using BoardSync.Api.Modules.Rbac.Models;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Rbac.Services;

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
        // A role satisfies the requirement if its numeric value is <= minimumRole
        // (lower value = more privileged in the enum)
        var directMatch = await _context.RoleAssignments.AnyAsync(
            ra => ra.UserId == userId
                  && ra.Scope == scope
                  && ra.ScopeId == scopeId
                  && (int)ra.Role <= (int)minimumRole,
            ct);

        if (directMatch) return true;

        // OrgAdmin implicitly satisfies any lower-scope check — but we need the orgId to resolve this.
        // For project/team scopes we do a secondary lookup via the hierarchy stored in OrgProject module.
        // This is resolved through the context: if the user is OrgAdmin for any org that contains
        // the resource, they pass. We keep this simple by checking RoleScope.Organization rows.
        if (scope == RoleScope.Project || scope == RoleScope.Team)
        {
            // Find all orgs where this user is OrgAdmin and where the scopeId lives
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
