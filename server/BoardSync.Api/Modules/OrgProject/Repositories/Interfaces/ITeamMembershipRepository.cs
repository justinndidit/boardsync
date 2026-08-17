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

    /// <summary>
    /// Deletes a user's membership of every team in one organization.
    /// </summary>
    /// <returns>How many rows were deleted.</returns>
    /// <remarks>
    /// Executes immediately rather than staging, for the same reason as
    /// <see cref="Rbac.Repositories.Interfaces.IRoleAssignmentRepository.RemoveAllInOrganizationAsync"/>:
    /// the alternative loads every membership across the organization to mark each one deleted.
    /// Run it inside a transaction, which it enlists in.
    /// </remarks>
    Task<int> RemoveAllInOrganizationAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}