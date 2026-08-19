using System.Text.Json.Serialization;

namespace BoardSync.Api.Modules.Rbac.Models;

/// <summary>
/// Roles available in the system.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every role belongs to exactly one scope</b>, and no name means two different things at two
/// different scopes. That is the point of the vocabulary: reading <c>Contributor</c> on a role
/// assignment tells you it is a grant on a project, without having to look at which column is
/// populated. <see cref="RolePermissions.IsValidAt"/> declares the pairing and the
/// <c>CK_RoleAssignment_RoleMatchesScope</c> check constraint enforces it, so an ill-scoped grant is
/// unrepresentable rather than merely discouraged.
/// </para>
/// <para>
/// <see cref="Viewer"/> is the one name held at two scopes, and deliberately: read-only on a team
/// and read-only on a project are the same idea applied to different things, unlike the
/// <c>Reader</c> it replaced, which meant "organization member" at one scope and "read-only" at the
/// other two.
/// </para>
/// <para>
/// <b>The numeric values carry no meaning.</b> They were once a privilege ladder, compared with
/// <c>&lt;=</c> to answer "at least a TeamMember?". That stopped working the moment team positions
/// arrived: a Scrum Master and a Product Owner are peers holding different authority, and no
/// ordering of them is truthful. What a role permits is declared in <see cref="RolePermissions"/>
/// and resolved by union, never by comparison.
/// </para>
/// <para>
/// Stored as names, so adding a value is a non-breaking change and removing one needs a data
/// migration. 40 and 50 are left unused: they were <c>Reader</c> and <c>User</c>, retired by
/// <c>Stage3_ScopedRoleNames</c>, and reusing a number whose name changed meaning is the one way
/// these values could still mislead.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoleType
{
    // ── Organization ──────────────────────────────────────────────────────────

    /// <summary>Administers the entire organization and everything inside it.</summary>
    OrgAdmin = 10,

    /// <summary>
    /// Belongs to the organization. Carries organization read and nothing else — every member holds
    /// it from the moment they join, and access to anything inside comes from a team or project
    /// grant rather than from this.
    /// </summary>
    Member = 11,

    // ── Team ──────────────────────────────────────────────────────────────────

    /// <summary>Leads a team: its composition, and who holds its positions.</summary>
    TeamLead = 21,

    /// <summary>Owns a team's process: runs the sprint lifecycle.</summary>
    ScrumMaster = 22,

    /// <summary>Owns a team's backlog: decides what a sprint commits to.</summary>
    ProductOwner = 23,

    /// <summary>Contributes on a team: orders the sprint, and contributes to the team's projects.</summary>
    TeamMember = 30,

    // ── Project ───────────────────────────────────────────────────────────────

    /// <summary>Administers one project: its settings, its board, and its direct role grants.</summary>
    ProjectAdmin = 20,

    /// <summary>Contributes to one project: creates, edits and comments on its work.</summary>
    Contributor = 31,

    // ── Team and project ──────────────────────────────────────────────────────

    /// <summary>Read-only: may view a team's sprints, or a project's board and work items.</summary>
    Viewer = 32
}

/// <summary>
/// The roles that are appointments rather than plain grants — held by one person per team at a
/// time, and handed over as an explicit act.
/// </summary>
public static class TeamPositions
{
    /// <summary>The positions, in the order a UI should list them.</summary>
    public static readonly IReadOnlyList<RoleType> All =
        [RoleType.TeamLead, RoleType.ScrumMaster, RoleType.ProductOwner];

    /// <summary>Whether a role is a singular team position rather than an ordinary grant.</summary>
    public static bool Includes(RoleType role) => All.Contains(role);
}

/// <summary>
/// The scope at which a role assignment applies.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoleScope
{
    Organization,
    Project,
    Team
}
