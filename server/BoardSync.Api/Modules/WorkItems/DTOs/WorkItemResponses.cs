using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.WorkItems.DTOs;

/// <remarks>
/// <para>
/// <c>Reference</c> is what people type — <c>BS-142</c> — assembled from the project's key and this
/// item's number. Show it wherever an item is identified; it is the only form a developer can put in
/// a branch name, and the whole git integration keys on it.
/// </para>
/// <para>
/// <c>Version</c> is an opaque row version. Send it back as <c>expectedVersion</c> when updating to
/// be told about a conflict instead of silently overwriting whoever edited in between. Compare only
/// — never compute with it or assume it increments by one.
/// </para>
/// <para>
/// It deliberately has no default value. It used to default to <c>0</c>, and the one place that
/// builds this record never passed it — so every response advertised version 0, every
/// <c>expectedVersion</c> a client echoed back was 0, and the conflict check could never match.
/// Without a default, forgetting it is a compile error rather than a feature that quietly does
/// nothing.
/// </para>
/// </remarks>
public record WorkItemResponse(
    Guid Id,
    Guid ProjectId,
    int Number,
    string Reference,
    Guid? TeamId,
    Guid? ParentId,
    WorkItemType Type,
    WorkItemState State,
    WorkItemPriority Priority,
    string Title,
    string? Description,
    Guid? AssigneeId,
    int? StoryPoints,
    List<string> Tags,
    int CommentCount,
    int ChildCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? CreatedBy,
    long Version
);

public record WorkItemSummaryResponse(
    Guid Id,
    WorkItemType Type,
    WorkItemState State,
    WorkItemPriority Priority,
    string Title,
    Guid? AssigneeId,
    int? StoryPoints,
    List<string> Tags,
    int ChildCount,
    DateTime CreatedAt
);

public record WorkItemCommentResponse(
    Guid Id,
    Guid WorkItemId,
    Guid AuthorId,
    string Body,
    bool IsEdited,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

/// <summary>One recorded change to a work item.</summary>
/// <remarks>
/// <para>
/// <b><see cref="ActorType"/> is the point of this record, not decoration.</b> The board moves
/// itself from git, so a client rendering history has to be able to say <em>"GitHub moved this to
/// Awaiting QA"</em> rather than attributing an automated transition to whichever person the
/// installation happens to be keyed to. Without it the product's central claim is invisible in the
/// one place it is most legible.
/// </para>
/// <para>
/// <see cref="AttributedToUserId"/> is the human the git event was traced back to — the commit
/// author, matched by email — and is null when no match was possible. It is attribution, not
/// authorship: the integration made the change.
/// </para>
/// </remarks>
/// <param name="Id">The history row.</param>
/// <param name="WorkItemId">The item it belongs to.</param>
/// <param name="ChangedBy">
/// The principal that made the change: a user id, or the git installation's id when
/// <paramref name="ActorType"/> is <c>Integration</c>.
/// </param>
/// <param name="ActorType">Whether a person or an integration made this change.</param>
/// <param name="AttributedToUserId">
/// For integration changes: the person the git event was traced to, when one was found.
/// </param>
/// <param name="FieldName">What changed.</param>
/// <param name="OldValue">What it was.</param>
/// <param name="NewValue">What it became.</param>
/// <param name="CreatedAt">When.</param>
public record WorkItemHistoryResponse(
    Guid Id,
    Guid WorkItemId,
    Guid ChangedBy,
    PrincipalType ActorType,
    Guid? AttributedToUserId,
    string FieldName,
    string? OldValue,
    string? NewValue,
    DateTime CreatedAt
);

public record WorkItemLinkResponse(
    Guid Id,
    Guid SourceId,
    Guid TargetId,
    WorkItemLinkType LinkType,
    string TargetTitle,
    WorkItemType TargetType,
    WorkItemState TargetState
);
