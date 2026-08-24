using System.Text.Json.Serialization;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.Notifications.Models;

/// <summary>Why somebody is being told something.</summary>
/// <remarks>
/// Stored by name, so adding one is additive. The client switches on these to choose an icon and a
/// destination, so they are part of the API contract — see <c>docs/permissions-frontend.md</c>.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationType
{
    /// <summary>A work item was assigned to you.</summary>
    WorkItemAssigned,

    /// <summary>A work item you are watching changed state.</summary>
    WorkItemStateChanged,

    /// <summary>Somebody commented on a work item you are watching.</summary>
    WorkItemCommented,

    /// <summary>
    /// Work reached <c>Resolved</c> and is waiting to be tested.
    /// </summary>
    /// <remarks>
    /// The one this product exists to send. Git can carry work as far as "merged, awaiting test" on
    /// its own, and the whole QA gate is pointless if nobody is told the work is now waiting on them
    /// — the queue would just be a column people had to remember to look at.
    /// </remarks>
    WorkItemAwaitingVerification
}

/// <summary>
/// One thing one person needs to know about.
/// </summary>
/// <remarks>
/// <para>
/// The bell used to be a query over <c>WorkItemHistory</c> filtered to the caller's organizations:
/// no recipient, no read state, no targeting, and no way to mark anything read because there was
/// nothing to write to. It showed everyone the same rows and called them notifications.
/// </para>
/// <para>
/// Written on the outbox path by <c>NotificationEventHandlers</c>, which means delivery is
/// at-least-once and every write is keyed on the originating event so a redelivery is recognised
/// rather than duplicated.
/// </para>
/// </remarks>
public class Notification : BaseEntity
{
    /// <summary>Who is being told.</summary>
    public Guid RecipientId { get; set; }

    public NotificationType Type { get; set; }

    /// <summary>
    /// The originating domain event.
    /// </summary>
    /// <remarks>
    /// Unique per recipient, and the reason a redelivered outbox message does not produce a second
    /// notification. Not unique on its own — one event legitimately notifies several people.
    /// </remarks>
    public Guid EventId { get; set; }

    /// <summary>What the notification is about — a work item today.</summary>
    public Guid EntityId { get; set; }

    /// <summary>The project it belongs to, for permission-scoped reads and deep links.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>The work item's reference, e.g. <c>BS-142</c>, so the bell can render without a join.</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>One line, already worded for display.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Supporting detail — the new state, the comment's first line.
    /// </summary>
    /// <remarks>
    /// Denormalized deliberately. A bell that had to join four tables to render ten rows would be the
    /// slowest thing on the page, and what a notification said at the time it was sent should not
    /// change afterwards because somebody edited the underlying record.
    /// </remarks>
    public string? Detail { get; set; }

    /// <summary>
    /// Who caused it, or null when nothing did.
    /// </summary>
    /// <remarks>
    /// May be an integration rather than a person — a merge moving an item is the common case now —
    /// which is what <see cref="ActorName"/> is for.
    /// </remarks>
    public Guid? ActorId { get; set; }

    /// <summary>The actor's display name, resolved at write time. "GitHub" for an integration.</summary>
    public string ActorName { get; set; } = string.Empty;

    /// <summary>When the recipient read it. Null while unread.</summary>
    public DateTime? ReadAt { get; set; }
}

/// <summary>
/// Somebody who wants to hear about a work item.
/// </summary>
/// <remarks>
/// <para>
/// Watching is mostly implicit: being assigned an item, or commenting on one, starts you watching
/// it. Explicit watching exists for the case implicit watching misses — a lead following work they
/// are not doing — but requiring it for the ordinary cases would mean the bell stayed empty for
/// everyone who did not know the feature existed.
/// </para>
/// <para>
/// Unwatching is remembered rather than deleted, so a later comment does not silently re-subscribe
/// somebody who deliberately opted out.
/// </para>
/// </remarks>
public class WorkItemWatcher : BaseEntity
{
    public Guid WorkItemId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The project the item belongs to, so a watcher list can be pruned with its project.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>
    /// False when the user has deliberately stopped watching.
    /// </summary>
    /// <remarks>
    /// A row rather than an absence, because "never watched" and "chose to stop" need different
    /// answers when something would otherwise auto-subscribe them again.
    /// </remarks>
    public bool IsWatching { get; set; } = true;
}
