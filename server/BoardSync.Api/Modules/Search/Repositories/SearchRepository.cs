using BoardSync.Api.Data;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Search.Repositories;

/// <inheritdoc />
public class SearchRepository : ISearchRepository
{
    private readonly BoardSyncDbContext _context;

    public SearchRepository(BoardSyncDbContext context)
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

    public async Task<IReadOnlyList<SearchHit>> SearchOrganizationsAsync(
        IReadOnlyCollection<Guid> organizationIds,
        string term,
        int take,
        CancellationToken ct = default)
    {
        if (organizationIds.Count == 0) return [];

        var orgIds = AsList(organizationIds);

        return await _context.Organizations
            .Where(o => orgIds.Contains(o.Id) && o.IsActive
                        && (o.Name.ToLower().Contains(term) || o.Slug.ToLower().Contains(term)))
            .OrderBy(o => o.Name)
            .Take(take)
            .Select(o => new SearchHit(o.Id, o.Name, o.Slug))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchProjectsAsync(
        IReadOnlyCollection<Guid> organizationIds,
        string term,
        int take,
        CancellationToken ct = default)
    {
        if (organizationIds.Count == 0) return [];

        var orgIds = AsList(organizationIds);

        return await _context.Projects
            .Where(p => orgIds.Contains(p.OrganizationId) && p.IsActive
                        && (p.Name.ToLower().Contains(term) || p.Slug.ToLower().Contains(term)))
            .OrderBy(p => p.Name)
            .Take(take)
            .Select(p => new SearchHit(p.Id, p.Name, p.Slug))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchMembersAsync(
        IReadOnlyCollection<Guid> organizationIds,
        string term,
        int take,
        CancellationToken ct = default)
    {
        if (organizationIds.Count == 0) return [];

        var orgIds = AsList(organizationIds);

        // Order on the joined shape, not on a constructed SearchHit: EF cannot translate an
        // OrderBy that reads a property off a projected record and fails the whole request.
        return await _context.OrganizationMemberships
            .Where(m => orgIds.Contains(m.OrganizationId))
            .Select(m => m.UserId)
            .Distinct()
            .Join(
                _context.Users.Where(u => u.IsActive
                    && (u.DisplayName.ToLower().Contains(term)
                        || u.Email.ToLower().Contains(term))),
                uid => uid,
                u => u.Id,
                (uid, u) => new { u.Id, u.DisplayName, u.Email })
            .OrderBy(x => x.DisplayName)
            .Take(take)
            .Select(x => new SearchHit(x.Id, x.DisplayName, x.Email))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchWorkItemsAsync(
        IReadOnlyCollection<Guid> organizationIds,
        string term,
        int take,
        CancellationToken ct = default)
    {
        if (organizationIds.Count == 0) return [];

        var orgIds = AsList(organizationIds);

        // The project set stays a subquery rather than a materialized IN list that grows with the
        // caller's membership.
        var projectIds = _context.Projects
            .Where(p => orgIds.Contains(p.OrganizationId) && p.IsActive)
            .Select(p => p.Id);

        return await _context.WorkItems
            .Where(w => projectIds.Contains(w.ProjectId)
                        && w.IsActive
                        && w.Title.ToLower().Contains(term))
            .OrderByDescending(w => w.CreatedAt)
            .Take(take)
            .Select(w => new SearchHit(w.Id, w.Title, null))
            .ToListAsync(ct);
    }

    private static List<Guid> AsList(IReadOnlyCollection<Guid> ids) =>
        ids as List<Guid> ?? ids.ToList();
}
