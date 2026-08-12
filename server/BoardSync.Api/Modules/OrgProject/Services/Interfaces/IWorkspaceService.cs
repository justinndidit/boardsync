using BoardSync.Api.Modules.OrgProject.Domain.DTOs;

namespace BoardSync.Api.Modules.OrgProject.Services.Interfaces;

/// <summary>
/// Workspace-level reads — everything scoped to "the organizations this user belongs to" rather
/// than to one organization, project or team.
/// </summary>
public interface IWorkspaceService
{
    /// <summary>Dashboard counters for one user's workspace.</summary>
    Task<WorkspaceSummaryResponse> GetSummaryAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Organizations the user is a member of. The activity feed spans all of them, so it needs
    /// the set before it can query.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetOrganizationIdsAsync(Guid userId, CancellationToken ct = default);
}
