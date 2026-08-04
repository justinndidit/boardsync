using BoardSync.Api.Modules.WorkItems.DTOs;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.WorkItems.Repository;

/// <summary>
/// Data access for the WorkItems module. Owns every query against the <c>work</c> schema
/// (work items, tags, comments, history, links) so that no other layer touches the DbContext.
///
/// Mutations are staged in-memory and only persisted by <see cref="SaveChangesAsync"/>, which
/// lets the service decide the transaction boundary (e.g. a work item, its tags and its first
/// history row are written in a single save).
/// </summary>
public interface IWorkItemRepository
{
    // ── Work items ────────────────────────────────────────────────────────────

    /// <summary>Active work item by ID, or null.</summary>
    Task<WorkItem?> GetActiveAsync(Guid workItemId, CancellationToken ct = default);

    /// <summary>Active work item by ID with its tags loaded, or null.</summary>
    Task<WorkItem?> GetActiveWithTagsAsync(Guid workItemId, CancellationToken ct = default);

    /// <summary>
    /// Work item by ID with tags loaded, regardless of <c>IsActive</c>. Used when building a
    /// response for an item that was just soft-deleted or updated.
    /// </summary>
    Task<WorkItem?> GetWithTagsAsync(Guid workItemId, CancellationToken ct = default);

    /// <summary>Active work item scoped to a project — used to validate a parent reference.</summary>
    Task<WorkItem?> GetActiveInProjectAsync(Guid workItemId, Guid projectId, CancellationToken ct = default);

    /// <summary>Filtered, paginated slice of a project's active work items, with tags loaded.</summary>
    Task<(IReadOnlyList<WorkItem> Items, int TotalCount)> GetForProjectAsync(
        Guid projectId,
        WorkItemFilterQuery filter,
        int skip,
        int take,
        CancellationToken ct = default);

    /// <summary>Number of active children per parent, for the given parent IDs.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetChildCountsAsync(
        IEnumerable<Guid> parentIds,
        CancellationToken ct = default);

    /// <summary>Number of active children of a single work item.</summary>
    Task<int> GetChildCountAsync(Guid workItemId, CancellationToken ct = default);

    void Add(WorkItem item);

    // ── Tags ──────────────────────────────────────────────────────────────────

    void AddTag(WorkItemTag tag);
    void RemoveTag(WorkItemTag tag);

    // ── Comments ──────────────────────────────────────────────────────────────

    Task<WorkItemComment?> GetCommentAsync(Guid commentId, CancellationToken ct = default);

    Task<(IReadOnlyList<WorkItemComment> Items, int TotalCount)> GetCommentsAsync(
        Guid workItemId,
        int skip,
        int take,
        CancellationToken ct = default);

    Task<int> GetCommentCountAsync(Guid workItemId, CancellationToken ct = default);

    void AddComment(WorkItemComment comment);
    void RemoveComment(WorkItemComment comment);

    // ── History ───────────────────────────────────────────────────────────────

    Task<(IReadOnlyList<WorkItemHistory> Items, int TotalCount)> GetHistoryAsync(
        Guid workItemId,
        int skip,
        int take,
        CancellationToken ct = default);

    void AddHistory(WorkItemHistory entry);

    // ── Links ─────────────────────────────────────────────────────────────────

    Task<WorkItemLink?> GetLinkAsync(Guid linkId, CancellationToken ct = default);

    /// <summary>Outgoing links for a work item, with the target work item loaded.</summary>
    Task<IReadOnlyList<WorkItemLink>> GetLinksWithTargetAsync(Guid workItemId, CancellationToken ct = default);

    Task<bool> LinkExistsAsync(Guid sourceId, Guid targetId, WorkItemLinkType linkType, CancellationToken ct = default);

    void AddLink(WorkItemLink link);
    void RemoveLink(WorkItemLink link);

    // ── Scope resolution ──────────────────────────────────────────────────────
    // Links and comments are addressed by their own IDs, so authorization needs a way to find
    // the owning project before touching them.

    /// <summary>Project owning the link's source work item, or null if the link does not exist.</summary>
    Task<Guid?> GetProjectIdForLinkAsync(Guid linkId, CancellationToken ct = default);

    /// <summary>Project owning the comment's work item, or null if the comment does not exist.</summary>
    Task<Guid?> GetProjectIdForCommentAsync(Guid commentId, CancellationToken ct = default);

    // ── Unit of work ──────────────────────────────────────────────────────────

    /// <summary>Persists everything staged since the last save.</summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
