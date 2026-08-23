using BoardSync.Api.Data;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.Rbac.Models;
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

    public async Task<IReadOnlyList<SearchHit>> SearchOrganizationsAsync(
        Guid[] organizationIds,
        string term,
        int take,
        CancellationToken ct = default)
    {
        if (organizationIds.Length == 0) return [];

        return await _context.Organizations
            .Where(o => organizationIds.Contains(o.Id) && o.IsActive
                        && (o.Name.ToLower().Contains(term) || o.Slug.ToLower().Contains(term)))
            .OrderBy(o => o.Name)
            .Take(take)
            .Select(o => new SearchHit(o.Id, o.Name, o.Slug))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchMembersAsync(
        Guid[] organizationIds,
        string term,
        int take,
        CancellationToken ct = default)
    {
        if (organizationIds.Length == 0) return [];

        // Order on the joined shape, not on a constructed SearchHit: EF cannot translate an
        // OrderBy that reads a property off a projected record and fails the whole request.
        return await _context.OrganizationMemberships
            .Where(m => organizationIds.Contains(m.OrganizationId))
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

    public async Task<IReadOnlyList<SearchHit>> SearchProjectsAsync(
        ProjectVisibility visibility,
        string term,
        int take,
        CancellationToken ct = default)
    {
        if (visibility.IsEmpty) return [];

        return await _context.Projects
            .Where(visibility.Predicate())
            .Where(p => p.IsActive
                        && (p.Name.ToLower().Contains(term) || p.Slug.ToLower().Contains(term)))
            .OrderBy(p => p.Name)
            .Take(take)
            .Select(p => new SearchHit(p.Id, p.Name, p.Slug))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchWorkItemsAsync(
        ProjectVisibility visibility,
        string term,
        int take,
        CancellationToken ct = default)
    {
        if (visibility.IsEmpty) return [];

        // Left unmaterialized so the readable-project set becomes a subquery of the work item
        // query rather than a round trip whose result is shipped straight back as an IN list. The
        // predicate itself is three `= ANY(@p)` tests against arrays sized by the caller's grants,
        // so the SQL is the same shape whether they hold one project or administer an organization.
        var visibleProjectIds = _context.Projects
            .Where(visibility.Predicate())
            .Where(p => p.IsActive)
            .Select(p => p.Id);

        return await _context.WorkItems
            .Where(w => visibleProjectIds.Contains(w.ProjectId)
                        && w.IsActive
                        && w.Title.ToLower().Contains(term))
            .OrderByDescending(w => w.CreatedAt)
            .Take(take)
            .Select(w => new SearchHit(w.Id, w.Title, null))
            .ToListAsync(ct);
    }
}
