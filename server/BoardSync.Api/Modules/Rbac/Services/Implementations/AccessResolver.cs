using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Repositories.Interfaces;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;

namespace BoardSync.Api.Modules.Rbac.Services.Implementations;

/// <summary>
/// Builds access snapshots from the database.
/// </summary>
/// <remarks>
/// Two queries, both indexed on <c>UserId</c>: the user's role assignments and the teams they are a
/// member of. The result is bounded by how much the user has been granted, not by how large their
/// organization is — an OrgAdmin of a thousand-project organization has the same snapshot size as
/// an OrgAdmin of one.
/// </remarks>
public sealed class AccessResolver : IAccessResolver
{
    private readonly IRoleAssignmentRepository _repository;

    public AccessResolver(IRoleAssignmentRepository repository)
    {
        _repository = repository;
    }

    public async Task<AccessSnapshot> GetSnapshotAsync(Guid userId, CancellationToken ct = default)
    {
        // Guid.Empty is what CurrentUserContext yields when the subject claim is missing or
        // unparseable. It is not a user, and must never accumulate grants by accident.
        if (userId == Guid.Empty)
            return AccessSnapshot.Empty;

        var assignments = await _repository.GetForUserAsync(userId, ct);
        var memberTeamIds = await _repository.GetMemberTeamIdsAsync(userId, ct);

        var organizations = new Dictionary<Guid, List<RoleType>>();
        var teams = new Dictionary<Guid, List<RoleType>>();
        var projects = new Dictionary<Guid, List<RoleType>>();

        foreach (var assignment in assignments)
        {
            // Read the scope column rather than trusting Scope alone: the check constraint
            // guarantees exactly one is populated, and that column is the authoritative target.
            if (assignment.OrganizationId is Guid orgId)
                Record(organizations, orgId, assignment.Role);
            else if (assignment.TeamId is Guid teamId)
                Record(teams, teamId, assignment.Role);
            else if (assignment.ProjectId is Guid projectId)
                Record(projects, projectId, assignment.Role);
        }

        // Membership of a team is a grant on that team in its own right. Folding it in here means a
        // membership row with no matching role row still works — and it is what carries access down
        // to the projects the team is assigned to.
        foreach (var teamId in memberTeamIds)
            Record(teams, teamId, RoleType.TeamMember);

        return new AccessSnapshot(organizations, teams, projects);
    }

    public Task<ProjectLocation?> GetProjectLocationAsync(Guid projectId, CancellationToken ct = default) =>
        _repository.GetProjectLocationAsync(projectId, ct);

    public Task<Guid?> GetTeamOrganizationIdAsync(Guid teamId, CancellationToken ct = default) =>
        _repository.GetTeamOrganizationIdAsync(teamId, ct);

    /// <summary>
    /// Adds <paramref name="role"/> to what is held at <paramref name="scopeId"/>.
    /// </summary>
    /// <remarks>
    /// Every role is kept, not just the "best" one — they are not ordered, and someone who is both
    /// Scrum Master and Team Lead needs both sets of permissions. Duplicates are skipped because
    /// team membership and an explicit TeamMember row commonly say the same thing.
    /// </remarks>
    private static void Record(
        Dictionary<Guid, List<RoleType>> target, Guid scopeId, RoleType role)
    {
        if (!target.TryGetValue(scopeId, out var roles))
            target[scopeId] = roles = [];

        if (!roles.Contains(role))
            roles.Add(role);
    }
}
