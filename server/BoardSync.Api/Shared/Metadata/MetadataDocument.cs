namespace BoardSync.Api.Shared.Metadata;

/// <summary>
/// Every vocabulary the client would otherwise hardcode.
/// </summary>
/// <remarks>
/// <para>
/// Enums serialize as their names, so the numeric values that encode ordering server-side never
/// reach the client. That left it hardcoding the order of priorities, the set of roles valid at each
/// scope, the legal state transitions, and five other lists — copies with no test behind them and no
/// migration to update them.
/// </para>
/// <para>
/// Every entry carries the same three fields: <c>value</c> (what goes on the wire), <c>label</c>
/// (what a person reads), and <c>order</c> (the sort key the enum's numbering carried and the string
/// does not).
/// </para>
/// </remarks>
/// <param name="Version">
/// A hash of this document's content. Changes exactly when the vocabulary changes, so a client can
/// cache against it and nobody has to remember to bump a number. Also served as the ETag.
/// </param>
/// <param name="Roles">Roles, one entry per role and scope. See <see cref="RoleMetadata"/>.</param>
/// <param name="Permissions">Every capability the system gates on.</param>
/// <param name="WorkItemTypes">Work item types and what may nest beneath each.</param>
/// <param name="WorkItemStates">Work item states and the moves allowed from each.</param>
/// <param name="Priorities">Work item priorities, in order of urgency.</param>
/// <param name="SprintStatuses">Sprint lifecycle states, in lifecycle order.</param>
/// <param name="WorkItemLinkTypes">Relationship types between two work items.</param>
/// <param name="TeamPositions">
/// The role names that are singular team appointments rather than ordinary grants, in the order a UI
/// should list them.
/// </param>
public sealed record MetadataDocument(
    string Version,
    IReadOnlyList<RoleMetadata> Roles,
    IReadOnlyList<PermissionMetadata> Permissions,
    IReadOnlyList<WorkItemTypeMetadata> WorkItemTypes,
    IReadOnlyList<WorkItemStateMetadata> WorkItemStates,
    IReadOnlyList<EnumMetadata> Priorities,
    IReadOnlyList<EnumMetadata> SprintStatuses,
    IReadOnlyList<LinkTypeMetadata> WorkItemLinkTypes,
    IReadOnlyList<string> TeamPositions);

/// <summary>The plain shape: a value, its label, and where it sorts.</summary>
/// <param name="Value">The string that appears on the wire.</param>
/// <param name="Label">What a person should see.</param>
/// <param name="Order">Sort position, ascending.</param>
/// <param name="Description">One sentence for a tooltip, when there is something worth saying.</param>
public record EnumMetadata(string Value, string Label, int Order, string? Description = null);

/// <summary>
/// A role, at one scope.
/// </summary>
/// <remarks>
/// <b>One entry per (role, scope) pair, not per role.</b> <c>Viewer</c> is valid at both team and
/// project scope and permits different things at each — team read versus a project's board, sprints
/// and work items — so a single entry could not describe it. <c>value</c> is therefore not unique
/// across the list; <c>(value, scope)</c> is.
/// </remarks>
/// <param name="Value">The <c>RoleType</c> name, as sent to and returned from the role endpoints.</param>
/// <param name="Label">What a person should see.</param>
/// <param name="Order">
/// Sort position, ascending. Ordered globally rather than per scope, and chosen so it reads
/// correctly within each scope's subset.
/// </param>
/// <param name="Scope">Where this grant applies: <c>Organization</c>, <c>Team</c> or <c>Project</c>.</param>
/// <param name="Grantable">
/// Whether a person may be given this role. False for roles that exist for non-human principals —
/// today only <c>Integration</c>, held by a connected git installation. A role picker should offer
/// only grantable roles; a display of an existing grant should still render this one.
/// </param>
/// <param name="IsPosition">
/// Whether this is a singular appointment — one holder per team, handed over as an explicit act —
/// rather than an ordinary grant. Positions are assigned through
/// <c>PUT /api/teams/{teamId}/positions/{position}</c>, not the general role endpoints.
/// </param>
/// <param name="Permissions">What holding this role at this scope permits.</param>
/// <param name="InheritedProjectPermissions">
/// For team-scope roles only, null elsewhere: what the role additionally permits on every project
/// the team is assigned to. This is the team → project edge, and it is not derivable client-side —
/// it is why a Scrum Master can run sprints on their team's projects without holding any project
/// role.
/// </param>
/// <param name="Description">One sentence describing the role.</param>
public sealed record RoleMetadata(
    string Value,
    string Label,
    int Order,
    string Scope,
    bool Grantable,
    bool IsPosition,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string>? InheritedProjectPermissions = null,
    string? Description = null);

