using BoardSync.Api.Data;
using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.OrgProject.Repositories.Implementations;

/// <inheritdoc />
public class OrganizationRepository : IOrganizationRepository
{
    private readonly BoardSyncDbContext _context;

    public OrganizationRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    // ── Organizations ─────────────────────────────────────────────────────────

    public Task<Organization?> GetActiveAsync(Guid organizationId, CancellationToken ct = default) =>
        _context.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId && o.IsActive, ct);

    public Task<Organization?> GetActiveBySlugAsync(string slug, CancellationToken ct = default) =>
        _context.Organizations.FirstOrDefaultAsync(o => o.Slug == slug && o.IsActive, ct);

    public Task<bool> ExistsActiveAsync(Guid organizationId, CancellationToken ct = default) =>
        _context.Organizations.AnyAsync(o => o.Id == organizationId && o.IsActive, ct);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default) =>
        _context.Organizations.AnyAsync(o => o.Slug == slug, ct);

    public async Task<OrganizationCounts> GetCountsAsync(Guid organizationId, CancellationToken ct = default) =>
        await _context.Organizations
            .Where(o => o.Id == organizationId)
            .Select(o => new OrganizationCounts(o.Members.Count, o.Projects.Count(p => p.IsActive)))
            .FirstOrDefaultAsync(ct)
        ?? new OrganizationCounts(0, 0);

    public async Task<(IReadOnlyList<OrganizationSummaryRecord> Items, int TotalCount)> GetForUserAsync(
        Guid userId, int skip, int take, CancellationToken ct = default)
    {
        var query = _context.Organizations
            .Where(o => o.IsActive && o.Members.Any(m => m.UserId == userId));

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(o => o.Name)
            .Skip(skip)
            .Take(take)
            .Select(o => new OrganizationSummaryRecord(
                o.Id,
                o.Slug,
                o.Name,
                o.AvatarUrl,
                o.Description,
                o.IsActive,
                o.Members.Count,
                o.Projects.Count(p => p.IsActive),
                o.CreatedAt))
            .ToListAsync(ct);

        return (items, total);
    }

    public void Add(Organization organization) => _context.Organizations.Add(organization);

    // ── Memberships ───────────────────────────────────────────────────────────

    public Task<bool> IsMemberAsync(Guid organizationId, Guid userId, CancellationToken ct = default) =>
        _context.OrganizationMemberships
            .AnyAsync(m => m.OrganizationId == organizationId && m.UserId == userId, ct);

    public Task<OrganizationMembership?> GetMembershipAsync(Guid organizationId, Guid userId, CancellationToken ct = default) =>
        _context.OrganizationMemberships
            .FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == userId, ct);

    public async Task<(IReadOnlyList<MemberRecord> Items, int TotalCount)> GetMembersAsync(
        Guid organizationId, int skip, int take, CancellationToken ct = default)
    {
        var query = _context.OrganizationMemberships.Where(m => m.OrganizationId == organizationId);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(m => m.JoinedAt)
            .Skip(skip)
            .Take(take)
            .Join(_context.Users,
                m => m.UserId,
                u => u.Id,
                (m, u) => new MemberRecord(m.UserId, u.DisplayName, u.Email, u.ProfilePictureUrl, m.JoinedAt))
            .ToListAsync(ct);

        return (items, total);
    }

    public void AddMembership(OrganizationMembership membership) =>
        _context.OrganizationMemberships.Add(membership);

    public void RemoveMembership(OrganizationMembership membership) =>
        _context.OrganizationMemberships.Remove(membership);

    // ── Unit of work ──────────────────────────────────────────────────────────

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _context.Database.BeginTransactionAsync(ct);
            await operation(ct);
            await tx.CommitAsync(ct);
        });
    }
}
