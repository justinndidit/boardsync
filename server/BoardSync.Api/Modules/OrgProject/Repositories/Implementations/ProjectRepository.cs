using BoardSync.Api.Data;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.OrgProject.Repositories.Implementations;

/// <inheritdoc />
public class ProjectRepository : IProjectRepository
{
    private readonly BoardSyncDbContext _context;

    public ProjectRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    public Task<Project?> GetActiveAsync(Guid projectId, CancellationToken ct = default) =>
        _context.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.IsActive, ct);

    public Task<bool> ExistsActiveAsync(Guid projectId, CancellationToken ct = default) =>
        _context.Projects.AnyAsync(p => p.Id == projectId && p.IsActive, ct);

    public Task<bool> AllowsSelfCertificationAsync(Guid projectId, CancellationToken ct = default) =>
        _context.Projects.AnyAsync(p => p.Id == projectId && p.AllowSelfCertification, ct);

    public Task<bool> SlugExistsInOrganizationAsync(Guid organizationId, string slug, CancellationToken ct = default) =>
        _context.Projects.AnyAsync(p => p.OrganizationId == organizationId && p.Slug == slug, ct);

    // public Task<int> GetActiveTeamCountAsync(Guid projectId, CancellationToken ct = default) =>
    //     _context.Teams.CountAsync(t => t.ProjectId == projectId && t.IsActive, ct);

    public async Task<(IReadOnlyList<Project> Items, int TotalCount)> GetForOrganizationAsync(
        Guid organizationId, int skip, int take, CancellationToken ct = default)
    {
        var query = _context.Projects.Where(p => p.OrganizationId == organizationId && p.IsActive);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(p => p.Name)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return (items, total);
    }

    public void Add(Project project) => _context.Projects.Add(project);

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);
}
