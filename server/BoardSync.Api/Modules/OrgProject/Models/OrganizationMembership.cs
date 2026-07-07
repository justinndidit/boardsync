using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Models;

/// <summary>
/// Tracks that a user belongs to an organization (independent of project/team membership).
/// </summary>
public class OrganizationMembership : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>When the user accepted/joined the organization.</summary>
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual Organization Organization { get; set; } = null!;
}
