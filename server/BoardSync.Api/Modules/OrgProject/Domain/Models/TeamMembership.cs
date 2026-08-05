using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Domain.Models;

/// <summary>
/// Tracks that a user is a member of a team.
/// </summary>
/// <remarks>
/// UserId references a User in a different module by convention (no direct
/// FK/navigation across module boundaries — enforced at the application layer).
/// </remarks>
public class TeamMembership : BaseEntity
{
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    // Navigation
    public virtual Team Team { get; set; } = null!;
}