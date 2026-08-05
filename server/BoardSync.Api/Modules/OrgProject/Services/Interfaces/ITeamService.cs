using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Services.Interfaces;
public interface ITeamService
{
    Task<TeamResponse> CreateAsync(Guid orgId, CreateTeamRequest request, Guid createdBy, CancellationToken ct = default);
    Task<TeamResponse> GetByIdAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Active teams in an organization. Teams belong to the organization, not to a project —
    /// a project selects one of these as its assigned team.
    /// </summary>
    Task<PagedResult<TeamResponse>> GetForOrgAsync(Guid orgId, PaginationQuery pagination, CancellationToken ct = default);

    Task<TeamResponse> UpdateAsync(Guid teamId, UpdateTeamRequest request, Guid updatedBy, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes a team by clearing its active flag. Teams are never hard-deleted because
    /// projects reference them with a restricting foreign key; archiving a team that still has
    /// projects assigned is rejected.
    /// </summary>
    Task DeactivateAsync(Guid teamId, Guid deactivatedBy, CancellationToken ct = default);

    Task<TeamMemberResponse> AddMemberAsync(Guid teamId, Guid userId, Guid addedBy, CancellationToken ct = default);
    Task RemoveMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default);
    Task<PagedResult<TeamMemberResponse>> GetMembersAsync(Guid teamId, PaginationQuery pagination, CancellationToken ct = default);

    /// <summary>Whether a user is currently a member of the team.</summary>
    Task<bool> IsMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default);
}
