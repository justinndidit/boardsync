using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Services.Interfaces;
public interface ITeamService
{
    Task<TeamResponse> CreateAsync(Guid orgId, CreateTeamRequest request, Guid createdBy, CancellationToken ct = default);
    Task<TeamResponse> GetByIdAsync(Guid teamId, CancellationToken ct = default);
    // Task<PagedResult<TeamResponse>> GetForProjectAsync(Guid projectId, PaginationQuery pagination, CancellationToken ct = default);
    Task<TeamResponse> UpdateAsync(Guid teamId, UpdateTeamRequest request, Guid updatedBy, CancellationToken ct = default);
    Task<TeamMemberResponse> AddMemberAsync(Guid teamId, Guid userId, Guid addedBy, CancellationToken ct = default);
    Task RemoveMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default);
    Task<PagedResult<TeamMemberResponse>> GetMembersAsync(Guid teamId, PaginationQuery pagination, CancellationToken ct = default);
    Task<bool> IsMember(Guid teamId, Guid userId, CancellationToken ct = default);
}
