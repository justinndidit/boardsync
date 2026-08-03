using BoardSync.Api.Modules.Sprints.DTOs;

namespace BoardSync.Api.Modules.Sprints.Services;

public interface IBoardService
{
    /// <summary>Get (or auto-create with default columns) the board for a team.</summary>
    Task<BoardResponse> GetOrCreateForTeamAsync(Guid teamId, Guid createdBy, CancellationToken ct = default);
    Task<BoardResponse> GetByIdAsync(Guid boardId, CancellationToken ct = default);
    Task<BoardResponse> UpdateAsync(Guid boardId, UpdateBoardRequest request, Guid updatedBy, CancellationToken ct = default);
    Task<BoardColumnDetailResponse> AddColumnAsync(Guid boardId, CreateBoardColumnRequest request, Guid createdBy, CancellationToken ct = default);
    Task<BoardColumnDetailResponse> UpdateColumnAsync(Guid columnId, UpdateBoardColumnRequest request, Guid updatedBy, CancellationToken ct = default);
    Task DeleteColumnAsync(Guid columnId, CancellationToken ct = default);
    Task ReorderColumnsAsync(Guid boardId, ReorderBoardColumnsRequest request, CancellationToken ct = default);
}
