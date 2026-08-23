using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.WorkItems.Domain;

/// <summary>
/// Which state a work item may move to next, and what that move requires.
/// </summary>
/// <remarks>
/// <para>
/// Lifted out of <c>WorkItemService</c> so the rule has one home. The service enforces it and the
/// metadata endpoint publishes it, and those must be the same table: a client that renders a "Move
/// to…" menu from a second copy will offer transitions the server rejects, and the bug reads as the
/// server being wrong.
/// </para>
/// <para>
/// Pure, so it is testable without a database — the same reason
/// <see cref="Rbac.Services.AccessEvaluator"/> is.
/// </para>
/// </remarks>
public static class WorkItemStateMachine
{
    /// <summary>
    /// The states reachable from <paramref name="current"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing reaches Closed except through Resolved.</b> <c>Active → Closed</c> was once allowed,
    /// which let whoever did the work also declare it finished — every transition is gated on
    /// <see cref="Permissions.WorkItemWrite"/>, which every contributor holds. Resolved is the single
    /// door into Closed, and <see cref="RequiredPermission"/> is what guards it.
    /// </para>
    /// <para>
    /// Each state is one a git signal can identify: a branch's first commit makes an item Active, an
    /// opened pull request makes it InReview, and a merge into the default branch makes it Resolved.
    /// The automation stops exactly there.
    /// </para>
    /// <para>
    /// <c>Active → Resolved</c> stays, for work that needs no pull request. <c>Closed → Active</c>
    /// stays too: reopening is a real thing, and forbidding it would only push people into filing
    /// duplicates.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<WorkItemState> AllowedFrom(WorkItemState current) => current switch
    {
        WorkItemState.New => [WorkItemState.Active],
        WorkItemState.Active => [WorkItemState.InReview, WorkItemState.Resolved],

        // Back to Active when a pull request is closed unmerged or review asks for changes.
        WorkItemState.InReview => [WorkItemState.Resolved, WorkItemState.Active],

        // QA accepts, or sends it back.
        WorkItemState.Resolved => [WorkItemState.Closed, WorkItemState.Active],

        WorkItemState.Closed => [WorkItemState.Active],
        _ => []
    };

    /// <summary>Whether one state may follow another.</summary>
    public static bool CanTransition(WorkItemState from, WorkItemState to) =>
        AllowedFrom(from).Contains(to);

    /// <summary>
    /// The permission a transition requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="Permissions.WorkItemVerify"/> guards every move out of the QA lane</b> — out of
    /// <c>Resolved</c> and out of <c>Closed</c> — rather than only the one into <c>Closed</c>. Once
    /// work is waiting to be tested, whether it is done is QA's answer to give: letting the author
    /// pull it back to Active would let them quietly take it out of the queue before a rejection was
    /// ever recorded, which is the same bypass by a slower route.
    /// </para>
    /// <para>
    /// Everything before that is ordinary work and needs only <see cref="Permissions.WorkItemWrite"/>,
    /// including <c>Active → Resolved</c>: saying "I think this is done" is not the same as saying
    /// "this is done".
    /// </para>
    /// <para>
    /// This method is the single definition. The service enforces it, <c>GET /api/metadata</c>
    /// publishes it per transition, and the client's menu is built from that — so a state machine
    /// change reaches all three from here.
    /// </para>
    /// </remarks>
    public static string RequiredPermission(WorkItemState from, WorkItemState to) =>
        from is WorkItemState.Resolved or WorkItemState.Closed
            ? Permissions.WorkItemVerify
            : Permissions.WorkItemWrite;
}

/// <summary>
/// Which work item types may nest inside which.
/// </summary>
/// <remarks>
/// Epic → Feature → User Story → Task or Bug. Lifted out of <c>WorkItemService</c> for the same
/// reason as the state machine: the "add child" menu is built from it, so the client and the
/// validator have to agree.
/// </remarks>
public static class WorkItemHierarchy
{
    /// <summary>The types that may be created as children of <paramref name="parent"/>.</summary>
    public static IReadOnlyList<WorkItemType> AllowedChildrenOf(WorkItemType parent) => parent switch
    {
        WorkItemType.Epic => [WorkItemType.Feature],
        WorkItemType.Feature => [WorkItemType.UserStory],
        WorkItemType.UserStory => [WorkItemType.Task, WorkItemType.Bug],

        // A Task and a Bug are leaves. Nesting under them produces a tree nobody can read and a
        // rollup nobody can compute.
        _ => []
    };

    /// <summary>Whether <paramref name="child"/> may sit under <paramref name="parent"/>.</summary>
    public static bool CanNest(WorkItemType parent, WorkItemType child) =>
        AllowedChildrenOf(parent).Contains(child);

    /// <summary>The hierarchy as one line, for rejection messages.</summary>
    public const string Description = "Epic → Feature → Story → Task/Bug";
}
