using BoardSync.Api.Modules.OrgProject.Domain.DTOs;

namespace BoardSync.Api.Modules.OrgProject.Services.Interfaces;

/// <summary>
/// Workspace-level reads — everything scoped to what this user can see across every organization,
/// rather than to one organization, project or team.
/// </summary>
public interface IWorkspaceService
{
    /// <summary>Dashboard counters for one user's workspace.</summary>
    Task<WorkspaceSummaryResponse> GetSummaryAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Organizations the user may read. The activity feed spans all of them, so it needs the set
    /// before it can query.
    /// </summary>
    /// <remarks>
    /// Was "organizations the user is a member of", read straight off <c>OrganizationMemberships</c>.
    /// The two sets coincide today, since joining an organization grants <c>Member</c> and
    /// <c>Member</c> carries <c>org:read</c> — but that is a fact about the current role table, not a
    /// rule, and the feed should follow the permission rather than the membership row.
    /// </remarks>
    Task<Guid[]> GetReadableOrganizationIdsAsync(Guid userId, CancellationToken ct = default);
}
