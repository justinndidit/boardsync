using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.OrgProject.Domain.Models;
using BoardSync.Api.Modules.OrgProject.Repositories.Interfaces;
using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Shared.Auth.Configuration;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth.Services;
using BoardSync.Api.Shared.Kernel.Exceptions;
using Microsoft.Extensions.Options;

namespace BoardSync.Api.Modules.OrgProject.Services.Implementations;

/// <inheritdoc cref="IOrganizationInvitationService"/>
public class OrganizationInvitationService : IOrganizationInvitationService
{
    private readonly IOrganizationInvitationRepository _invitations;
    private readonly IOrganizationRepository _organizations;
    private readonly IOrganizationService _organizationService;
    private readonly IUserService _users;
    private readonly ITokenService _tokens;
    private readonly IEmailService _email;
    private readonly SecuritySettings _security;
    private readonly EmailSettings _emailSettings;
    private readonly ILogger<OrganizationInvitationService> _logger;

    public OrganizationInvitationService(
        IOrganizationInvitationRepository invitations,
        IOrganizationRepository organizations,
        IOrganizationService organizationService,
        IUserService users,
        ITokenService tokens,
        IEmailService email,
        IOptions<SecuritySettings> security,
        IOptions<EmailSettings> emailSettings,
        ILogger<OrganizationInvitationService> logger)
    {
        _invitations = invitations;
        _organizations = organizations;
        _organizationService = organizationService;
        _users = users;
        _tokens = tokens;
        _email = email;
        _security = security.Value;
        _emailSettings = emailSettings.Value;
        _logger = logger;
    }

