using BoardSync.Api.Modules.WorkItems.DTOs;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.WorkItems.Services;

public interface IWorkItemService
{
    // ── CRUD ──────────────────────────────────────────────────────────────────
    Task<WorkItemResponse> CreateAsync(Guid projectId, CreateWorkItemRequest request, Guid createdBy, CancellationToken ct = default);
    Task<WorkItemResponse> GetByIdAsync(Guid workItemId, CancellationToken ct = default);
    Task<PagedResult<WorkItemSummaryResponse>> GetForProjectAsync(Guid projectId, WorkItemFilterQuery filter, CancellationToken ct = default);
    Task<WorkItemResponse> UpdateAsync(Guid workItemId, UpdateWorkItemRequest request, Guid updatedBy, CancellationToken ct = default);

    /// <summary>
    /// Applies only the fields the caller supplied. See <see cref="PatchWorkItemRequest"/>.
    /// </summary>
    Task<WorkItemResponse> PatchAsync(Guid workItemId, PatchWorkItemRequest request, Guid updatedBy, CancellationToken ct = default);
    Task<WorkItemResponse> UpdateStateAsync(
        Guid workItemId,
        WorkItemState newState,
        Guid updatedBy,
        long? expectedVersion = null,
        CancellationToken ct = default);
    Task<(WorkItem Item, WorkItemState OldState)> StageStateTransitionAsync(
        Guid workItemId,
        WorkItemState newState,
        Guid updatedBy,
        long? expectedVersion = null,
        CancellationToken ct = default,
        bool allowSameState = false);
    Task DeleteAsync(Guid workItemId, Guid deletedBy, CancellationToken ct = default);

    // ── Comments ──────────────────────────────────────────────────────────────
    Task<WorkItemCommentResponse> AddCommentAsync(Guid workItemId, AddWorkItemCommentRequest request, Guid authorId, CancellationToken ct = default);
    Task<WorkItemCommentResponse> UpdateCommentAsync(Guid commentId, UpdateWorkItemCommentRequest request, Guid updatedBy, CancellationToken ct = default);
    Task DeleteCommentAsync(Guid commentId, Guid deletedBy, CancellationToken ct = default);
    Task<PagedResult<WorkItemCommentResponse>> GetCommentsAsync(Guid workItemId, PaginationQuery pagination, CancellationToken ct = default);

    // ── History ───────────────────────────────────────────────────────────────
    Task<PagedResult<WorkItemHistoryResponse>> GetHistoryAsync(Guid workItemId, PaginationQuery pagination, CancellationToken ct = default);

    // ── Links ─────────────────────────────────────────────────────────────────
    Task<WorkItemLinkResponse> AddLinkAsync(Guid workItemId, AddWorkItemLinkRequest request, Guid createdBy, CancellationToken ct = default);
    Task RemoveLinkAsync(Guid linkId, Guid removedBy, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItemLinkResponse>> GetLinksAsync(Guid workItemId, CancellationToken ct = default);

    // ── Scope resolution ──────────────────────────────────────────────────────
    // Links and comments are addressed by their own IDs, so a caller must be able to resolve the
    // owning project in order to authorize the request before it is carried out.

    /// <summary>Project owning the link. Throws <see cref="Shared.Kernel.Exceptions.NotFoundException"/> if it does not exist.</summary>
    Task<Guid> GetProjectIdForLinkAsync(Guid linkId, CancellationToken ct = default);

    /// <summary>Project owning the comment. Throws <see cref="Shared.Kernel.Exceptions.NotFoundException"/> if it does not exist.</summary>
    Task<Guid> GetProjectIdForCommentAsync(Guid commentId, CancellationToken ct = default);
}
