using System.Text.Json.Serialization;
using BoardSync.Api.Shared.Metadata;

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
    [DisplayMetadata("Organization Admin", 10,
        Description = "Administers the organization and everything inside it.")]
    OrgAdmin = 10,

    /// <summary>
    /// Belongs to the organization. Carries organization read and nothing else — every member holds
    /// it from the moment they join, and access to anything inside comes from a team or project
    /// grant rather than from this.
    /// </summary>
    [DisplayMetadata("Member", 20,
        Description = "Belongs to the organization. Access to anything inside comes from a team or project grant.")]
    Member = 11,

    // ── Team ──────────────────────────────────────────────────────────────────

    /// <summary>Leads a team: its composition, and who holds its positions.</summary>
    [DisplayMetadata("Team Lead", 30,
        Description = "Leads the team: its composition, and who holds its positions.")]
    TeamLead = 21,

    /// <summary>Owns a team's process: runs the sprint lifecycle on the team's projects.</summary>
    [DisplayMetadata("Scrum Master", 40,
        Description = "Runs the sprint lifecycle on the team's projects.")]
    ScrumMaster = 22,

    /// <summary>Owns a team's backlog: decides what the sprints of the team's projects commit to.</summary>
    [DisplayMetadata("Product Owner", 50,
        Description = "Decides what the sprints of the team's projects commit to.")]
    ProductOwner = 23,

    /// <summary>Contributes on a team, and so to the team's projects and their sprints.</summary>
    [DisplayMetadata("Team Member", 70,
        Description = "Contributes on the team, and so to its projects and their sprints.")]
    TeamMember = 30,

    // ── Project ───────────────────────────────────────────────────────────────

    /// <summary>Administers one project: its settings, its board, and its direct role grants.</summary>
    [DisplayMetadata("Project Admin", 60,
        Description = "Administers one project: its settings, its board, and its role grants.")]
    ProjectAdmin = 20,

    /// <summary>Contributes to one project: creates, edits and comments on its work.</summary>
    [DisplayMetadata("Contributor", 80,
        Description = "Creates, edits and comments on the project's work.")]
    Contributor = 31,

    // ── Team and project ──────────────────────────────────────────────────────

    /// <summary>
    /// Tests a team's or a project's work, and certifies it as done.
    /// </summary>
    /// <remarks>
    /// Held at either scope, for the same reason <see cref="Viewer"/> is: testing a team's work and
    /// testing one project's work are the same idea applied to different things. Deliberately not a
    /// <see cref="TeamPositions">position</see> — a team can and should have several testers, unlike
    /// its single Scrum Master.
    /// </remarks>
    [DisplayMetadata("Tester", 85,
        Description = "Tests the work and certifies it as done.")]
    Tester = 33,

    /// <summary>Read-only: may view a team, or a project's board, sprints and work items.</summary>
    [DisplayMetadata("Viewer", 90, Description = "Read-only.")]
    Viewer = 32,

    // ── Not held by people ────────────────────────────────────────────────────

    /// <summary>
    /// What a connected git installation may do on a project it feeds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Held only by a <see cref="PrincipalType.Integration"/> principal, never granted to a person —
    /// there is no endpoint that hands it out, and it is deliberately absent from the roles a picker
    /// offers.
    /// </para>
    /// <para>
    /// <b>What it lacks is the point.</b> It permits contribution and carries neither
    /// <c>workitem:verify</c> nor <c>workitem:delete</c> nor anything administrative, so automation
    /// can carry work as far as "merged, awaiting test" and structurally cannot close it. See
    /// <see cref="PrincipalType"/>.
    /// </para>
    /// </remarks>
    [DisplayMetadata("Git integration", 100,
        Description = "A connected git provider acting on webhook events.")]
    Integration = 40
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
