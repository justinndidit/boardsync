using System.Text.Json.Serialization;

namespace BoardSync.Api.Modules.Rbac.Models;

/// <summary>
/// Roles available in the system.
/// </summary>
/// <remarks>
/// <para>
/// <b>The numeric values no longer carry meaning.</b> They were once a privilege ladder, compared
/// with <c>&lt;=</c> to answer "at least a TeamMember?". That stopped working the moment team
/// positions arrived: a Scrum Master and a Product Owner are peers holding different authority, and
/// no ordering of them is truthful. What a role permits is now declared in
/// <see cref="RolePermissions"/> and resolved by union, never by comparison. The values remain only
/// because they are persisted, and are otherwise arbitrary.
/// </para>
/// <para>
/// Stored as names, so adding a value is a non-breaking change and removing one is not.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoleType
{
    /// <summary>Administers the entire organization and everything inside it.</summary>
    OrgAdmin = 10,

    /// <summary>Administers one project: its settings, its board, and its direct role grants.</summary>
    ProjectAdmin = 20,

    /// <summary>Leads a team: its composition, and who holds its positions.</summary>
    TeamLead = 21,

    /// <summary>Owns a team's process: runs the sprint lifecycle.</summary>
    ScrumMaster = 22,

    /// <summary>Owns a team's backlog: decides what a sprint commits to.</summary>
    ProductOwner = 23,

    /// <summary>Contributes on a team: creates and edits work, orders the sprint.</summary>
    TeamMember = 30,

    /// <summary>Read-only: can view boards, backlogs and reports but cannot mutate.</summary>
    Reader = 40,

    /// <summary>
    /// Legacy. Never assigned by any code path and grants nothing; retained only because the column
    /// stores names and an old row could still say so.
    /// </summary>
    User = 50
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
