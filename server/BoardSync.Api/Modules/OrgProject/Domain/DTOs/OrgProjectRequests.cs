using System.ComponentModel.DataAnnotations;

namespace BoardSync.Api.Modules.OrgProject.Domain.DTOs;

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

    /// <summary>
    /// The team that will own this project's work. Must be an active team in the same
    /// organization. Create the team first — teams belong to the organization, not the project.
    /// </summary>
    [Required]
    public Guid AssignedTeamId { get; init; }
}

public class UpdateProjectRequest
{
    [Required] [MaxLength(100)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; init; }

    /// <summary>
    /// Whether someone may certify a work item assigned to them. Omitted leaves it unchanged.
    /// </summary>
    /// <remarks>
    /// Nullable, unlike the fields above, because this is a switch rather than a value: a client
    /// editing the project's name must not silently turn the QA separation off by not mentioning it.
    /// </remarks>
    public bool? AllowSelfCertification { get; init; }
}

/// <summary>Reassign a project to a different team in the same organization.</summary>
public class AssignProjectTeamRequest
{
    [Required]
    public Guid AssignedTeamId { get; init; }
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

/// <summary>
/// Who should hold a team position. No <c>[Required]</c>: a Guid cannot be absent, and an empty one
/// fails the team-membership check with a message that says what is actually wrong.
/// </summary>
public record AssignTeamPositionRequest(Guid UserId);