/// <summary>One capability the system gates on.</summary>
/// <param name="Value">The permission string, as it appears in logs and in role definitions.</param>
/// <param name="Label">What a person should see.</param>
/// <param name="Order">Sort position, ascending.</param>
/// <param name="Group">Heading to list this permission under. Twenty-odd is unusable flat.</param>
/// <param name="Description">One sentence describing what it allows.</param>
public sealed record PermissionMetadata(
    string Value,
    string Label,
    int Order,
    string? Group = null,
    string? Description = null);

/// <summary>A work item type, and what may nest beneath it.</summary>
/// <param name="Value">The <c>WorkItemType</c> name. Note it is <c>UserStory</c>, not <c>Story</c>.</param>
/// <param name="Label">What a person should see.</param>
/// <param name="Order">Sort position, ascending — broadest first.</param>
/// <param name="AllowedChildren">
/// Types that may be created beneath this one. Empty for leaves, which is what a client should use
/// to decide whether to offer an "add child" action at all.
/// </param>
/// <param name="Description">One sentence describing the type.</param>
public sealed record WorkItemTypeMetadata(
    string Value,
    string Label,
    int Order,
    IReadOnlyList<string> AllowedChildren,
    string? Description = null);

/// <summary>A work item state, and where it can go next.</summary>
/// <param name="Value">The <c>WorkItemState</c> name, as sent to <c>PATCH /api/workitems/{id}/state</c>.</param>
/// <param name="Label">
/// What a person should see, which is not always the enum name — <c>Resolved</c> shows as
/// "Awaiting QA", because what the state means to a reader is that the work is waiting to be tested.
/// </param>
/// <param name="Order">Sort position, ascending — board order, left to right.</param>
/// <param name="Category">
/// The lane this state belongs to — <c>Pending</c>, <c>InProgress</c>, <c>Review</c>, <c>Done</c>.
/// Lets a client colour and group states without switching on their names.
/// </param>
/// <param name="TransitionsTo">Where this state may move next, and what each move requires.</param>
/// <param name="Description">One sentence describing the state.</param>
public sealed record WorkItemStateMetadata(
    string Value,
    string Label,
    int Order,
    string? Category,
    IReadOnlyList<StateTransitionMetadata> TransitionsTo,
    string? Description = null);

/// <summary>One legal move out of a state.</summary>
/// <param name="State">The state that may be moved to.</param>
/// <param name="RequiresPermission">
/// The permission this move needs. Uniform today; the QA gate gives the edges into and out of
/// <c>Closed</c> their own, so a client that reads this rather than assuming keeps working.
/// </param>
public sealed record StateTransitionMetadata(string State, string RequiresPermission);

/// <summary>A relationship type between two work items.</summary>
/// <param name="Value">The <c>WorkItemLinkType</c> name.</param>
/// <param name="Label">What a person should see from the source item's side.</param>
/// <param name="Order">Sort position, ascending.</param>
/// <param name="Inverse">
/// What the relationship is called from the other item's side — "Blocks" read from the target is
/// "Blocked by". One row, two wordings, and the client cannot derive the second.
/// </param>
/// <param name="Description">One sentence describing the relationship.</param>
public sealed record LinkTypeMetadata(
    string Value,
    string Label,
    int Order,
    string Inverse,
    string? Description = null);
