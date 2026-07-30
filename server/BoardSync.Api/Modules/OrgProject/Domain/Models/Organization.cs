using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Models;

/// <summary>
/// Top-level tenant container. A user can belong to multiple organizations.
/// </summary>
public class Organization : BaseEntity
{
    /// <summary>Unique URL-friendly identifier (slug).</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public bool IsActive { get; set; } = true;

    // Navigation
    public virtual ICollection<Project> Projects { get; set; } = new List<Project>();
    public virtual ICollection<OrganizationMembership> Members { get; set; } = new List<OrganizationMembership>();
}
