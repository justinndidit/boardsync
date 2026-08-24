using BoardSync.Api.Modules.Notifications.Models;

namespace BoardSync.Api.Modules.Notifications.DTOs;

/// <summary>
/// One entry in the notification bell.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>The shape changed.</b> The bell used to return work item history rows worded into
/// sentences, with a <c>type</c> drawn from a field name and no read state — because there was no
/// notification to have state. It is now a real record addressed to one person.
/// </para>
/// <para>
/// Everything needed to render a row is here, so a bell showing twenty entries makes one request and
/// no joins.
/// </para>
/// </remarks>
/// <param name="Id">The notification.</param>
/// <param name="Type">Why it was sent. Switch on this for the icon and the destination.</param>
/// <param name="Title">One line, already worded — e.g. "BS-142 is awaiting QA".</param>
/// <param name="Detail">Supporting detail: the work item title, or a comment's first line.</param>
/// <param name="Reference">The work item as people call it, e.g. <c>BS-142</c>.</param>
/// <param name="EntityId">The work item, for the deep link.</param>
/// <param name="ProjectId">Its project.</param>
/// <param name="ActorName">
/// Who caused it. <b>May be an integration</b> — "GitHub" rather than a person — now that git moves
/// the board on its own.
/// </param>
/// <param name="IsRead">Whether the recipient has read it.</param>
/// <param name="CreatedAt">When it was raised.</param>
public record NotificationResponse(
    Guid Id,
    NotificationType Type,
    string Title,
    string? Detail,
    string Reference,
    Guid EntityId,
    Guid ProjectId,
    string ActorName,
    bool IsRead,
    DateTime CreatedAt);

/// <summary>The bell's contents and its badge.</summary>
/// <param name="Items">The entries, newest first.</param>
/// <param name="UnreadCount">
/// How many unread the recipient has in total, which is not the same as how many unread are in
/// <paramref name="Items"/> — the badge has to be right even when the list is truncated.
/// </param>
public record NotificationFeedResponse(
    IReadOnlyList<NotificationResponse> Items,
    int UnreadCount);

/// <summary>Whether the caller is watching a work item.</summary>
public record WatchStateResponse(Guid WorkItemId, bool IsWatching);
