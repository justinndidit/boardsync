using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;
using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;

namespace BoardSync.Api.Modules.OrgProject.Services.Implementations;

/// <inheritdoc />
public class WorkspaceService : IWorkspaceService
{
    private readonly IWorkspaceRepository _repository;
    private readonly IRbacService _rbac;

    public WorkspaceService(IWorkspaceRepository repository, IRbacService rbac)
    {
        _repository = repository;
        _rbac = rbac;
    }

    public async Task<WorkspaceSummaryResponse> GetSummaryAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        // All three resolve from one cached access snapshot, so this is one lookup, not three.
        var scope = new WorkspaceScope(
            Organizations: await _rbac.GetVisibleOrganizationIdsAsync(userId, Permissions.OrgRead, ct),
            Projects: await _rbac.GetProjectVisibilityAsync(userId, Permissions.ProjectRead, ct),
            WorkItems: await _rbac.GetProjectVisibilityAsync(userId, Permissions.WorkItemRead, ct));

        return await _repository.GetSummaryAsync(userId, scope, ct);
    }

    public Task<Guid[]> GetReadableOrganizationIdsAsync(Guid userId, CancellationToken ct = default)
        => _rbac.GetVisibleOrganizationIdsAsync(userId, Permissions.OrgRead, ct);
}
