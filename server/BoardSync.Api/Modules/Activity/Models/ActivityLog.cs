using BoardSync.Api.Shared.Kernel;
using System.Text.Json.Serialization;

namespace BoardSync.Api.Modules.Activity.Models;

/// <summary>
/// The kind of thing an activity entry is about.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivityEntityType
{
    Organization,
    Project,
    Team,
    WorkItem,
    Comment,
    Sprint,
    Board
}

/// <summary>
/// What happened to the entity. Kept deliberately small — anything that does not map to one of
/// these is a <see cref="Updated"/> with the changed field carried in
/// <see cref="ActivityLog.FieldName"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivityVerb
{
    Created,
    Updated,
    Deleted,
    Archived,
    StateChanged,
    Assigned,
    MemberAdded,
    MemberRemoved,
    RoleChanged,
    Commented,
    Linked
}

/// <summary>
/// One thing that happened inside an organization — the single source for both activity feeds.
///
/// Rows are append-only and are written by the handlers in the Activity module in response to
/// domain events, never by the modules that raise them. Every row carries
/// <see cref="OrganizationId"/> even when the subject is a project or team, because both feeds
/// filter on it: the organization feed by one id, the workspace feed by every organization the
/// caller belongs to.
/// </summary>
public class ActivityLog : BaseEntity
{
    /// <summary>
    /// The outbox event this entry was written from.
    /// </summary>
    /// <remarks>
    /// Unique, and it is what makes recording idempotent. The outbox delivers at least once — a
    /// dispatcher that crashes after running handlers but before marking the row dispatched will
    /// redeliver — so without this key a redelivery would duplicate the feed line.
    /// </remarks>
    public Guid EventId { get; set; }

    /// <summary>Organization the activity belongs to. Always set — this is what the feeds filter on.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>Owning project, when the subject sits inside one.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>Owning team, when the subject sits inside one.</summary>
    public Guid? TeamId { get; set; }

    /// <summary>User who performed the action.</summary>
    public Guid ActorId { get; set; }

    public ActivityEntityType EntityType { get; set; }

    /// <summary>Id of the subject (work item, project, team, …).</summary>
    public Guid EntityId { get; set; }

    /// <summary>
    /// Display name of the subject as it read at the time. Snapshotted rather than joined, so a
    /// later rename — or a delete — does not rewrite or blank out history.
    /// </summary>
    public string EntityTitle { get; set; } = string.Empty;

    public ActivityVerb Verb { get; set; }

    /// <summary>Field that changed, for <see cref="ActivityVerb.Updated"/> entries.</summary>
    public string? FieldName { get; set; }

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    /// <summary>
    /// When the action happened, taken from the domain event rather than from insert time —
    /// handlers run after the originating transaction commits, so the two can differ.
    /// </summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
