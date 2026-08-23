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

    public async Task<WorkspaceSummaryResponse> GetSummaryAsync(
        Guid userId,
        WorkspaceScope scope,
        CancellationToken ct = default)
    {
        if (scope.IsEmpty)
            return new WorkspaceSummaryResponse(0, 0, 0, 0);

        var organizationIds = scope.Organizations;

        // Left unmaterialized on purpose — composing these as IQueryable turns them into
        // subqueries of the projection below, so all four counters come back in one round trip.
        var readableProjectIds = _context.Projects
            .Where(scope.Projects.Predicate())
            .Where(p => p.IsActive)
            .Select(p => p.Id);

        var workItemProjectIds = _context.Projects
            .Where(scope.WorkItems.Predicate())
            .Where(p => p.IsActive)
            .Select(p => p.Id);

        // Anchored on the caller's own row purely to give the projection a single row to hang off.
        var summary = await _context.Users
            .Where(u => u.Id == userId)
            .Select(u => new WorkspaceSummaryResponse(
                organizationIds.Length,
                readableProjectIds.Count(),
                _context.OrganizationMemberships
                    .Where(m => organizationIds.Contains(m.OrganizationId))
                    .Select(m => m.UserId)
                    .Distinct()
                    .Count(),
                _context.WorkItems.Count(w =>
                    workItemProjectIds.Contains(w.ProjectId)
                    && w.IsActive
                    && w.State != WorkItemState.Closed
                    && w.State != WorkItemState.Resolved)))
            .FirstOrDefaultAsync(ct);

        // A user row that has gone missing under an otherwise valid token is an empty workspace,
        // not a failure.
        return summary ?? new WorkspaceSummaryResponse(0, 0, 0, 0);
    }
}
