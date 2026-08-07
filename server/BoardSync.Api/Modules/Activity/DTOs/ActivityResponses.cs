using BoardSync.Api.Modules.Activity.Models;

namespace BoardSync.Api.Modules.Activity.DTOs;

/// <summary>
/// One entry in an activity feed. Identical for <c>/api/orgs/{orgId}/activity</c> and
/// <c>/api/workspace/activity</c> — the two differ only in which organizations they cover.
/// </summary>
/// <remarks>
/// <c>Type</c> is the client-facing discriminator, formatted "<c>EntityType.Verb</c>" (e.g.
/// "WorkItem.StateChanged", "Team.MemberAdded") — switch on it to pick an icon; the
/// <c>EntityType</c> and <c>Verb</c> fields carry the same information in structured form.
/// <c>EntityId</c> identifies the subject so the client can link straight to it, <c>Title</c> is
/// the subject's name as it read when the action happened, and <c>Detail</c> is the rendered
/// description of the change, e.g. "State: New → Active".
/// </remarks>
public record ActivityResponse(
    Guid Id,
    string Type,
    ActivityEntityType EntityType,
    ActivityVerb Verb,
    Guid EntityId,
    string Title,
    string? Detail,
    Guid ActorId,
    string ActorName,
    Guid OrganizationId,
    string Organization,
    Guid? ProjectId,
    string? Project,
    Guid? TeamId,
    string? Team,
    DateTime OccurredAt
);
