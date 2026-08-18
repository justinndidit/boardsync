using BoardSync.Api.Modules.Backlog.DTOs;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.Backlog.Services;

public interface IBacklogService
{
    /// <summary>
    /// Get the unscheduled product backlog for a project (items not assigned to any sprint),
    /// ordered by rank ascending. Optionally filtered to a specific team.
    /// </summary>
    Task<PagedResult<BacklogItemResponse>> GetForProjectAsync(
        Guid projectId,
        Guid? teamId,
        PaginationQuery pagination,
        CancellationToken ct = default);

    /// <summary>
    /// Ensure a work item has a backlog entry for this project.
    /// Idempotent — returns the existing entry if one already exists.
    /// </summary>
    Task<BacklogItemResponse> AddAsync(
        Guid projectId,
        AddToBacklogRequest request,
        Guid addedBy,
        CancellationToken ct = default);

    /// <summary>Remove a work item from the backlog entirely.</summary>
    Task RemoveAsync(Guid projectId, Guid workItemId, CancellationToken ct = default);

    /// <summary>
    /// Re-rank backlog items by accepting the desired work item ID order.
    /// Any IDs not in the list keep their existing rank relative to each other
    /// at the bottom.
    /// </summary>
    Task ReorderAsync(
        Guid projectId,
        ReorderBacklogRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Pull a set of backlog items into a sprint.
    /// Also creates SprintWorkItem rows so the sprint service sees them immediately.
    /// </summary>
    Task<BacklogBulkOperationResponse> MoveToSprintAsync(
        Guid projectId,
        MoveToSprintRequest request,
        Guid movedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Return items from the named sprint back to the unscheduled backlog.
    /// Clears SprintId on the backlog entry and asks the Sprints module to drop the membership.
    /// </summary>
    Task<BacklogBulkOperationResponse> ReturnToBacklogAsync(
        Guid projectId,
        ReturnToBacklogRequest request,
        Guid returnedBy,
        CancellationToken ct = default);
}
