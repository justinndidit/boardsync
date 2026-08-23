using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Domain.Models;

/// <summary>
/// A project lives inside an organization. Work items, boards and sprints are scoped to a project.
/// </summary>
public class Project : BaseEntity
{
    public Guid OrganizationId { get; set; } //project belong to organization

    public Guid AssignedTeamId {get; set;}  // foreignkey linking project to assigned team
    /// <summary>Unique slug within the organization (used in URLs).</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether someone may certify a work item assigned to them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default: signing off your own work defeats the point of certification being a separate
    /// authority from doing the work.
    /// </para>
    /// <para>
    /// A setting rather than a rule because a small team where everyone tests is a real shape, and
    /// the way people route around a rule they cannot turn off is to grant each other
    /// <c>project:admin</c> — which hands out far more than self-certification. Better that they
    /// switch this on deliberately, and that the audit trail records who signed off either way.
    /// </para>
    /// </remarks>
    public bool AllowSelfCertification { get; set; }

    public virtual Organization Organization { get; set; } = null!;
    public virtual Team AssignedTeam { get; set; } = null!;
}
