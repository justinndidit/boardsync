using BoardSync.Api.Modules.OrgProject.Domain.Models;

namespace BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;

/// <summary>
/// Persistence for the Team aggregate and its memberships. Pure unit of work — see
/// <see cref="IOrganizationRepository"/> for the save semantics.
/// </summary>
public interface ITeamRepository
{
    // ── Teams ─────────────────────────────────────────────────────────────────

    /// <summary>Active team by ID, tracked for mutation, or null.</summary>
    Task<Team?> GetActiveAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Whether a team row exists at all, active or not. Used to distinguish "no such team" (404)
    /// from "team has no members" (empty page) when listing members.
    /// </summary>
    Task<bool> ExistsAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>Whether the name is taken within the project (names are unique per project).</summary>
    Task<bool> NameExistsInProjectAsync(Guid projectId, string name, CancellationToken ct = default);

    Task<int> GetMemberCountAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>Active teams in a project, ordered by name, with member counts.</summary>
    Task<(IReadOnlyList<TeamSummaryRecord> Items, int TotalCount)> GetForProjectAsync(
        Guid projectId, int skip, int take, CancellationToken ct = default);

    void Add(Team team);

    // ── Memberships ───────────────────────────────────────────────────────────

    Task<bool> IsMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default);

    Task<TeamMembership?> GetMembershipAsync(Guid teamId, Guid userId, CancellationToken ct = default);

    /// <summary>Members of a team ordered by display name, joined to their user profiles.</summary>
    Task<(IReadOnlyList<MemberRecord> Items, int TotalCount)> GetMembersAsync(
        Guid teamId, int skip, int take, CancellationToken ct = default);

    void AddMembership(TeamMembership membership);

    void RemoveMembership(TeamMembership membership);

    // ── Unit of work ──────────────────────────────────────────────────────────

    Task SaveChangesAsync(CancellationToken ct = default);
}
