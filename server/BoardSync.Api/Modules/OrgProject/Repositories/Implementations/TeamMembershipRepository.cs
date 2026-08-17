using BoardSync.Api.Data;

using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.OrgProject.Repositories.Implementations;

public class TeamMembershipRepository : ITeamMembershipRepository
{
    private readonly BoardSyncDbContext _context;

    public TeamMembershipRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

  public void AddMembership(TeamMembership membership) => _context.TeamMemberships.Add(membership);

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

  public Task<TeamMembership?> GetMembershipAsync(Guid teamId, Guid userId, CancellationToken ct = default) =>
      _context.TeamMemberships.FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, ct);

  public Task<bool> IsMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default) =>
      _context.TeamMemberships.AnyAsync(m => m.TeamId == teamId && m.UserId == userId, ct);

  public void RemoveMembership(TeamMembership membership) => _context.TeamMemberships.Remove(membership);

  public Task<int> RemoveAllInOrganizationAsync(
      Guid userId,
      Guid organizationId,
      CancellationToken ct = default)
  {
    // Subquery rather than a materialized id list, matching the role-assignment cascade.
    var teamIds = _context.Teams
        .Where(t => t.OrganizationId == organizationId)
        .Select(t => t.Id);

    return _context.TeamMemberships
        .Where(m => m.UserId == userId && teamIds.Contains(m.TeamId))
        .ExecuteDeleteAsync(ct);
  }


  public Task SaveChangesAsync(CancellationToken ct = default)
  {
    throw new NotImplementedException();
  }
}