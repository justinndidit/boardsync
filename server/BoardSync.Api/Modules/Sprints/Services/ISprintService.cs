using BoardSync.Api.Modules.Sprints.DTOs;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.Sprints.Services;

public interface ISprintService
{
    /// <summary>Creates a sprint for a team.</summary>
    Task<SprintResponse> CreateAsync(Guid teamId, CreateSprintRequest request, Guid createdBy, CancellationToken ct = default);
    Task<SprintResponse> GetByIdAsync(Guid sprintId, CancellationToken ct = default);
    /// <summary>A team's sprints, newest first.</summary>
    Task<PagedResult<SprintSummaryResponse>> GetForTeamAsync(Guid teamId, PaginationQuery pagination, CancellationToken ct = default);

    /// <summary>The sprints of the team that builds a project.</summary>
    Task<PagedResult<SprintSummaryResponse>> GetForProjectAsync(Guid projectId, PaginationQuery pagination, CancellationToken ct = default);
    Task<SprintResponse?> GetActiveForProjectAsync(Guid projectId, CancellationToken ct = default);
    Task<SprintResponse> UpdateAsync(Guid sprintId, UpdateSprintRequest request, Guid updatedBy, CancellationToken ct = default);
    Task<SprintResponse> UpdateStatusAsync(Guid sprintId, SprintStatus newStatus, Guid updatedBy, CancellationToken ct = default);
    Task DeleteAsync(Guid sprintId, Guid deletedBy, CancellationToken ct = default);
    Task<SprintWorkItemResponse> AddWorkItemAsync(Guid sprintId, AddSprintWorkItemRequest request, Guid addedBy, CancellationToken ct = default);

    /// <summary>
    /// Whether this work item is a breakdown of something the sprint already contains — that is,
    /// whether its parent is in the sprint.
    /// </summary>
    /// <remarks>
    /// Lets a team member add or remove their own task breakdown without holding
    /// <c>sprint:scope</c>, because decomposing committed work does not change what the team
    /// committed to. An item with no parent, or a parent outside the sprint, is new scope.
    /// </remarks>
    Task<bool> IsDecompositionOfSprintWorkAsync(Guid sprintId, Guid workItemId, CancellationToken ct = default);
    Task RemoveWorkItemAsync(Guid sprintId, Guid workItemId, Guid removedBy, CancellationToken ct = default);
    Task<PagedResult<SprintWorkItemResponse>> GetWorkItemsAsync(Guid sprintId, PaginationQuery pagination, CancellationToken ct = default);
    /// <summary>
    /// Moves a single backlog item between two neighbours. One row is written.
    /// </summary>
    /// <returns>The item's new rank, so the caller can confirm where it landed.</returns>
    Task<decimal> MoveWorkItemAsync(
        Guid sprintId,
        Guid workItemId,
        MoveSprintWorkItemRequest request,
        CancellationToken ct = default);

    Task<MoveWorkItemCommandResponse> MoveWorkItemWithStateAsync(
        Guid sprintId,
        Guid workItemId,
        MoveWorkItemCommandRequest request,
        Guid changedBy,
        CancellationToken ct = default);

    Task ReorderWorkItemsAsync(Guid sprintId, ReorderSprintWorkItemsRequest request, CancellationToken ct = default);

    /// <summary>
    /// Close an active sprint.
    /// Marks it Completed and routes incomplete items to the backlog or a next sprint.
    /// </summary>
    Task<CloseSprintResponse> CloseAsync(Guid sprintId, CloseSprintRequest request, Guid closedBy, CancellationToken ct = default);
}