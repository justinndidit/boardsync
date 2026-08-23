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
    /// <b>Nothing reaches Closed except through Resolved.</b> <c>Active → Closed</c> was allowed and
    /// is not: every transition is gated on <see cref="Permissions.WorkItemWrite"/>, held by every
    /// contributor and every team member, so that edge let whoever did the work also declare it
    /// finished. Resolved is the single door into Closed, which is what the QA gate guards — see
    /// build_context.md §4.
    /// </para>
    /// <para>
    /// Closed → Active is deliberately kept: reopening is a real thing that happens, and forbidding
    /// it would only push people into creating duplicate items.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<WorkItemState> AllowedFrom(WorkItemState current) => current switch
    {
        WorkItemState.New => [WorkItemState.Active],
        WorkItemState.Active => [WorkItemState.Resolved],
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
    /// Uniformly <see cref="Permissions.WorkItemWrite"/> today, and expressed as a function anyway
    /// because it is about to stop being uniform: the QA gate gives the edges into and out of Closed
    /// their own permission, <c>workitem:verify</c>, so that certifying work is a different authority
    /// from doing it. Modelling it now means Phase B changes this method and nothing else — the
    /// endpoint guard, the published metadata and the client's menu all follow from here.
    /// </remarks>
    public static string RequiredPermission(WorkItemState from, WorkItemState to) =>
        Permissions.WorkItemWrite;
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
