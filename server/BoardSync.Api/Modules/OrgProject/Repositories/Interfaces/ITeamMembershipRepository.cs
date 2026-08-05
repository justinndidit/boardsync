using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;


namespace BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;

public interface ITeamMembershipRepository
{
    Task<bool> IsMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default);

    Task<TeamMembership?> GetMembershipAsync(Guid teamId, Guid userId, CancellationToken ct = default);

    /// <summary>Members of a team ordered by display name, joined to their user profiles.</summary>
    Task<(IReadOnlyList<MemberRecord> Items, int TotalCount)> GetMembersAsync(
        Guid teamId, int skip, int take, CancellationToken ct = default);
    void AddMembership(TeamMembership membership);
    void RemoveMembership(TeamMembership membership);
    Task SaveChangesAsync(CancellationToken ct = default);
}