using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.OrgProject.Domain.Models;

/// <summary>
/// An offer of membership, addressed to an email rather than to a user.
/// </summary>
/// <remarks>
/// <para>
/// Membership used to be something an administrator did <i>to</i> somebody: the UI looked an email
/// up, and the API added that user to the organization on the spot — no notice, no consent, and no
/// path at all for a person who had not signed up yet, because the lookup simply 404'd.
/// </para>
/// <para>
/// The invitation is the missing record. It is keyed on the <b>email</b>, deliberately, because at
/// the moment it is created there may be no user to key on — that is the case the old flow could
/// not express. Whoever eventually accepts has to prove they own that address, either by already
/// holding an account for it or by registering with it.
/// </para>
/// </remarks>
public class OrganizationInvitation : BaseEntity
{
    public Guid OrganizationId { get; set; }

    /// <summary>The address invited, normalized the way <c>User.Email</c> is.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The organization-scope role granted on acceptance.
    /// </summary>
    /// <remarks>
    /// Stored as a name rather than an ordinal, for the same reason <c>RoleAssignment.Role</c> is:
    /// a readable audit row survives enum renumbering, and nothing compares these ordinally.
    /// </remarks>
    public RoleType Role { get; set; } = RoleType.Member;

    /// <summary>
    /// SHA-256 of the token that was emailed — never the token itself.
    /// </summary>
    /// <remarks>
    /// The same treatment password-reset and email-confirmation tokens already get. A leaked
    /// database read is then not a pile of working invitations into every organization, and an
    /// administrator reading the table cannot mint a membership from a row.
    /// </remarks>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    /// <summary>Set when somebody accepted. Null while the invitation is still open.</summary>
    public DateTime? AcceptedAt { get; set; }

    /// <summary>Who accepted — not necessarily anyone who existed when it was sent.</summary>
    public Guid? AcceptedByUserId { get; set; }

    /// <summary>Set when an administrator withdrew it before it was accepted.</summary>
    public DateTime? RevokedAt { get; set; }

    // Navigation
    public virtual Organization Organization { get; set; } = null!;

    /// <summary>
    /// Whether this invitation can still be accepted, at <paramref name="asOf"/>.
    /// </summary>
    /// <remarks>
    /// One definition, used by the lookup, the preview and the accept. Three separate spellings of
    /// "is it still good" is how an expired invitation ends up accepted down one path and refused
    /// down another.
    /// </remarks>
    public bool IsOpen(DateTime asOf) =>
        AcceptedAt is null && RevokedAt is null && ExpiresAt > asOf;
}
