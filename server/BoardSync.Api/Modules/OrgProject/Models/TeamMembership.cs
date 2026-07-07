using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Models;

/// <summary>
/// Tracks that a user is a member of a team.
/// </summary>
public class TeamMembership : BaseEntity
{
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual Team Team { get; set; } = null!;
}
