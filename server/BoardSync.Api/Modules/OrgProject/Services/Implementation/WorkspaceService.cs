using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;
using BoardSync.Api.Modules.OrgProject.Services.Interfaces;

namespace BoardSync.Api.Modules.OrgProject.Services.Implementations;

/// <inheritdoc />
public class WorkspaceService : IWorkspaceService
{
    private readonly IWorkspaceRepository _repository;

    public WorkspaceService(IWorkspaceRepository repository)
    {
        _repository = repository;
    }

    public Task<WorkspaceSummaryResponse> GetSummaryAsync(Guid userId, CancellationToken ct = default)
        => _repository.GetSummaryAsync(userId, ct);

    public Task<IReadOnlyList<Guid>> GetOrganizationIdsAsync(Guid userId, CancellationToken ct = default)
        => _repository.GetOrganizationIdsForUserAsync(userId, ct);
}
