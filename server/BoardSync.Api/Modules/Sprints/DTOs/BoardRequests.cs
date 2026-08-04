using System.ComponentModel.DataAnnotations;

namespace BoardSync.Api.Modules.Sprints.DTOs;

/// <summary>Rename an existing board.</summary>
public class UpdateBoardRequest
{
    [Required]
    [MaxLength(100)]
    public string Name { get; init; } = string.Empty;
}

/// <summary>Add a column to a board.</summary>
public class CreateBoardColumnRequest
{
    [Required]
    [MaxLength(100)]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// WorkItemState name this column maps to: "New", "Active", "Resolved", or "Closed".
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string MappedState { get; init; } = string.Empty;

    /// <summary>0-based position. Omit to append at the end.</summary>
    public int? Position { get; init; }

    /// <summary>Optional WIP limit. Null = no limit.</summary>
    public int? WipLimit { get; init; }
}

/// <summary>Update a column's properties.</summary>
public class UpdateBoardColumnRequest
{
    [Required]
    [MaxLength(100)]
    public string Name { get; init; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string MappedState { get; init; } = string.Empty;

    public int? WipLimit { get; init; }

    public int Position { get; init; }
}

/// <summary>Reorder all columns on a board.</summary>
public class ReorderBoardColumnsRequest
{
    /// <summary>Ordered list of column IDs representing the desired left-to-right order.</summary>
    [Required]
    [MinLength(1)]
    public List<Guid> ColumnIds { get; init; } = new();
}
