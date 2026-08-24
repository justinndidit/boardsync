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

    public async Task<IReadOnlyCollection<string>> GetKeysInOrganizationAsync(
        Guid orgId, CancellationToken ct = default) =>
        await _context.Projects
            .Where(p => p.OrganizationId == orgId)
            .Select(p => p.Key)
            .ToListAsync(ct);

    public async Task<int> TakeNextWorkItemNumberAsync(Guid projectId, CancellationToken ct = default)
    {
        // Raw SQL because EF has no way to express "increment and give me what it was" as one
        // statement, and doing it as a read then a write would let two concurrent creates in the
        // same project take the same number. The row lock lasts only for this statement; creates in
        // different projects never contend.
        var numbers = await _context.Database
            .SqlQueryRaw<int>(
                """
                UPDATE org."Projects"
                SET "NextWorkItemNumber" = "NextWorkItemNumber" + 1
                WHERE "Id" = {0}
                RETURNING "NextWorkItemNumber" - 1 AS "Value"
                """, projectId)
            .ToListAsync(ct);

        return numbers.Count > 0
            ? numbers[0]
            : throw new InvalidOperationException($"Project '{projectId}' does not exist.");
    }

    public async Task<Guid?> GetOrganizationIdAsync(Guid projectId, CancellationToken ct = default) =>
        await _context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.OrganizationId)
            .FirstOrDefaultAsync(ct);

    public async Task<string> GetKeyAsync(Guid projectId, CancellationToken ct = default) =>
        await _context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => p.Key)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

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
