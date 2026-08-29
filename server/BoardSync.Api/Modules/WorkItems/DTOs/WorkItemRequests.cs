using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Kernel;
using System.ComponentModel.DataAnnotations;

namespace BoardSync.Api.Modules.WorkItems.DTOs;

public class CreateWorkItemRequest
{
    [Required]
    [MaxLength(255)]
    public string Title { get; init; } = string.Empty;

    [MaxLength(10000)]
    public string? Description { get; init; }

    [Required]
    public string Type { get; init; } = string.Empty;

    public WorkItemPriority Priority { get; init; } = WorkItemPriority.Medium;

    /// <summary>Required team member assined to task.</summary>
    public Guid AssigneeId { get; init; }

    /// <summary>Parent work item ID for hierarchy (e.g., Epic → Feature → Story → Task).</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Team scope.</summary>
    public Guid TeamId { get; init; }

    [Range(0, 1000)]
    public int? StoryPoints { get; init; }

    public List<string> Tags { get; init; } = new();
}

/// <summary>
/// A partial update: only the fields present are changed.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UpdateWorkItemRequest"/> is a full replace, so changing one field means sending the
/// whole object back — which forces a read-modify-write and overwrites anything another editor
/// changed in the meantime with values that were current when you loaded the form. That is the
/// wrong shape for a person editing one field, and the wrong shape for the coming git integration,
/// which wants to move state and touch nothing else.
/// </para>
/// <para>
/// Every field is a <see cref="Patch{T}"/>, so <c>{"assigneeId": null}</c> unassigns and omitting
/// <c>assigneeId</c> leaves it alone. Sending <c>{}</c> is valid and changes nothing.
/// </para>
/// <para>
/// State is deliberately absent. It moves through <c>PATCH /api/workitems/{id}/state</c>, which
/// enforces the workflow and the QA gate; allowing it here would be a second, unguarded door.
/// </para>
/// </remarks>
public class PatchWorkItemRequest
{
    /// <inheritdoc cref="UpdateWorkItemRequest.ExpectedVersion" />
    public long? ExpectedVersion { get; init; }

    public Patch<string> Title { get; init; }

    public Patch<string> Description { get; init; }

    public Patch<WorkItemPriority> Priority { get; init; }

    public Patch<Guid?> AssigneeId { get; init; }

    public Patch<Guid?> TeamId { get; init; }

    public Patch<int?> StoryPoints { get; init; }

    public Patch<List<string>> Tags { get; init; }
}

public class UpdateWorkItemRequest
{
    /// <summary>
    /// The <c>version</c> from the work item you read, when you want conflicts reported.
    /// </summary>
    /// <remarks>
    /// Optional, and omitting it keeps the previous behaviour — last write wins, no conflict
    /// signal. Supplying it turns "somebody else changed this while you were editing" from a silent
    /// overwrite into a <c>409</c> carrying the current state, which is the only way a client can
    /// reconcile rather than clobber.
    /// </remarks>
    public long? ExpectedVersion { get; init; }

    [Required]
    [MaxLength(255)]
    public string Title { get; init; } = string.Empty;

    [MaxLength(10000)]
    public string? Description { get; init; }

    public WorkItemPriority Priority { get; init; } = WorkItemPriority.Medium;

    public Guid? AssigneeId { get; init; }

    public Guid? TeamId { get; init; }

    /// <summary>
    /// The work item this one sits under, or null for none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a PUT, so omitting it clears the parent</b> — the same semantic
    /// <see cref="TeamId"/> and <see cref="AssigneeId"/> already have here. A client that edits a
    /// title must send the parent it read, or it will silently orphan the item. Use
    /// <c>PATCH</c> when you want to change one field and say nothing about the rest.
    /// </para>
    /// <para>
    /// Re-parenting was previously impossible: the parent could be chosen at creation and never
    /// afterwards, so a wrong one could only be fixed by deleting the item and making another —
    /// which takes a new number and leaves its history behind.
    /// </para>
    /// </remarks>
    public Guid? ParentId { get; init; }

    [Range(0, 1000)]
    public int? StoryPoints { get; init; }

    public List<string> Tags { get; init; } = new();
}

public class UpdateWorkItemStateRequest
{
    [Required]
    public WorkItemState State { get; init; }

    /// <inheritdoc cref="UpdateWorkItemRequest.ExpectedVersion" />
    public long? ExpectedVersion { get; init; }
}

public class AddWorkItemCommentRequest
{
    [Required]
    [MaxLength(10000)]
    public string Body { get; init; } = string.Empty;
}

public class UpdateWorkItemCommentRequest
{
    [Required]
    [MaxLength(10000)]
    public string Body { get; init; } = string.Empty;
}

public class AddWorkItemLinkRequest
{
    [Required]
    public Guid TargetId { get; init; }

    [Required]
    public WorkItemLinkType LinkType { get; init; }
}

public class WorkItemFilterQuery
{
    public WorkItemType? Type { get; init; }
    public WorkItemState? State { get; init; }
    public Guid? AssigneeId { get; init; }
    public Guid? TeamId { get; init; }
    public string? Tag { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
