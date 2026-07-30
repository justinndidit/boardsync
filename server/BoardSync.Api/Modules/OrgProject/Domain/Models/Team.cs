using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Models;

/// <summary>
/// A team within a project. Boards and iterations are scoped to a team.
/// </summary>
public class Team : BaseEntity
{
    public Guid ProjectId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    // Navigation
    public virtual Project Project { get; set; } = null!;
    public virtual ICollection<TeamMembership> Members { get; set; } = new List<TeamMembership>();
}
