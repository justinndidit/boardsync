using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Models;

/// <summary>
/// A project lives inside an organization. Work items, boards and sprints are scoped to a project.
/// </summary>
public class Project : BaseEntity
{
    public Guid OrganizationId { get; set; }

    /// <summary>Unique slug within the organization (used in URLs).</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    // Navigation
    public virtual Organization Organization { get; set; } = null!;
    public virtual ICollection<Team> Teams { get; set; } = new List<Team>();
}
