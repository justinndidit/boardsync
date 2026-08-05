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
    Task<Team?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<(IReadOnlyList<TeamResponse> Items, int TotalCount)> GetActiveTeamsInOrgAsync(Guid orgId, PaginationQuery pagination, CancellationToken ct = default);    /// <summary>
    /// Whether a team row exists at all, active or not. Used to distinguish "no such team" (404)
    /// from "team has no members" (empty page) when listing members.
    /// </summary>
    Task<bool> ExistsAsync(Guid teamId, CancellationToken ct = default);

    void Add(Team team);
    void Delete(Team team);

    Task SaveChangesAsync(CancellationToken ct = default);
}
