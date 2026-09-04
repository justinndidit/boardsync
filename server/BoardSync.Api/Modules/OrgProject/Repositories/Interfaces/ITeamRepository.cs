using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Shared.Kernel;


namespace BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;

/// <summary>
/// Persistence for the Team aggregate and its memberships. Pure unit of work — see
/// <see cref="IOrganizationRepository"/> for the save semantics.
/// </summary>
public interface ITeamRepository
{
    /// <summary>Active team by ID, tracked for mutation, or null.</summary>
    Task<Team?> GetActiveByIdAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>Team by ID regardless of active status, tracked for mutation, or null.</summary>
    Task<Team?> GetByIdIncludingInactiveAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Active team with the given name inside one organization, or null. Name uniqueness is
    /// per-organization, so the org must be part of the lookup — a global name search would
    /// let one organization's team names block another's.
    /// </summary>
    Task<Team?> GetByNameInOrgAsync(Guid orgId, string name, CancellationToken ct = default);

    Task<(IReadOnlyList<TeamResponse> Items, int TotalCount)> GetActiveTeamsInOrgAsync(Guid orgId, PaginationQuery pagination, CancellationToken ct = default);

    /// <summary>
    /// Archived (inactive) teams in an organization. Returns only teams where IsActive is false,
    /// filtered by organization, ordered by name, paged.
    /// </summary>
    Task<(IReadOnlyList<TeamResponse> Items, int TotalCount)> GetArchivedTeamsInOrgAsync(Guid orgId, PaginationQuery pagination, CancellationToken ct = default);

    /// <summary>
    /// Whether a team row exists at all, active or not. Used to distinguish "no such team" (404)
    /// from "team has no members" (empty page) when listing members.
    /// </summary>
    Task<bool> ExistsAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>Whether an active team with this ID belongs to the given organization.</summary>
    Task<bool> ExistsActiveInOrgAsync(Guid orgId, Guid teamId, CancellationToken ct = default);

    /// <summary>Number of active projects currently assigned to this team.</summary>
    Task<int> GetAssignedProjectCountAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>Number of members on this team.</summary>
    Task<int> GetMemberCountAsync(Guid teamId, CancellationToken ct = default);

    void Add(Team team);
    void Delete(Team team);

    Task SaveChangesAsync(CancellationToken ct = default);

    /// <inheritdoc cref="IOrganizationRepository.ExecuteInTransactionAsync" />
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default);
}
