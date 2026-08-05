using BoardSync.Api.Modules.OrgProject.Domain.Models;

namespace BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;

/// <summary>
/// Persistence for the Project aggregate. Pure unit of work — see
/// <see cref="IOrganizationRepository"/> for the save semantics.
/// </summary>
public interface IProjectRepository
{
    /// <summary>Active project by ID, tracked for mutation, or null.</summary>
    Task<Project?> GetActiveAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Whether an active project exists, without loading it.</summary>
    Task<bool> ExistsActiveAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Whether the slug is taken within the organization (slugs are unique per org, not globally).</summary>
    Task<bool> SlugExistsInOrganizationAsync(Guid organizationId, string slug, CancellationToken ct = default);

    /// <summary>Number of active teams in the project.</summary>
    Task<int> GetActiveTeamCountAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Active projects in an organization, ordered by name.</summary>
    Task<(IReadOnlyList<Project> Items, int TotalCount)> GetForOrganizationAsync(
        Guid organizationId, int skip, int take, CancellationToken ct = default);

    void Add(Project project);

    Task SaveChangesAsync(CancellationToken ct = default);
}
