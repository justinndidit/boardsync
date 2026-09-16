using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BoardSync.Api.Modules.OrgProject.Controllers;

/// <summary>
/// The receiving end of an organization invitation — what the link in the email opens.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>OrganizationsController</c> because these are addressed by <b>token</b>, not by
/// organization id. The caller does not know which organization they are joining until the token is
/// read, so there is no <c>orgId</c> to authorize against and the usual
/// <c>[RequirePermission(..., From = "orgId")]</c> has nothing to bind to.
/// </para>
/// <para>
/// Rate-limited on the auth bucket. Both endpoints take an opaque token and say whether it is good,
/// which is the shape of something worth guessing at; the tokens are 256 bits of CSPRNG output, and
/// the limiter keeps that from being tested at speed.
/// </para>
/// </remarks>
[ApiController]
[Route("api/invitations")]
[EnableRateLimiting("auth")]
[Produces("application/json")]
public class InvitationsController : ControllerBase
{
    private readonly IOrganizationInvitationService _invitations;
    private readonly ICurrentUserContext _currentUser;

    public InvitationsController(
        IOrganizationInvitationService invitations,
        ICurrentUserContext currentUser)
    {
        _invitations = invitations;
        _currentUser = currentUser;
    }

    /// <summary>
    /// What an invitation link is offering, without signing in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anonymous by necessity: the recipient may have no account, and the page cannot decide
    /// between "sign in" and "create an account" until it knows which. That decision needs the
    /// invited address and whether an account holds it, which is exactly what this returns.
    /// </para>
    /// <para>
    /// It deliberately returns nothing else about either side — no membership, no other
    /// invitations, no user detail beyond the inviter's display name. A link that reaches the wrong
    /// inbox should not become a readout of an organization.
    /// </para>
    /// </remarks>
    /// <param name="token">The token from the emailed link.</param>
    /// <response code="200">The invitation is open; returns what it offers</response>
    /// <response code="400">Unknown, already used, withdrawn, or expired</response>
    [HttpGet("{token}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<InvitationPreviewResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Preview(string token, CancellationToken ct)
    {
        var result = await _invitations.PreviewAsync(token, ct);

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Accept an invitation as the signed-in user.
    /// </summary>
    /// <remarks>
    /// The account must own the address the invitation was sent to. A mailed token is a bearer
    /// credential — forwarded, quoted in a reply, or read off a shared screen it reaches people it
    /// was not addressed to — so holding the link is deliberately not enough on its own.
    /// </remarks>
    /// <param name="token">The token from the emailed link.</param>
    /// <response code="200">Joined; returns the organization to route to</response>
    /// <response code="400">Not open, or the signed-in account is not the invited address</response>
    /// <response code="401">Not signed in</response>
    [HttpPost("{token}/accept")]
    [Authorize]
    [NoPermissionRequired(
        "Authorized by the token and by the caller owning the address it was sent to; there is no "
        + "scope to check because the caller is not in the organization yet.")]
    [ProducesResponseType(typeof(ApiResponse<InvitationAcceptedResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Accept(string token, CancellationToken ct)
    {
        var result = await _invitations.AcceptAsync(token, _currentUser.UserId, ct);

        return result.Success ? Ok(result) : BadRequest(result);
    }
}
