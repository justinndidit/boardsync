using BoardSync.Api.Data;
using BoardSync.Api.Modules.Activity.Models;
using BoardSync.Api.Modules.Activity.Services;
using BoardSync.Api.Modules.OrgProject.Domain.Events;
using BoardSync.Api.Shared.Kernel.Events;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;

namespace BoardSync.Api.Modules.Activity.Handlers;

/// <summary>
/// Turns domain events from every module into activity log rows. These are the event bus's
/// subscribers — the raising modules know nothing about the activity log.
/// </summary>
/// <remarks>
/// Recording is best-effort by construction: <see cref="InMemoryEventBus"/> resolves handlers in a
/// fresh scope after the originating transaction has committed, and swallows whatever they throw.
/// A dropped row costs a line in the feed and nothing else, which is the right trade for never
/// failing a user's write because the audit trail was unavailable.
/// </remarks>
public partial class ActivityEventHandlers :
    IEventHandler<OrganizationCreated>,
    IEventHandler<OrganizationUpdated>,
    IEventHandler<MemberAddedToOrg>,
    IEventHandler<MemberRemovedFromOrg>,
    IEventHandler<OrgMemberRoleChanged>,
    IEventHandler<ProjectCreated>,
    IEventHandler<ProjectUpdated>,
    IEventHandler<ProjectTeamAssigned>,
    IEventHandler<ProjectRoleChanged>,
    IEventHandler<TeamCreated>,
    IEventHandler<TeamUpdated>,
    IEventHandler<TeamArchived>,
    IEventHandler<MemberAddedToTeam>,
    IEventHandler<MemberRemovedFromTeam>
{
    private readonly IActivityRecorder _recorder;
    private readonly BoardSyncDbContext _context;

    public ActivityEventHandlers(IActivityRecorder recorder, BoardSyncDbContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    // ── Organization ─────────────────────────────────────────────────────────

    public Task HandleAsync(OrganizationCreated e, CancellationToken ct = default) =>
        RecordAsync(e, e.OrganizationId, ActivityEntityType.Organization, e.OrganizationId,
            e.Name, ActivityVerb.Created, e.CreatedByUserId, ct);

    public Task HandleAsync(OrganizationUpdated e, CancellationToken ct = default) =>
        RecordAsync(e, e.OrganizationId, ActivityEntityType.Organization, e.OrganizationId,
            e.Name, ActivityVerb.Updated, e.UpdatedByUserId, ct,
            fieldName: e.FieldName, oldValue: e.OldValue, newValue: e.NewValue);

    public async Task HandleAsync(MemberAddedToOrg e, CancellationToken ct = default) =>
        await RecordAsync(e, e.OrganizationId, ActivityEntityType.Organization, e.OrganizationId,
            await OrgNameAsync(e.OrganizationId, ct), ActivityVerb.MemberAdded, e.AddedByUserId, ct,
            fieldName: "Member", newValue: await UserNameAsync(e.UserId, ct));

    public async Task HandleAsync(MemberRemovedFromOrg e, CancellationToken ct = default) =>
        await RecordAsync(e, e.OrganizationId, ActivityEntityType.Organization, e.OrganizationId,
            await OrgNameAsync(e.OrganizationId, ct), ActivityVerb.MemberRemoved, e.RemovedByUserId, ct,
            fieldName: "Member", oldValue: await UserNameAsync(e.UserId, ct));

    public async Task HandleAsync(OrgMemberRoleChanged e, CancellationToken ct = default) =>
        await RecordAsync(e, e.OrganizationId, ActivityEntityType.Organization, e.OrganizationId,
            await UserNameAsync(e.UserId, ct), ActivityVerb.RoleChanged, e.ChangedByUserId, ct,
            fieldName: "Role", oldValue: e.PreviousRole?.ToString(), newValue: e.NewRole.ToString());

    // ── Project ──────────────────────────────────────────────────────────────

    public Task HandleAsync(ProjectCreated e, CancellationToken ct = default) =>
        RecordAsync(e, e.OrganizationId, ActivityEntityType.Project, e.ProjectId,
            e.Name, ActivityVerb.Created, e.CreatedByUserId, ct, projectId: e.ProjectId);

    public Task HandleAsync(ProjectUpdated e, CancellationToken ct = default) =>
        RecordAsync(e, e.OrganizationId, ActivityEntityType.Project, e.ProjectId,
            e.Name, ActivityVerb.Updated, e.UpdatedByUserId, ct, projectId: e.ProjectId,
            fieldName: e.FieldName, oldValue: e.OldValue, newValue: e.NewValue);

    public async Task HandleAsync(ProjectTeamAssigned e, CancellationToken ct = default) =>
        await RecordAsync(e, e.OrganizationId, ActivityEntityType.Project, e.ProjectId,
            e.ProjectName, ActivityVerb.Assigned, e.AssignedByUserId, ct,
            projectId: e.ProjectId, teamId: e.NewTeamId, fieldName: "Team",
            oldValue: await TeamNameAsync(e.PreviousTeamId, ct),
            newValue: await TeamNameAsync(e.NewTeamId, ct));

    public async Task HandleAsync(ProjectRoleChanged e, CancellationToken ct = default) =>
        await RecordAsync(e, e.OrganizationId, ActivityEntityType.Project, e.ProjectId,
            e.ProjectName, ActivityVerb.RoleChanged, e.ChangedByUserId, ct, projectId: e.ProjectId,
            fieldName: $"Role for {await UserNameAsync(e.UserId, ct)}",
            newValue: e.NewRole?.ToString() ?? "revoked");

    // ── Team ─────────────────────────────────────────────────────────────────

    public Task HandleAsync(TeamCreated e, CancellationToken ct = default) =>
        RecordAsync(e, e.OrganizationId, ActivityEntityType.Team, e.TeamId,
            e.Name, ActivityVerb.Created, e.CreatedByUserId, ct, teamId: e.TeamId);

    public Task HandleAsync(TeamUpdated e, CancellationToken ct = default) =>
        RecordAsync(e, e.OrganizationId, ActivityEntityType.Team, e.TeamId,
            e.Name, ActivityVerb.Updated, e.UpdatedByUserId, ct, teamId: e.TeamId,
            fieldName: e.FieldName, oldValue: e.OldValue, newValue: e.NewValue);

    public Task HandleAsync(TeamArchived e, CancellationToken ct = default) =>
        RecordAsync(e, e.OrganizationId, ActivityEntityType.Team, e.TeamId,
            e.Name, ActivityVerb.Archived, e.ArchivedByUserId, ct, teamId: e.TeamId);

    public async Task HandleAsync(MemberAddedToTeam e, CancellationToken ct = default) =>
        await RecordAsync(e, e.OrganizationId, ActivityEntityType.Team, e.TeamId,
            await TeamNameAsync(e.TeamId, ct), ActivityVerb.MemberAdded, e.AddedByUserId, ct,
            teamId: e.TeamId, fieldName: "Member", newValue: await UserNameAsync(e.UserId, ct));

    public async Task HandleAsync(MemberRemovedFromTeam e, CancellationToken ct = default) =>
        await RecordAsync(e, e.OrganizationId, ActivityEntityType.Team, e.TeamId,
            await TeamNameAsync(e.TeamId, ct), ActivityVerb.MemberRemoved, e.RemovedByUserId, ct,
            teamId: e.TeamId, fieldName: "Member", oldValue: await UserNameAsync(e.UserId, ct));

    // ── Shared plumbing ──────────────────────────────────────────────────────

    private Task RecordAsync(
        IDomainEvent e,
        Guid organizationId,
        ActivityEntityType entityType,
        Guid entityId,
        string entityTitle,
        ActivityVerb verb,
        Guid actorId,
        CancellationToken ct,
        Guid? projectId = null,
        Guid? teamId = null,
        string? fieldName = null,
        string? oldValue = null,
        string? newValue = null) =>
        _recorder.RecordAsync(new ActivityLog
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            TeamId = teamId,
            ActorId = actorId,
            EntityType = entityType,
            EntityId = entityId,
            EntityTitle = Truncate(entityTitle, 255),
            Verb = verb,
            FieldName = Truncate(fieldName, 100),
            OldValue = Truncate(oldValue, 1000),
            NewValue = Truncate(newValue, 1000),
            OccurredAt = e.OccurredAt,
            CreatedBy = actorId
        }, ct);

    /// <summary>
    /// Clips a value to what the column accepts. Descriptions and comment bodies are unbounded at
    /// the source, and an over-long value must cost a truncated feed line, not a lost row.
    /// </summary>
    [return: NotNullIfNotNull(nameof(value))]
    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private async Task<string> UserNameAsync(Guid userId, CancellationToken ct) =>
        await _context.Users.Where(u => u.Id == userId)
            .Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "Unknown";

    private async Task<string> OrgNameAsync(Guid orgId, CancellationToken ct) =>
        await _context.Organizations.Where(o => o.Id == orgId)
            .Select(o => o.Name).FirstOrDefaultAsync(ct) ?? string.Empty;

    private async Task<string> TeamNameAsync(Guid teamId, CancellationToken ct) =>
        await _context.Teams.Where(t => t.Id == teamId)
            .Select(t => t.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
}
