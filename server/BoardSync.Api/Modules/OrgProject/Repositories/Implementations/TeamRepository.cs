using BoardSync.Api.Data;
using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Repositories.Implementations;

/// <inheritdoc />
public class TeamRepository : ITeamRepository
{
    private readonly BoardSyncDbContext _context;

    public TeamRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    public Task<Team?> GetActiveByIdAsync(Guid teamId, CancellationToken ct = default) =>
        _context.Teams.FirstOrDefaultAsync(t => t.Id == teamId && t.IsActive, ct);

    public Task<Team?> GetByNameInOrgAsync(Guid orgId, string name, CancellationToken ct = default) =>
        _context.Teams.FirstOrDefaultAsync(
            t => t.OrganizationId == orgId && t.Name == name && t.IsActive, ct);

    public async Task<(IReadOnlyList<TeamResponse> Items, int TotalCount)> GetActiveTeamsInOrgAsync(Guid orgId, PaginationQuery pagination, CancellationToken ct = default)
    {
        var query = _context.Teams.Where(t => t.OrganizationId == orgId && t.IsActive);

        var total = await query.CountAsync(ct);

        var items = await query
                            .OrderBy(t => t.Name)
                            .Skip(pagination.Skip)
                            .Take(pagination.PageSize)
                            .Select(t => new TeamResponse(t.Id, t.OrganizationId, t.Name, t.Description, t.IsActive, t.Members.Count, t.CreatedAt))
                            .ToListAsync(ct);

        return (items, total);
    }

    public Task<bool> ExistsAsync(Guid teamId, CancellationToken ct = default) =>
        _context.Teams.AnyAsync(t => t.Id == teamId, ct);

    public Task<bool> ExistsActiveInOrgAsync(Guid orgId, Guid teamId, CancellationToken ct = default) =>
        _context.Teams.AnyAsync(t => t.Id == teamId && t.OrganizationId == orgId && t.IsActive, ct);

    public Task<int> GetAssignedProjectCountAsync(Guid teamId, CancellationToken ct = default) =>
        _context.Projects.CountAsync(p => p.AssignedTeamId == teamId && p.IsActive, ct);

    public Task<int> GetMemberCountAsync(Guid teamId, CancellationToken ct = default) =>
        _context.TeamMemberships.CountAsync(m => m.TeamId == teamId, ct);

    public void Add(Team team) => _context.Teams.Add(team);

    public void Delete(Team team) =>
        _context.Teams.Remove(team);

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);
}
