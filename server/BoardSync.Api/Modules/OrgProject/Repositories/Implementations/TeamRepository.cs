using BoardSync.Api.Data;
using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.OrgProject.Repositories.Implementations;

/// <inheritdoc />
public class TeamRepository : ITeamRepository
{
    private readonly BoardSyncDbContext _context;

    public TeamRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    // ── Teams ─────────────────────────────────────────────────────────────────

    public Task<Team?> GetActiveAsync(Guid teamId, CancellationToken ct = default) =>
        _context.Teams.FirstOrDefaultAsync(t => t.Id == teamId && t.IsActive, ct);

    public Task<bool> ExistsAsync(Guid teamId, CancellationToken ct = default) =>
        _context.Teams.AnyAsync(t => t.Id == teamId, ct);

    public Task<bool> NameExistsInProjectAsync(Guid projectId, string name, CancellationToken ct = default) =>
        _context.Teams.AnyAsync(t => t.ProjectId == projectId && t.Name == name, ct);

    public Task<int> GetMemberCountAsync(Guid teamId, CancellationToken ct = default) =>
        _context.TeamMemberships.CountAsync(m => m.TeamId == teamId, ct);

    public async Task<(IReadOnlyList<TeamSummaryRecord> Items, int TotalCount)> GetForProjectAsync(
        Guid projectId, int skip, int take, CancellationToken ct = default)
    {
        var query = _context.Teams.Where(t => t.ProjectId == projectId && t.IsActive);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(t => t.Name)
            .Skip(skip)
            .Take(take)
            .Select(t => new TeamSummaryRecord(
                t.Id, t.ProjectId, t.Name, t.Description, t.IsActive, t.Members.Count, t.CreatedAt))
            .ToListAsync(ct);

        return (items, total);
    }

    public void Add(Team team) => _context.Teams.Add(team);

    // ── Memberships ───────────────────────────────────────────────────────────

    public Task<bool> IsMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default) =>
        _context.TeamMemberships.AnyAsync(m => m.TeamId == teamId && m.UserId == userId, ct);

    public Task<TeamMembership?> GetMembershipAsync(Guid teamId, Guid userId, CancellationToken ct = default) =>
        _context.TeamMemberships.FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, ct);

    public async Task<(IReadOnlyList<MemberRecord> Items, int TotalCount)> GetMembersAsync(
        Guid teamId, int skip, int take, CancellationToken ct = default)
    {
        // The join happens before paging so the page can be ordered by display name in SQL.
        // Ordering must key off the joined shape, not a constructed MemberRecord — EF cannot
        // translate OrderBy over a projected record's property and throws at query time.
        var query = _context.TeamMemberships
            .Where(m => m.TeamId == teamId)
            .Join(_context.Users,
                m => m.UserId,
                u => u.Id,
                (m, u) => new { m.UserId, u.DisplayName, u.Email, u.ProfilePictureUrl, m.JoinedAt });

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(x => x.DisplayName)
            .Skip(skip)
            .Take(take)
            .Select(x => new MemberRecord(x.UserId, x.DisplayName, x.Email, x.ProfilePictureUrl, x.JoinedAt))
            .ToListAsync(ct);

        return (items, total);
    }

    public void AddMembership(TeamMembership membership) => _context.TeamMemberships.Add(membership);

    public void RemoveMembership(TeamMembership membership) => _context.TeamMemberships.Remove(membership);

    // ── Unit of work ──────────────────────────────────────────────────────────

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);
}
