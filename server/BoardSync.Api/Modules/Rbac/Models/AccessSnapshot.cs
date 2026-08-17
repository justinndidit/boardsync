namespace BoardSync.Api.Modules.Rbac.Models;

/// <summary>
/// Where a project sits in the scope tree: the organization that owns it and the team that works
/// on it.
/// </summary>
/// <remarks>
/// <c>Project.AssignedTeamId</c> is a required, restricting foreign key, so every project has
/// exactly one of each. Both are needed to answer a project permission question: the organization
/// carries OrgAdmin inheritance, the team carries the membership edge.
/// </remarks>
public sealed record ProjectLocation(Guid OrganizationId, Guid AssignedTeamId);

/// <summary>
/// Everything the system knows about what one user has been granted, in a form that answers
/// permission questions without touching the database.
/// </summary>
/// <remarks>
/// <para>
/// Each dictionary maps a scope id to the <em>most privileged</em> role the user holds directly at
/// that scope — lowest <see cref="RoleType"/> value wins. Team membership is folded into
/// <see cref="TeamRoles"/> when the snapshot is built, because membership of a team is a grant on
/// that team whether or not a matching role row exists.
/// </para>
/// <para>
/// <b>This holds grants, not their consequences.</b> It deliberately does not expand OrgAdmin into
/// every project of the organization, or team membership into every project of the team. Expanding
/// would make the snapshot grow with the size of the organization rather than with the size of the
/// user's access, and would mean every new project invalidated every admin's snapshot. Instead the
/// expansion happens at question time in <see cref="Services.AccessEvaluator"/>, which needs only
/// the one scope being asked about and its position in the tree.
/// </para>
/// <para>
/// Consequently a snapshot is invalidated by changes to <em>this user's</em> grants and by nothing
/// else — with one exception, reassigning a project to a different team, which changes the tree
/// underneath everyone on both teams. See <c>AccessChangeHandlers</c>.
/// </para>
/// </remarks>
public sealed record AccessSnapshot(
    Dictionary<Guid, RoleType> OrganizationRoles,
    Dictionary<Guid, RoleType> TeamRoles,
    Dictionary<Guid, RoleType> ProjectRoles)
{
    /// <summary>A user with no grants anywhere. Every question against it answers no.</summary>
    public static AccessSnapshot Empty { get; } = new([], [], []);
}
