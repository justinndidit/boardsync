using BoardSync.Api.Modules.OrgProject.Domain.Events;
using BoardSync.Api.Modules.Sprints.Events;
using BoardSync.Api.Modules.WorkItems.Events;

namespace BoardSync.Api.Shared.Kernel.Events;

/// <summary>
/// Decides which topics an event should reach.
/// </summary>
/// <remarks>
/// <para>
/// One switch, in one file, on purpose. The alternative — each module deciding its own routing —
/// spreads the answer to "who sees this?" across the codebase, and the failure mode is an event
/// that quietly reaches nobody.
/// </para>
/// <para>
/// Topics are computed at enqueue time and stored on the outbox row, so replaying "what did this
/// topic miss?" is an indexed database filter rather than deserializing every message to ask.
/// </para>
/// <para>
/// Work item events carry a project but no organization, so they do not route to
/// <see cref="Topic.Organization"/> here. The organization feed is served separately by the activity
/// path, which already resolves the owning organization and has the rendered feed entry to send —
/// pushing the raw event as well would put two different shapes of the same change on one topic.
/// </para>
/// </remarks>
public static class EventTopics
{
    public static string[] For(IDomainEvent domainEvent) => domainEvent switch
    {
        // ── Organization ──────────────────────────────────────────────────────
        OrganizationCreated e => [Topic.Organization(e.OrganizationId)],
        OrganizationUpdated e => [Topic.Organization(e.OrganizationId)],

        // Membership changes reach the organization *and* the person affected — their own client
        // needs to know its access just changed, wherever it happens to be looking.
        MemberAddedToOrg e => [Topic.Organization(e.OrganizationId), Topic.User(e.UserId)],
        MemberRemovedFromOrg e => [Topic.Organization(e.OrganizationId), Topic.User(e.UserId)],
        OrgMemberRoleChanged e => [Topic.Organization(e.OrganizationId), Topic.User(e.UserId)],

        // ── Project ───────────────────────────────────────────────────────────
        ProjectCreated e => [Topic.Organization(e.OrganizationId), Topic.Project(e.ProjectId)],
        ProjectUpdated e => [Topic.Organization(e.OrganizationId), Topic.Project(e.ProjectId)],
        ProjectTeamAssigned e =>
            [Topic.Organization(e.OrganizationId), Topic.Project(e.ProjectId), Topic.Team(e.NewTeamId)],
        ProjectRoleChanged e =>
            [Topic.Organization(e.OrganizationId), Topic.Project(e.ProjectId), Topic.User(e.UserId)],

        // ── Team ──────────────────────────────────────────────────────────────
        TeamCreated e => [Topic.Organization(e.OrganizationId), Topic.Team(e.TeamId)],
        TeamUpdated e => [Topic.Organization(e.OrganizationId), Topic.Team(e.TeamId)],
        TeamArchived e => [Topic.Organization(e.OrganizationId), Topic.Team(e.TeamId)],
        MemberAddedToTeam e =>
            [Topic.Organization(e.OrganizationId), Topic.Team(e.TeamId), Topic.User(e.UserId)],
        MemberRemovedFromTeam e =>
            [Topic.Organization(e.OrganizationId), Topic.Team(e.TeamId), Topic.User(e.UserId)],

        // ── Work items ────────────────────────────────────────────────────────
        // The project topic is what a board client subscribes to; board changes ride it too.
        WorkItemCreated e => [Topic.Project(e.ProjectId)],
        WorkItemUpdated e => [Topic.Project(e.ProjectId)],
        WorkItemStateChanged e => [Topic.Project(e.ProjectId)],
        WorkItemDeleted e => [Topic.Project(e.ProjectId)],
        WorkItemCommentAdded e => [Topic.Project(e.ProjectId)],
        WorkItemLinked => [],

        // Assignment reaches both people: the one who just picked it up and the one who did not.
        WorkItemAssigned e =>
        [
            Topic.Project(e.ProjectId),
            .. e.NewAssigneeId is { } next ? new[] { Topic.User(next) } : [],
            .. e.PreviousAssigneeId is { } prev ? new[] { Topic.User(prev) } : []
        ],

        // ── Sprints and boards ────────────────────────────────────────────────
        SprintCreated e => [Topic.Organization(e.OrganizationId), Topic.Project(e.ProjectId), Topic.Sprint(e.SprintId)],
        SprintUpdated e => [Topic.Organization(e.OrganizationId), Topic.Project(e.ProjectId), Topic.Sprint(e.SprintId)],
        SprintStatusChanged e =>
            [Topic.Organization(e.OrganizationId), Topic.Project(e.ProjectId), Topic.Sprint(e.SprintId)],
        SprintDeleted e => [Topic.Organization(e.OrganizationId), Topic.Project(e.ProjectId), Topic.Sprint(e.SprintId)],
        SprintWorkItemAdded e => [Topic.Project(e.ProjectId), Topic.Sprint(e.SprintId)],
        SprintWorkItemRemoved e => [Topic.Project(e.ProjectId), Topic.Sprint(e.SprintId)],
        BoardChanged e => [Topic.Organization(e.OrganizationId), Topic.Project(e.ProjectId)],

        // An event nobody routes reaches nobody. That is a routing gap, not a silent success, so it
        // is worth being able to find — the dispatcher logs it.
        _ => []
    };
}
