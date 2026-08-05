using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Services.Interfaces;
public interface IProjectService
{
    Task<ProjectResponse> CreateAsync(Guid orgId, CreateProjectRequest request, Guid createdBy, CancellationToken ct = default);
    Task<ProjectResponse> GetByIdAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// Whether an active project exists. Exposed so other modules can validate a project
    /// reference without querying the OrgProject module's tables directly.
    /// </summary>
    Task<bool> ExistsAsync(Guid projectId, CancellationToken ct = default);

    Task<PagedResult<ProjectSummaryResponse>> GetForOrgAsync(Guid orgId, PaginationQuery pagination, CancellationToken ct = default);
    Task<ProjectResponse> UpdateAsync(Guid projectId, UpdateProjectRequest request, Guid updatedBy, CancellationToken ct = default);

    /// <summary>
    /// Points the project at a different team. The new team must be active and belong to the
    /// same organization as the project.
    /// </summary>
    Task<ProjectResponse> AssignTeamAsync(Guid projectId, Guid teamId, Guid updatedBy, CancellationToken ct = default);
}
