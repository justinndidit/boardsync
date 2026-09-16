using BoardSync.Api.Data;
using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.OrgProject.Repositories.Implementations;

/// <inheritdoc />
public class OrganizationInvitationRepository : IOrganizationInvitationRepository
{
    private readonly BoardSyncDbContext _context;

    public OrganizationInvitationRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    public Task<OrganizationInvitation?> GetByTokenHashAsync(
        string tokenHash, CancellationToken ct = default) =>
        _context.OrganizationInvitations
            .Include(i => i.Organization)
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);

    public Task<OrganizationInvitation?> GetAsync(
        Guid organizationId, Guid invitationId, CancellationToken ct = default) =>
        _context.OrganizationInvitations
            .FirstOrDefaultAsync(
                i => i.Id == invitationId && i.OrganizationId == organizationId, ct);

    public Task<OrganizationInvitation?> GetOpenForEmailAsync(
        Guid organizationId, string email, DateTime asOf, CancellationToken ct = default) =>
        _context.OrganizationInvitations
            .FirstOrDefaultAsync(
                i => i.OrganizationId == organizationId
                     && i.Email == email
                     && i.AcceptedAt == null
                     && i.RevokedAt == null
                     && i.ExpiresAt > asOf,
                ct);

    public async Task<IReadOnlyList<OrganizationInvitation>> ListAsync(
        Guid organizationId, bool openOnly, DateTime asOf, CancellationToken ct = default)
    {
        var query = _context.OrganizationInvitations
            .Where(i => i.OrganizationId == organizationId);

        if (openOnly)
        {
            query = query.Where(
                i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > asOf);
        }

        return await query
            .OrderByDescending(i => i.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public void Add(OrganizationInvitation invitation) =>
        _context.OrganizationInvitations.Add(invitation);

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);
}
