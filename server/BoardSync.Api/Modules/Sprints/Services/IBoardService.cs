using BoardSync.Api.Modules.Sprints.DTOs;

namespace BoardSync.Api.Modules.Sprints.Services;

public interface IBoardService
{
    /// <summary>Get (or auto-create with default columns) the board for a project.</summary>
    Task<BoardResponse> GetOrCreateForProjectAsync(Guid projectId, Guid createdBy, CancellationToken ct = default);
    Task<BoardResponse> GetByIdAsync(Guid boardId, CancellationToken ct = default);
    Task<BoardResponse> UpdateAsync(Guid boardId, UpdateBoardRequest request, Guid updatedBy, CancellationToken ct = default);
    Task<BoardColumnDetailResponse> AddColumnAsync(Guid boardId, CreateBoardColumnRequest request, Guid createdBy, CancellationToken ct = default);
    Task<BoardColumnDetailResponse> UpdateColumnAsync(Guid columnId, UpdateBoardColumnRequest request, Guid updatedBy, CancellationToken ct = default);
    Task DeleteColumnAsync(Guid columnId, Guid deletedBy, CancellationToken ct = default);
    Task ReorderColumnsAsync(Guid boardId, ReorderBoardColumnsRequest request, Guid reorderedBy, CancellationToken ct = default);

    /// <summary>
    /// Project owning a column, resolved column → board → project.
    /// </summary>
    /// <remarks>
    /// Columns are addressed by their own IDs, so authorization has to resolve the owning project
    /// before it can decide anything. Exposed here so the controller can ask the module rather than
    /// reaching into the database itself.
    /// </remarks>
    /// <exception cref="Shared.Kernel.Exceptions.NotFoundException">No such column.</exception>
    Task<Guid> GetProjectIdForColumnAsync(Guid columnId, CancellationToken ct = default);
}
