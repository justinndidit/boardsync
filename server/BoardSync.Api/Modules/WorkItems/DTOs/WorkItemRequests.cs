using BoardSync.Api.Modules.WorkItems.Models;
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
    public WorkItemType Type { get; init; }

    public WorkItemPriority Priority { get; init; } = WorkItemPriority.Medium;

    public Guid? AssigneeId { get; init; }

    /// <summary>Parent work item ID for hierarchy (e.g., Epic → Feature → Story → Task).</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Optional team scope.</summary>
    public Guid? TeamId { get; init; }

    [Range(0, 1000)]
    public int? StoryPoints { get; init; }

    public List<string> Tags { get; init; } = new();
}

public class UpdateWorkItemRequest
{
    [Required]
    [MaxLength(255)]
    public string Title { get; init; } = string.Empty;

    [MaxLength(10000)]
    public string? Description { get; init; }

    public WorkItemPriority Priority { get; init; } = WorkItemPriority.Medium;

    public Guid? AssigneeId { get; init; }

    public Guid? TeamId { get; init; }

    [Range(0, 1000)]
    public int? StoryPoints { get; init; }

    public List<string> Tags { get; init; } = new();
}

public class UpdateWorkItemStateRequest
{
    [Required]
    public WorkItemState State { get; init; }
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
