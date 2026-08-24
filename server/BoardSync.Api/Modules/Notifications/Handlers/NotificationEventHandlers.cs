using BoardSync.Api.Modules.Notifications.Models;
using BoardSync.Api.Modules.Notifications.Repositories.Interfaces;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Modules.WorkItems.Events;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Kernel.Events;

namespace BoardSync.Api.Modules.Notifications.Handlers;

/// <summary>
/// Turns domain events into notifications for the people who need to know.
/// </summary>
/// <remarks>
/// <para>
/// Subscribers on the outbox, like the activity handlers — the raising modules know nothing about
/// notifications. Delivery is at-least-once, so every write is keyed on
/// <see cref="IDomainEvent.EventId"/> per recipient and a redelivery is recognised rather than
/// duplicated.
/// </para>
/// <para>
/// <b>Nobody is ever notified about their own action.</b> It is the difference between a bell worth
/// opening and one people mute in the first week.
/// </para>
/// </remarks>
public class NotificationEventHandlers :
    IEventHandler<WorkItemCreated>,
    IEventHandler<WorkItemAssigned>,
    IEventHandler<WorkItemStateChanged>,
    IEventHandler<WorkItemCommentAdded>
{
    private readonly INotificationWriter _writer;
    private readonly IRbacService _rbac;
    private readonly ILogger<NotificationEventHandlers> _logger;

    public NotificationEventHandlers(
        INotificationWriter writer,
        IRbacService rbac,
        ILogger<NotificationEventHandlers> logger)
    {
        _writer = writer;
        _rbac = rbac;
        _logger = logger;
    }

    /// <summary>
    /// Tells whoever a new work item was created for.
    /// </summary>
    /// <remarks>
    /// <b>Creating an item with an assignee raises no assignment event</b> — the assignment is part
    /// of the creation, and <c>WorkItemAssigned</c> is only raised when an existing item changes
    /// hands. That makes this the commonest way somebody is given work, and without this handler it
    /// notified nobody: the tests caught exactly that.
    /// </remarks>
    public async Task HandleAsync(WorkItemCreated e, CancellationToken ct = default)
    {
        if (await _writer.DescribeAsync(e.WorkItemId, ct) is not { } item) return;
        if (item.AssigneeId is not { } assignee) return;

        await _writer.WatchAsync(e.WorkItemId, e.ProjectId, assignee, ct);

        if (assignee == e.CreatedByUserId) return;

        await _writer.WriteAsync(new NotificationDraft(
            Recipients: [assignee],
            Type: NotificationType.WorkItemAssigned,
            EventId: e.EventId,
            Item: item,
            Title: $"{item.Reference} was assigned to you",
            Detail: item.Title,
            ActorId: e.CreatedByUserId), ct);
    }

    /// <summary>
    /// Tells the new assignee, and starts them watching.
    /// </summary>
    /// <remarks>
    /// The previous assignee is deliberately not told. Being taken off something is rarely news to
    /// the person it happened to — they were usually in the conversation — and a notification for it
    /// reads as an accusation.
    /// </remarks>
    public async Task HandleAsync(WorkItemAssigned e, CancellationToken ct = default)
    {
        if (e.NewAssigneeId is not { } assignee) return;

        // Assignment starts you watching, which is what makes the bell useful without anybody
        // discovering a "watch" button first.
        await _writer.WatchAsync(e.WorkItemId, e.ProjectId, assignee, ct);

        if (assignee == e.ChangedByUserId) return;

        // Pattern-matched rather than null-checked: NotifiableItem is a struct, so `is null` on the
        // nullable form does not narrow it for the uses below.
        if (await _writer.DescribeAsync(e.WorkItemId, ct) is not { } item) return;

        await _writer.WriteAsync(new NotificationDraft(
            Recipients: [assignee],
            Type: NotificationType.WorkItemAssigned,
            EventId: e.EventId,
            Item: item,
            Title: $"{item.Reference} was assigned to you",
            Detail: item.Title,
            ActorId: e.ChangedByUserId), ct);
    }

    /// <summary>
    /// Tells the watchers, and — when work reaches the QA lane — whoever can certify it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>Resolved</c> case is the one this product exists to send. Git carries work as far as
    /// "merged, awaiting test" on its own, and a QA gate nobody is told about is just a column people
    /// have to remember to check. Reaching the right people needs the reverse permission lookup:
    /// whoever holds <c>workitem:verify</c> here, however they came by it.
    /// </para>
    /// <para>
    /// Only on the way <em>into</em> Resolved. Every later event on the same item would otherwise
    /// re-notify the whole QA group.
    /// </para>
    /// </remarks>
    public async Task HandleAsync(WorkItemStateChanged e, CancellationToken ct = default)
    {
        if (await _writer.DescribeAsync(e.WorkItemId, ct) is not { } item) return;

        var actorName = await _writer.ActorNameAsync(e.ChangedByUserId, ct);

        if (e.NewState == WorkItemState.Resolved)
        {
            var verifiers = await _rbac.GetUsersWithPermissionOnProjectAsync(
                e.ProjectId, Permissions.WorkItemVerify, ct);

            await _writer.WriteAsync(new NotificationDraft(
                Recipients: [.. verifiers.Where(id => id != e.ChangedByUserId)],
                Type: NotificationType.WorkItemAwaitingVerification,
                EventId: e.EventId,
                Item: item,
                Title: $"{item.Reference} is awaiting QA",
                Detail: item.Title,
                ActorId: e.ChangedByUserId,
                ActorName: actorName), ct);

            _logger.LogInformation(
                "{Reference} reached the QA lane; notified {Count} verifier(s).",
                item.Reference, verifiers.Count);
        }

        var watchers = await _writer.GetWatchersAsync(e.WorkItemId, ct);

        await _writer.WriteAsync(new NotificationDraft(
            Recipients: [.. watchers.Where(id => id != e.ChangedByUserId)],
            Type: NotificationType.WorkItemStateChanged,
            EventId: e.EventId,
            Item: item,
            Title: $"{item.Reference} moved to {Describe(e.NewState)}",
            Detail: item.Title,
            ActorId: e.ChangedByUserId,
            ActorName: actorName), ct);
    }

    /// <summary>Tells the watchers, and starts the author watching.</summary>
    public async Task HandleAsync(WorkItemCommentAdded e, CancellationToken ct = default)
    {
        await _writer.WatchAsync(e.WorkItemId, e.ProjectId, e.AuthorId, ct);

        if (await _writer.DescribeAsync(e.WorkItemId, ct) is not { } item) return;

        var watchers = await _writer.GetWatchersAsync(e.WorkItemId, ct);

        await _writer.WriteAsync(new NotificationDraft(
            Recipients: [.. watchers.Where(id => id != e.AuthorId)],
            Type: NotificationType.WorkItemCommented,
            EventId: e.EventId,
            Item: item,
            Title: $"New comment on {item.Reference}",
            Detail: await _writer.CommentPreviewAsync(e.CommentId, ct) ?? item.Title,
            ActorId: e.AuthorId), ct);
    }

    /// <summary>
    /// The state as a person reads it, which is not always its name.
    /// </summary>
    /// <remarks>
    /// "Awaiting QA" rather than "Resolved" — the same wording the metadata endpoint publishes, and
    /// the one that says what is actually waiting to happen.
    /// </remarks>
    private static string Describe(WorkItemState state) => state switch
    {
        WorkItemState.Resolved => "Awaiting QA",
        WorkItemState.InReview => "In Review",
        _ => state.ToString()
    };
}
