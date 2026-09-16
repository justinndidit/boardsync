using BoardSync.Api.Modules.OrgProject.Domain.Helpers;
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

/// <summary>The organization details an OrgAdmin may edit.</summary>
/// <remarks>
/// No avatar field. It used to carry one, validated only as <c>[Url]</c> — so an organization's
/// logo could be pointed at any host, and every member's browser would fetch it. The logo is set
/// through the avatar endpoints instead, where the API knows the image is one it stored because it
/// read the bytes back out of its own container.
/// </remarks>
public class UpdateOrganizationRequest
{
    [Required] [MaxLength(100)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; init; }
}

/// <summary>Invites somebody to an organization by email address.</summary>
/// <remarks>
/// An email, not a user id. That is the whole point of the change: at the moment an invitation is
/// created there may be no user to name, which is the case the previous "add member" flow could not
/// express — it looked the address up and gave up with a 404 if nobody held it.
/// </remarks>
public class InviteMemberRequest
{
    [Required] [EmailAddress] [MaxLength(320)]
    public string Email { get; init; } = string.Empty;

    /// <summary>
    /// The organization-scope role granted on acceptance — <c>OrgAdmin</c> or <c>Member</c>.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>Member</c>, which is what the old direct-add always granted. Validated
    /// against the roles assignable at organization scope rather than against the whole enum, so
    /// this cannot become the back door for a grant the role-change endpoint refuses.
    /// </remarks>
    [MaxLength(40)]
    public string? Role { get; init; }
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

    /// <summary>
    /// The short key people will type — the <c>BS</c> in <c>BS-142</c>. Derived from the name if
    /// omitted.
    /// </summary>
    /// <remarks>
    /// Settable only here. Changing it later orphans every branch name and commit message already
    /// pushed that referenced the old one, and those cannot be rewritten — so there is no endpoint
    /// to change it.
    /// </remarks>
    [MaxLength(ProjectKey.MaxLength)]
    [RegularExpression("^[A-Za-z][A-Za-z0-9]{1,9}$",
        ErrorMessage = "A project key must start with a letter and be 2–10 letters or digits.")]
    public string? Key { get; init; }
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