    /// <summary>One definition of how an address becomes a key, matching <c>User.Email</c>.</summary>
    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    public async Task<ApiResponse<OrganizationInvitationResponse>> InviteAsync(
        Guid orgId, InviteMemberRequest request, Guid invitedBy, CancellationToken ct)
    {
        var org = await _organizations.GetActiveAsync(orgId, ct)
            ?? throw new NotFoundException(nameof(Organization), orgId);

        var email = Normalize(request.Email);

        /*
         * Validated against the roles assignable at organization scope rather than against the
         * whole enum. Otherwise this becomes the back door for a grant the role-change endpoint
         * refuses — 'ProjectAdmin' at organization scope, say, which grants nothing and reads to an
         * administrator as though it grants everything.
         */
        if (!TryResolveRole(request.Role, out var role))
        {
            return new ApiResponse<OrganizationInvitationResponse>(
                false,
                $"'{request.Role}' is not a role that can be granted at organization scope. "
                + $"Use one of: {string.Join(", ", RolePermissions.AssignableAt(RoleScope.Organization))}.");
        }

        // Somebody already inside does not need an invitation, and sending one would produce a
        // link that can only ever fail on accept.
        var existing = await _users.GetByEmailAsync(email);

        if (existing.Success && existing.Data is not null &&
            await _organizationService.IsMemberAsync(orgId, existing.Data.Id, ct))
        {
            return new ApiResponse<OrganizationInvitationResponse>(
                false, $"{email} is already a member of this organization.");
        }

        var now = DateTime.UtcNow;

        /*
         * Re-inviting supersedes rather than duplicates. Two open invitations to one address are
         * two working links to the same membership, and revoking the one the administrator can see
         * would leave the other alive.
         */
        var open = await _invitations.GetOpenForEmailAsync(orgId, email, now, ct);

        if (open is not null)
        {
            open.RevokedAt = now;
            open.UpdatedAt = now;
        }

        // The raw token exists here and in the email, and nowhere else — the row keeps its hash.
        var token = _tokens.GenerateEmailConfirmationToken();

        var invitation = new OrganizationInvitation
        {
            OrganizationId = orgId,
            Email = email,
            Role = role,
            TokenHash = _tokens.HashToken(token),
            ExpiresAt = now.AddDays(_security.InvitationExpirationDays),
            CreatedBy = invitedBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _invitations.Add(invitation);
        await _invitations.SaveChangesAsync(ct);

        var inviter = await _users.GetByIdAsync(invitedBy);

        var sent = await _email.SendOrganizationInviteAsync(
            email,
            org.Name,
            inviter.Data?.DisplayName,
            token,
            AppBaseUrl(),
            invitation.ExpiresAt);

        if (!sent.Success)
        {
            /*
             * The row stays. The invitation is real and an administrator can send it again from the
             * members screen; deleting it here would also throw away the supersede above, quietly
             * reviving a link that was meant to be replaced.
             */
            _logger.LogError(
                "Invitation to {Email} for organization {OrgId} was created but not sent: {Message}",
                email, orgId, sent.Message);

            return new ApiResponse<OrganizationInvitationResponse>(
                false,
                "The invitation was created but the email could not be sent. Try sending it again.");
        }

        _logger.LogInformation(
            "{UserId} invited {Email} to organization {OrgId} as {Role}",
            invitedBy, email, orgId, role);

        return new ApiResponse<OrganizationInvitationResponse>(
            true,
            $"Invitation sent to {email}.",
            ToResponse(invitation, inviter.Data?.DisplayName, now));
    }

    public async Task<IReadOnlyList<OrganizationInvitationResponse>> ListAsync(
        Guid orgId, bool openOnly, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await _invitations.ListAsync(orgId, openOnly, now, ct);

        /*
         * Inviter names resolved in one pass over the distinct ids rather than per row: a members
         * screen listing twenty invitations from two administrators should ask about two people.
         */
        var inviterIds = rows
            .Where(i => i.CreatedBy.HasValue)
            .Select(i => i.CreatedBy!.Value)
            .Distinct()
            .ToList();

        var names = new Dictionary<Guid, string?>();

        foreach (var id in inviterIds)
        {
            var user = await _users.GetByIdAsync(id);
            names[id] = user.Data?.DisplayName;
        }

        return rows
            .Select(i => ToResponse(
                i,
                i.CreatedBy.HasValue ? names.GetValueOrDefault(i.CreatedBy.Value) : null,
                now))
            .ToList();
    }

    public async Task<ApiResponse> RevokeAsync(Guid orgId, Guid invitationId, CancellationToken ct)
    {
        var invitation = await _invitations.GetAsync(orgId, invitationId, ct)
            ?? throw new NotFoundException("Invitation", invitationId);

        if (invitation.AcceptedAt is not null)
        {
            // Revoking would say the person is not a member when they are. Removing a member is a
            // different act, with its own endpoint and its own last-OrgAdmin protection.
            return new ApiResponse(
                false, "That invitation has already been accepted. Remove the member instead.");
        }

        if (invitation.RevokedAt is not null)
            return new ApiResponse(true, "Invitation already revoked.");

        invitation.RevokedAt = DateTime.UtcNow;
        invitation.UpdatedAt = invitation.RevokedAt.Value;

        await _invitations.SaveChangesAsync(ct);

        return new ApiResponse(true, "Invitation revoked.");
    }

    public async Task<ApiResponse<InvitationPreviewResponse>> PreviewAsync(
        string token, CancellationToken ct)
    {
        var (invitation, error) = await OpenInvitationAsync(token, ct);

        if (invitation is null)
            return new ApiResponse<InvitationPreviewResponse>(false, error!);

        var account = await _users.GetByEmailAsync(invitation.Email);

        return new ApiResponse<InvitationPreviewResponse>(
            true,
            "Invitation found.",
            new InvitationPreviewResponse(
                invitation.Organization.Name,
                invitation.Email,
                invitation.Role.ToString(),
                invitation.CreatedBy.HasValue
                    ? (await _users.GetByIdAsync(invitation.CreatedBy.Value)).Data?.DisplayName
                    : null,
                invitation.ExpiresAt,
                account.Success && account.Data is not null));
    }

    public async Task<ApiResponse<InvitationAcceptedResponse>> AcceptAsync(
        string token, Guid userId, CancellationToken ct)
    {
        var (invitation, error) = await OpenInvitationAsync(token, ct);

        if (invitation is null)
            return new ApiResponse<InvitationAcceptedResponse>(false, error!);

        var user = await _users.GetByIdAsync(userId);

        if (!user.Success || user.Data is null)
            return new ApiResponse<InvitationAcceptedResponse>(false, "User not found.");

        /*
         * The rule the whole design rests on.
         *
         * A token in an email is a bearer credential: forwarded, quoted in a reply, or read off a
         * shared screen, it reaches people it was not addressed to. Binding acceptance to the
         * invited address means holding the link is not enough — you must also control the mailbox
         * it was sent to, which is the thing the administrator actually vouched for.
         */
        if (!string.Equals(Normalize(user.Data.Email), invitation.Email, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "User {UserId} tried to accept an invitation addressed to {Email}",
                userId, invitation.Email);

            return new ApiResponse<InvitationAcceptedResponse>(
                false,
                $"This invitation was sent to {invitation.Email}. "
                + "Sign in with that address to accept it.");
        }

        if (await _organizationService.IsMemberAsync(invitation.OrganizationId, userId, ct))
        {
            // Not an error worth showing: they are where the invitation was taking them. Mark it
            // used so it stops appearing as outstanding.
            await MarkAcceptedAsync(invitation, userId, ct);

            return Accepted(invitation, "You are already a member of this organization.");
        }

        await _organizationService.AddMemberAsync(
            invitation.OrganizationId, userId, invitation.CreatedBy ?? userId, invitation.Role, ct);

        await MarkAcceptedAsync(invitation, userId, ct);

        _logger.LogInformation(
            "User {UserId} accepted an invitation to organization {OrgId} as {Role}",
            userId, invitation.OrganizationId, invitation.Role);

        return Accepted(invitation, $"You have joined {invitation.Organization.Name}.");
    }

    public async Task<bool> IsOpenForEmailAsync(string token, string email, CancellationToken ct)
    {
        var (invitation, _) = await OpenInvitationAsync(token, ct);

        return invitation is not null
               && string.Equals(invitation.Email, Normalize(email), StringComparison.Ordinal);
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The invitation a token names, if it can still be accepted — or why it cannot.
    /// </summary>
    /// <remarks>
    /// One place decides "is this link still good", so the preview and the accept can never
    /// disagree — which is how an expired invitation ends up refused on one screen and honoured on
    /// the next. The reasons are distinguished because "this expired" and "this was withdrawn" ask
    /// different things of the reader, while an unknown token stays deliberately vague.
    /// </remarks>
    private async Task<(OrganizationInvitation? Invitation, string? Error)> OpenInvitationAsync(
        string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (null, "That invitation link is not valid.");

        var invitation = await _invitations.GetByTokenHashAsync(_tokens.HashToken(token), ct);

        if (invitation is null)
            return (null, "That invitation link is not valid.");

        if (invitation.AcceptedAt is not null)
            return (null, "That invitation has already been used.");

        if (invitation.RevokedAt is not null)
            return (null, "That invitation has been withdrawn.");

        if (invitation.ExpiresAt <= DateTime.UtcNow)
            return (null, "That invitation has expired. Ask an administrator to send a new one.");

        return (invitation, null);
    }

    private async Task MarkAcceptedAsync(
        OrganizationInvitation invitation, Guid userId, CancellationToken ct)
    {
        invitation.AcceptedAt = DateTime.UtcNow;
        invitation.AcceptedByUserId = userId;
        invitation.UpdatedAt = invitation.AcceptedAt.Value;

        await _invitations.SaveChangesAsync(ct);
    }

    private static ApiResponse<InvitationAcceptedResponse> Accepted(
        OrganizationInvitation invitation, string message) =>
        new(true,
            message,
            new InvitationAcceptedResponse(
                invitation.OrganizationId,
                invitation.Organization.Slug,
                invitation.Organization.Name));

    private static bool TryResolveRole(string? requested, out RoleType role)
    {
        role = RoleType.Member;

        if (string.IsNullOrWhiteSpace(requested))
            return true;

        return Enum.TryParse(requested, ignoreCase: true, out role)
               && RolePermissions.AssignableAt(RoleScope.Organization).Contains(role);
    }

    /// <summary>Status is derived, never stored, so it cannot disagree with its own timestamps.</summary>
    private static OrganizationInvitationResponse ToResponse(
        OrganizationInvitation i, string? invitedBy, DateTime asOf) =>
        new(i.Id,
            i.Email,
            i.Role.ToString(),
            i.AcceptedAt is not null ? "Accepted"
                : i.RevokedAt is not null ? "Revoked"
                : i.ExpiresAt <= asOf ? "Expired"
                : "Pending",
            invitedBy,
            i.ExpiresAt,
            i.CreatedAt);

    /// <summary>
    /// Where the app is, not where the API is.
    /// </summary>
    /// <remarks>
    /// An invitation link opens a screen, so it must point at the single-page app — unlike the
    /// confirmation and reset links, which are API endpoints.
    /// </remarks>
    private string AppBaseUrl() => _emailSettings.AppBaseUrl.TrimEnd('/');
}
