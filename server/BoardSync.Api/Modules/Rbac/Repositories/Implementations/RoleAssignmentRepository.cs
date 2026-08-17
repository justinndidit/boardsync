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

    public Task<int> RemoveAllInOrganizationAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken ct = default)
    {
        // Left as subqueries so the whole cascade is one DELETE rather than a fetch of every team
        // and project id followed by an IN list shipped back to the server.
        var teamIds = _context.Teams
            .Where(t => t.OrganizationId == organizationId)
            .Select(t => t.Id);

        var projectIds = _context.Projects
            .Where(p => p.OrganizationId == organizationId)
            .Select(p => p.Id);

        return _context.RoleAssignments
            .Where(ra => ra.UserId == userId
                         && (ra.OrganizationId == organizationId
                             || (ra.TeamId != null && teamIds.Contains(ra.TeamId.Value))
                             || (ra.ProjectId != null && projectIds.Contains(ra.ProjectId.Value))))
            .ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetMemberTeamIdsAsync(
        Guid userId,
        CancellationToken ct = default) =>
        await _context.TeamMemberships
            .Where(m => m.UserId == userId)
            .Select(m => m.TeamId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetTeamMemberUserIdsAsync(
        Guid teamId,
        CancellationToken ct = default) =>
        await _context.TeamMemberships
            .Where(m => m.TeamId == teamId)
            .Select(m => m.UserId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<RoleAssignment>> GetHoldersOfTeamPositionAsync(
        Guid teamId,
        RoleType position,
        CancellationToken ct = default) =>
        await _context.RoleAssignments
            .Where(ra => ra.TeamId == teamId && ra.Role == position)
            .ToListAsync(ct);

    public Task<ProjectLocation?> GetProjectLocationAsync(
        Guid projectId,
        CancellationToken ct = default) =>
        _context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new ProjectLocation(p.OrganizationId, p.AssignedTeamId))
            .FirstOrDefaultAsync(ct);

    public async Task<Guid?> GetTeamOrganizationIdAsync(
        Guid teamId,
        CancellationToken ct = default) =>
        await _context.Teams
            .Where(t => t.Id == teamId)
            .Select(t => (Guid?)t.OrganizationId)
            .FirstOrDefaultAsync(ct);

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
