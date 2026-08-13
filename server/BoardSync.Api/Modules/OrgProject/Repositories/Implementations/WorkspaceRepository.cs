using BoardSync.Api.Data;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;
using BoardSync.Api.Modules.WorkItems.Models;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.OrgProject.Repositories.Implementations;

/// <inheritdoc />
public class WorkspaceRepository : IWorkspaceRepository
{
    private readonly BoardSyncDbContext _context;

    public WorkspaceRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Guid>> GetOrganizationIdsForUserAsync(
        Guid userId,
        CancellationToken ct = default) =>
        await _context.OrganizationMemberships
            .Where(m => m.UserId == userId)
            .Select(m => m.OrganizationId)
            .ToListAsync(ct);

    public async Task<WorkspaceSummaryResponse> GetSummaryAsync(Guid userId, CancellationToken ct = default)
    {
        // Left unmaterialized on purpose — composing these as IQueryable turns them into
        // subqueries of the projection below, so all four counters come back in one round trip.
        var orgIds = _context.OrganizationMemberships
            .Where(m => m.UserId == userId)
            .Select(m => m.OrganizationId);

        var projectIds = _context.Projects
            .Where(p => orgIds.Contains(p.OrganizationId) && p.IsActive)
            .Select(p => p.Id);

        // Anchored on the caller's own row purely to give the projection a single row to hang off.
        var summary = await _context.Users
            .Where(u => u.Id == userId)
            .Select(u => new WorkspaceSummaryResponse(
                orgIds.Count(),
                projectIds.Count(),
                _context.OrganizationMemberships
                    .Where(m => orgIds.Contains(m.OrganizationId))
                    .Select(m => m.UserId)
                    .Distinct()
                    .Count(),
                _context.WorkItems.Count(w =>
                    projectIds.Contains(w.ProjectId)
                    && w.IsActive
                    && w.State != WorkItemState.Closed
                    && w.State != WorkItemState.Resolved)))
            .FirstOrDefaultAsync(ct);

        // A user row that has gone missing under an otherwise valid token is an empty workspace,
        // not a failure.
        return summary ?? new WorkspaceSummaryResponse(0, 0, 0, 0);
    }
}
