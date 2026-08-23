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
    /// <summary>
    /// Whether this project lets someone certify work assigned to them.
    /// </summary>
    /// <remarks>
    /// A missing project answers false — the safe reading, and the caller will fail on the item's
    /// own lookup anyway.
    /// </remarks>
    Task<bool> AllowsSelfCertificationAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// Takes the next work item number for a project, atomically.
    /// </summary>
    /// <remarks>
    /// Runs in the caller's transaction, so a number taken by a create that then fails rolls back
    /// with it rather than leaving a permanent gap in what people read as a continuous list.
    /// </remarks>
    Task<int> TakeNextWorkItemNumberAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>The project's short key, or an empty string if it no longer exists.</summary>
    Task<string> GetKeyAsync(Guid projectId, CancellationToken ct = default);

    Task<bool> ExistsAsync(Guid projectId, CancellationToken ct = default);

    Task<PagedResult<ProjectSummaryResponse>> GetForOrgAsync(Guid orgId, PaginationQuery pagination, CancellationToken ct = default);
    Task<ProjectResponse> UpdateAsync(Guid projectId, UpdateProjectRequest request, Guid updatedBy, CancellationToken ct = default);

    /// <summary>
    /// Points the project at a different team. The new team must be active and belong to the
    /// same organization as the project.
    /// </summary>
    Task<ProjectResponse> AssignTeamAsync(Guid projectId, Guid teamId, Guid updatedBy, CancellationToken ct = default);
}
