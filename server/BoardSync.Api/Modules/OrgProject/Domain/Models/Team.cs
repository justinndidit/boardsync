using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Domain.Models;

/// <summary>
/// A team within an organization. Can be assigned to multiple projects.
/// Boards and iterations are scoped to a team.
/// </summary>
public class Team : BaseEntity
{
    public Guid OrganizationId { get; set; } //Teams belong to Organization
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public virtual ICollection<TeamMembership> Members { get; set; } = new List<TeamMembership>();
    public virtual ICollection<Project> AssignedProjects { get; set; } = new List<Project>();
}