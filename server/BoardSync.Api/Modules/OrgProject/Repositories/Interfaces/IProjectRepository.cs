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

    /// <summary>
    /// Whether the project permits self-certification, without loading it. False if it is gone.
    /// </summary>
    Task<bool> AllowsSelfCertificationAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Project keys already in use in an organization, for deriving a free one.</summary>
    Task<IReadOnlyCollection<string>> GetKeysInOrganizationAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Takes the next work item number for a project, atomically.
    /// </summary>
    /// <remarks>
    /// One <c>UPDATE … RETURNING</c>, so two concurrent creates in the same project cannot be handed
    /// the same number. It must run in the caller's transaction: a number allocated by a create that
    /// then fails has to roll back, or people read a permanent gap in what looks like a continuous
    /// list.
    /// </remarks>
    Task<int> TakeNextWorkItemNumberAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>The project's short key, or an empty string if it no longer exists.</summary>
    Task<string> GetKeyAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Whether the slug is taken within the organization (slugs are unique per org, not globally).</summary>
    Task<bool> SlugExistsInOrganizationAsync(Guid organizationId, string slug, CancellationToken ct = default);

    /// <summary>Active projects in an organization, ordered by name.</summary>
    Task<(IReadOnlyList<Project> Items, int TotalCount)> GetForOrganizationAsync(
        Guid organizationId, int skip, int take, CancellationToken ct = default);

    void Add(Project project);

    Task SaveChangesAsync(CancellationToken ct = default);
}
