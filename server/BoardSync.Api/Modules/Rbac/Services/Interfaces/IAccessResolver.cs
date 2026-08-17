using BoardSync.Api.Modules.Rbac.Models;

namespace BoardSync.Api.Modules.Rbac.Services.Interfaces;

/// <summary>
/// Loads a user's grants, and locates scopes in the tree so those grants can be interpreted.
/// </summary>
/// <remarks>
/// Split from <see cref="IRbacService"/> because the two have very different caching profiles. A
/// snapshot changes only when the user's own grants change; a scope's position in the tree barely
/// changes at all. Keeping them apart lets each be cached on its own terms.
/// </remarks>
public interface IAccessResolver
{
    /// <summary>Everything this user has been granted, anywhere.</summary>
    Task<AccessSnapshot> GetSnapshotAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Where a project sits in the tree, or null if it does not exist.</summary>
    Task<ProjectLocation?> GetProjectLocationAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>The organization owning a team, or null if the team does not exist.</summary>
    Task<Guid?> GetTeamOrganizationIdAsync(Guid teamId, CancellationToken ct = default);
}
