using BoardSync.Api.Modules.Notifications.Models;

namespace BoardSync.Api.Modules.Notifications.Repositories.Interfaces;

/// <summary>The work item a notification is about, in the shape the bell renders.</summary>
/// <param name="Id">The work item.</param>
/// <param name="ProjectId">Its project.</param>
/// <param name="Reference">What people call it — <c>BS-142</c>.</param>
/// <param name="Title">Its title.</param>
/// <param name="AssigneeId">
/// Who it is assigned to, when anyone. Carried because a work item created with an assignee raises
/// no assignment event — the assignment is part of the creation — and that is the commonest way
/// somebody is given work.
/// </param>
public readonly record struct NotifiableItem(
    Guid Id, Guid ProjectId, string Reference, string Title, Guid? AssigneeId);

/// <summary>One notification, before it is fanned out to its recipients.</summary>
/// <param name="Recipients">Who to tell. Duplicates and the actor are filtered by the writer.</param>
/// <param name="Type">Why.</param>
/// <param name="EventId">The originating event, which is what makes a redelivery idempotent.</param>
/// <param name="Item">What it is about.</param>
/// <param name="Title">One line, already worded.</param>
/// <param name="Detail">Supporting detail.</param>
/// <param name="ActorId">Who caused it — may be an integration.</param>
/// <param name="ActorName">
/// Their display name, resolved by the caller when it already knows it. The writer resolves it when
/// this is null, so a handler that does not need the name does not pay for a lookup.
/// </param>
public readonly record struct NotificationDraft(
    IReadOnlyList<Guid> Recipients,
    NotificationType Type,
    Guid EventId,
    NotifiableItem Item,
    string Title,
    string? Detail,
    Guid? ActorId,
    string? ActorName = null);

/// <summary>
/// Writes notifications and maintains who is watching what.
/// </summary>
/// <remarks>
/// Separate from <see cref="INotificationRepository"/>, which is the read side the bell uses. They
/// have different callers, different lifetimes and — since one runs on the outbox path and the other
/// on a request — different failure consequences.
/// </remarks>
public interface INotificationWriter
{
    /// <summary>
    /// Creates one notification per recipient, skipping anyone already told about this event.
    /// </summary>
    /// <remarks>
    /// Idempotent on <c>(RecipientId, EventId)</c>, which is what makes at-least-once outbox delivery
    /// safe: a redelivered message finds the rows already there and writes nothing.
    /// </remarks>
    Task WriteAsync(NotificationDraft draft, CancellationToken ct = default);

    /// <summary>The work item, or null if it has gone.</summary>
    Task<NotifiableItem?> DescribeAsync(Guid workItemId, CancellationToken ct = default);

    /// <summary>Everyone currently watching a work item.</summary>
    Task<IReadOnlyList<Guid>> GetWatchersAsync(Guid workItemId, CancellationToken ct = default);

    /// <summary>
    /// Starts a user watching, unless they previously chose to stop.
    /// </summary>
    /// <remarks>
    /// The exception is the point: implicit watching must not quietly re-subscribe somebody who
    /// deliberately unwatched. Explicit watching goes through
    /// <see cref="SetWatchingAsync"/>, which does override it.
    /// </remarks>
    Task WatchAsync(Guid workItemId, Guid projectId, Guid userId, CancellationToken ct = default);

    /// <summary>Starts or stops watching, at the user's explicit request.</summary>
    Task SetWatchingAsync(
        Guid workItemId, Guid projectId, Guid userId, bool watching, CancellationToken ct = default);

    /// <summary>Whether a user is watching a work item.</summary>
    Task<bool> IsWatchingAsync(Guid workItemId, Guid userId, CancellationToken ct = default);

    /// <summary>A display name for whoever acted — "GitHub" when it was an integration.</summary>
    Task<string> ActorNameAsync(Guid actorId, CancellationToken ct = default);

    /// <summary>The first line of a comment, for the notification's detail.</summary>
    Task<string?> CommentPreviewAsync(Guid commentId, CancellationToken ct = default);
}
