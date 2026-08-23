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

    /// <summary>
    /// The short key people type: the <c>BS</c> in <c>BS-142</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upper-case, 2–10 alphanumerics, unique within the organization. Distinct from
    /// <see cref="Slug"/> on purpose: a slug lives in URLs and wants to be long and descriptive,
    /// while this is typed into branch names and commit messages and wants to be short.
    /// </para>
    /// <para>
    /// <b>Effectively immutable.</b> Renaming it orphans every branch and commit message in flight
    /// that referenced the old one, so there is no endpoint to change it — the cost lands on people
    /// who have already pushed and cannot rewrite what they typed.
    /// </para>
    /// </remarks>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The number the next work item in this project will get.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A counter column rather than a Postgres sequence, because it has to be allocated inside the
    /// same transaction as the work item and roll back with it — a sequence does not roll back, so a
    /// failed create would burn a number and leave a permanent gap in what people read as a
    /// continuous list.
    /// </para>
    /// <para>
    /// Allocated by an atomic <c>UPDATE … RETURNING</c>, which holds a row lock only for that one
    /// statement. Two concurrent creates in the same project serialize on this row for microseconds;
    /// creates in different projects never touch each other.
    /// </para>
    /// </remarks>
    public int NextWorkItemNumber { get; set; } = 1;

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
