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
    Task<WorkItemResponse> UpdateStateAsync(Guid workItemId, WorkItemState newState, Guid updatedBy, CancellationToken ct = default);
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
}
