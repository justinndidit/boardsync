using BoardSync.Api.Modules.OrgProject.DTOs;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Services;

public interface IOrganizationService
{
    Task<OrganizationResponse> CreateAsync(CreateOrganizationRequest request, Guid createdBy, CancellationToken ct = default);
    Task<OrganizationResponse> GetByIdAsync(Guid orgId, CancellationToken ct = default);
    Task<OrganizationResponse> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<PagedResult<OrganizationSummaryResponse>> GetForUserAsync(Guid userId, PaginationQuery pagination, CancellationToken ct = default);
    Task<OrganizationResponse> UpdateAsync(Guid orgId, UpdateOrganizationRequest request, Guid updatedBy, CancellationToken ct = default);
    Task AddMemberAsync(Guid orgId, Guid userId, Guid addedBy, CancellationToken ct = default);
    Task RemoveMemberAsync(Guid orgId, Guid userId, CancellationToken ct = default);
    Task<bool> IsMemberAsync(Guid orgId, Guid userId, CancellationToken ct = default);
}

public interface IProjectService
{
    Task<ProjectResponse> CreateAsync(Guid orgId, CreateProjectRequest request, Guid createdBy, CancellationToken ct = default);
    Task<ProjectResponse> GetByIdAsync(Guid projectId, CancellationToken ct = default);
    Task<PagedResult<ProjectSummaryResponse>> GetForOrgAsync(Guid orgId, PaginationQuery pagination, CancellationToken ct = default);
    Task<ProjectResponse> UpdateAsync(Guid projectId, UpdateProjectRequest request, Guid updatedBy, CancellationToken ct = default);
}

public interface ITeamService
{
    Task<TeamResponse> CreateAsync(Guid projectId, CreateTeamRequest request, Guid createdBy, CancellationToken ct = default);
    Task<TeamResponse> GetByIdAsync(Guid teamId, CancellationToken ct = default);
    Task<PagedResult<TeamResponse>> GetForProjectAsync(Guid projectId, PaginationQuery pagination, CancellationToken ct = default);
    Task<TeamResponse> UpdateAsync(Guid teamId, UpdateTeamRequest request, Guid updatedBy, CancellationToken ct = default);
    Task<TeamMemberResponse> AddMemberAsync(Guid teamId, Guid userId, Guid addedBy, CancellationToken ct = default);
    Task RemoveMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default);
    Task<PagedResult<TeamMemberResponse>> GetMembersAsync(Guid teamId, PaginationQuery pagination, CancellationToken ct = default);
}
