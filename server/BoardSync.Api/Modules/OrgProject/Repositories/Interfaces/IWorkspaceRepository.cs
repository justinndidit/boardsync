using BoardSync.Api.Modules.OrgProject.Domain.DTOs;

namespace BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;

/// <summary>
/// Cross-organization reads for the workspace dashboard.
/// </summary>
/// <remarks>
/// Separate from the per-aggregate repositories because these questions do not belong to one
/// organization, project or team — they span every organization a user is in.
/// </remarks>
public interface IWorkspaceRepository
{
    /// <summary>Organizations the user is a member of.</summary>
    Task<IReadOnlyList<Guid>> GetOrganizationIdsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The four dashboard counters, resolved in one round trip.
    /// </summary>
    /// <remarks>
    /// Composed as subqueries rather than four sequential queries, and the organization and project
    /// id sets stay in the database instead of being pulled into memory and shipped back as IN
    /// lists that grow with the user's membership.
    /// </remarks>
    Task<WorkspaceSummaryResponse> GetSummaryAsync(Guid userId, CancellationToken ct = default);
}
