using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.Sprints.Services;

public interface ISprintService
{
    Task<SprintResponse> CreateAsync(Guid teamId, CreateSprintRequest request, Guid createdBy, CancellationToken ct = default);
    Task<SprintResponse> GetByIdAsync(Guid sprintId, CancellationToken ct = default);
    Task<PagedResult<SprintSummaryResponse>> GetForTeamAsync(Guid teamId, PaginationQuery pagination, CancellationToken ct = default);
    Task<SprintResponse?> GetActiveForTeamAsync(Guid teamId, CancellationToken ct = default);
    Task<SprintResponse> UpdateAsync(Guid sprintId, UpdateSprintRequest request, Guid updatedBy, CancellationToken ct = default);
    Task<SprintResponse> UpdateStatusAsync(Guid sprintId, SprintStatus newStatus, Guid updatedBy, CancellationToken ct = default);
    Task DeleteAsync(Guid sprintId, Guid deletedBy, CancellationToken ct = default);
    Task<SprintWorkItemResponse> AddWorkItemAsync(Guid sprintId, AddSprintWorkItemRequest request, Guid addedBy, CancellationToken ct = default);
    Task RemoveWorkItemAsync(Guid sprintId, Guid workItemId, CancellationToken ct = default);
    Task<PagedResult<SprintWorkItemResponse>> GetWorkItemsAsync(Guid sprintId, PaginationQuery pagination, CancellationToken ct = default);
    Task ReorderWorkItemsAsync(Guid sprintId, ReorderSprintWorkItemsRequest request, CancellationToken ct = default);
}
