using BoardSync.Api.Modules.OrgProject.Domain.Models;

namespace BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;

/// <summary>
/// Persistence for organization invitations.
///
/// Pure unit of work, like <see cref="IOrganizationRepository"/>: <c>Add</c> stages a change and
/// nothing is written until <see cref="SaveChangesAsync"/>, so accepting an invitation and the
/// membership it creates can land in one transaction.
/// </summary>
public interface IOrganizationInvitationRepository
{
    /// <summary>
    /// The invitation a token names, tracked for mutation, or null.
    /// </summary>
    /// <remarks>
    /// Takes the <b>hash</b>, never the token: the raw value exists only in the email that was
    /// sent, and hashing before the query is what keeps it out of logs, parameter captures and
    /// query plans.
    /// </remarks>
    Task<OrganizationInvitation?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>One organization's invitation by id, tracked for mutation, or null.</summary>
    Task<OrganizationInvitation?> GetAsync(
        Guid organizationId, Guid invitationId, CancellationToken ct = default);

    /// <summary>
    /// The still-acceptable invitation for an address in this organization, if there is one.
    /// </summary>
    /// <remarks>
    /// Used to refuse a duplicate and to supersede on re-send. Expired and revoked rows are not
    /// returned — they are history, and a new invitation to the same address is normal.
    /// </remarks>
    Task<OrganizationInvitation?> GetOpenForEmailAsync(
        Guid organizationId, string email, DateTime asOf, CancellationToken ct = default);

    /// <summary>This organization's invitations, newest first.</summary>
    Task<IReadOnlyList<OrganizationInvitation>> ListAsync(
        Guid organizationId, bool openOnly, DateTime asOf, CancellationToken ct = default);

    void Add(OrganizationInvitation invitation);

    Task SaveChangesAsync(CancellationToken ct = default);
}
