using System.ComponentModel.DataAnnotations;

namespace BoardSync.Api.Modules.OrgProject.DTOs;

public class CreateOrganizationRequest
{
    [Required] [MaxLength(100)]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Optional slug — auto-generated from Name if not provided.
    /// Must be lowercase alphanumeric + hyphens.
    /// </summary>
    [MaxLength(60)]
    [RegularExpression(@"^[a-z0-9]+(-[a-z0-9]+)*$", ErrorMessage = "Slug must be lowercase alphanumeric with hyphens.")]
    public string? Slug { get; init; }

    [MaxLength(500)]
    public string? Description { get; init; }
}

public class UpdateOrganizationRequest
{
    [Required] [MaxLength(100)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; init; }

    [Url] [MaxLength(2048)]
    public string? AvatarUrl { get; init; }
}

public class CreateProjectRequest
{
    [Required] [MaxLength(100)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(60)]
    [RegularExpression(@"^[a-z0-9]+(-[a-z0-9]+)*$", ErrorMessage = "Slug must be lowercase alphanumeric with hyphens.")]
    public string? Slug { get; init; }

    [MaxLength(500)]
    public string? Description { get; init; }
}

public class UpdateProjectRequest
{
    [Required] [MaxLength(100)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; init; }
}

public class CreateTeamRequest
{
    [Required] [MaxLength(100)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; init; }
}

public class UpdateTeamRequest
{
    [Required] [MaxLength(100)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; init; }
}

public class AddTeamMemberRequest
{
    [Required]
    public Guid UserId { get; init; }
}
