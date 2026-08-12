using BoardSync.Api.Data;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Rbac.Repositories.Implementations;

/// <inheritdoc />
public class RoleAssignmentRepository : IRoleAssignmentRepository
{
    private readonly BoardSyncDbContext _context;

    public RoleAssignmentRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    public Task<RoleAssignment?> GetAsync(
        Guid userId,
        RoleType role,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default) =>
        WhereScope(_context.RoleAssignments, scope, scopeId)
            .FirstOrDefaultAsync(ra => ra.UserId == userId && ra.Role == role, ct);

    public async Task<IReadOnlyList<RoleType>> GetRolesAtScopeAsync(
        Guid userId,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default) =>
        await WhereScope(_context.RoleAssignments, scope, scopeId)
            .Where(ra => ra.UserId == userId)
            .Select(ra => ra.Role)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<RoleAssignment>> GetForUserAsync(
        Guid userId,
        CancellationToken ct = default) =>
        await _context.RoleAssignments
            .Where(ra => ra.UserId == userId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<RoleAssignment>> GetForScopeAsync(
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default) =>
        await WhereScope(_context.RoleAssignments, scope, scopeId).ToListAsync(ct);

    public async Task<IReadOnlyList<RoleAssignment>> GetUserAssignmentsAtScopeAsync(
        Guid userId,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default) =>
        await WhereScope(_context.RoleAssignments, scope, scopeId)
            .Where(ra => ra.UserId == userId)
            .ToListAsync(ct);

    public Task<bool> IsOrgAdminForScopeAsync(
        Guid userId,
        RoleScope scope,
        Guid scopeId,
        CancellationToken ct = default)
    {
        // The organizations this user administers, left as a subquery so the check below is one
        // statement rather than a fetch followed by an in-memory Contains.
        var adminOrgIds = _context.RoleAssignments
            .Where(ra => ra.UserId == userId
                         && ra.Role == RoleType.OrgAdmin
                         && ra.Scope == RoleScope.Organization
                         && ra.OrganizationId != null)
            .Select(ra => ra.OrganizationId!.Value);

        return scope switch
        {
            RoleScope.Project => _context.Projects
                .AnyAsync(p => p.Id == scopeId && adminOrgIds.Contains(p.OrganizationId), ct),

            RoleScope.Team => _context.Teams
                .AnyAsync(t => t.Id == scopeId && adminOrgIds.Contains(t.OrganizationId), ct),

            // Organization scope has no parent to inherit from — a direct assignment is the only
            // way to hold a role there.
            _ => Task.FromResult(false)
        };
    }

    public void Add(RoleAssignment assignment) => _context.RoleAssignments.Add(assignment);

    public void Remove(RoleAssignment assignment) => _context.RoleAssignments.Remove(assignment);

    public void RemoveRange(IEnumerable<RoleAssignment> assignments) =>
        _context.RoleAssignments.RemoveRange(assignments);

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);

    /// <summary>
    /// Filters to the given scope, comparing against whichever of
    /// OrganizationId/ProjectId/TeamId matches it.
    /// </summary>
    private static IQueryable<RoleAssignment> WhereScope(
        IQueryable<RoleAssignment> query, RoleScope scope, Guid scopeId) => scope switch
    {
        RoleScope.Organization => query.Where(ra => ra.OrganizationId == scopeId),
        RoleScope.Project      => query.Where(ra => ra.ProjectId == scopeId),
        RoleScope.Team         => query.Where(ra => ra.TeamId == scopeId),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unhandled RoleScope value.")
    };
}
