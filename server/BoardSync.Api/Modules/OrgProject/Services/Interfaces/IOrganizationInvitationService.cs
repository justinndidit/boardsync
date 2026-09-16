using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Shared.Auth.DTOs;

namespace BoardSync.Api.Modules.OrgProject.Services.Interfaces;

/// <summary>
/// Inviting people to an organization, and letting them accept.
/// </summary>
/// <remarks>
/// <para>
/// This replaces adding members outright. An administrator could previously look up an address and
/// put that user into their organization with no notice and no consent — and could do nothing at
/// all for somebody who had not signed up, because the lookup 404'd.
/// </para>
/// <para>
/// The rule that makes a mailed link safe to hand out is in <see cref="AcceptAsync"/>: the account
/// accepting must own the address the invitation was sent to. Without that, the link alone is
/// membership, and a forwarded email would be a way into somebody else's organization.
/// </para>
/// </remarks>
public interface IOrganizationInvitationService
{
    /// <summary>Creates an invitation and emails it. Supersedes any invitation still open for the address.</summary>
    Task<ApiResponse<OrganizationInvitationResponse>> InviteAsync(
        Guid orgId, InviteMemberRequest request, Guid invitedBy, CancellationToken ct);

    /// <summary>This organization's invitations, newest first.</summary>
    Task<IReadOnlyList<OrganizationInvitationResponse>> ListAsync(
        Guid orgId, bool openOnly, CancellationToken ct);

    /// <summary>Withdraws an invitation that has not been accepted.</summary>
    Task<ApiResponse> RevokeAsync(Guid orgId, Guid invitationId, CancellationToken ct);

    /// <summary>
    /// What the holder of a link is shown before they accept — reachable without signing in.
    /// </summary>
    /// <remarks>
    /// Anonymous by necessity: the recipient may have no account, and the page has to know whether
    /// to offer sign-in or sign-up before it can ask them for anything.
    /// </remarks>
    Task<ApiResponse<InvitationPreviewResponse>> PreviewAsync(string token, CancellationToken ct);

    /// <summary>Accepts an invitation as <paramref name="userId"/>, who must own the invited address.</summary>
    Task<ApiResponse<InvitationAcceptedResponse>> AcceptAsync(
        string token, Guid userId, CancellationToken ct);

    /// <summary>
    /// The invitation a registration is being completed against, if the token is good for that
    /// address — used to activate the new account without a second confirmation email.
    /// </summary>
    /// <remarks>
    /// Reading the invitation <i>is</i> proof of the address: it was sent there, and only somebody
    /// who can read that mailbox has the token. Making them then confirm a second email proves the
    /// same fact twice and puts one more step between accepting an invitation and being in the
    /// organization.
    /// </remarks>
    Task<bool> IsOpenForEmailAsync(string token, string email, CancellationToken ct);
}
