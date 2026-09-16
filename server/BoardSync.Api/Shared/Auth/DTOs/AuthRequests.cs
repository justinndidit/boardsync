using System.ComponentModel.DataAnnotations;

namespace BoardSync.Api.Shared.Auth.DTOs;

// Authentication Requests
public class LoginRequest
{
    [Required] [EmailAddress] 
    public string Email { get; init; } = string.Empty;
    
    [Required] [MinLength(6)] 
    public string Password { get; init; } = string.Empty;
    
    public bool RememberMe { get; init; } = false;
}

public class RegisterRequest
{
    [Required] [EmailAddress] 
    public string Email { get; init; } = string.Empty;
    
    [Required] [MinLength(6)] 
    public string Password { get; init; } = string.Empty;
    
    [Required] [Compare(nameof(Password))] 
    public string ConfirmPassword { get; init; } = string.Empty;
    
    [Required] [MaxLength(50)] 
    public string FirstName { get; init; } = string.Empty;
    
    [Required] [MaxLength(50)] 
    public string LastName { get; init; } = string.Empty;
    
    [MaxLength(100)] 
    public string? DisplayName { get; init; }

    /// <summary>
    /// An organization invitation this registration is completing, if it came from one.
    /// </summary>
    /// <remarks>
    /// When the token is open and addressed to <see cref="Email"/>, the account is active
    /// immediately and no confirmation email is sent. Reading the invitation <i>is</i> the proof
    /// confirmation asks for: it was sent to that address, and only somebody who can read that
    /// mailbox has the token. Requiring a second email proves the same fact twice and puts another
    /// step between accepting an invitation and being in the organization.
    ///
    /// A token that is absent, expired, revoked, already used, or addressed elsewhere is ignored
    /// entirely — registration proceeds down the ordinary confirmation path rather than failing.
    /// </remarks>
    public string? InviteToken { get; init; }
}

public record ForgotPasswordRequest(
    [Required] [EmailAddress] string Email
);

public class ResetPasswordRequest
{
    [Required] [EmailAddress] 
    public string Email { get; init; } = string.Empty;
    
    [Required] 
    public string Token { get; init; } = string.Empty;
    
    [Required] [MinLength(6)] 
    public string Password { get; init; } = string.Empty;
    
    [Required] [Compare(nameof(Password))] 
    public string ConfirmPassword { get; init; } = string.Empty;
}

public record RefreshTokenRequest(
    [Required] string RefreshToken
);

public record ConfirmEmailRequest(
    [Required] [EmailAddress] string Email,
    [Required] string Token
);

public class ChangePasswordRequest
{
    [Required] 
    public string CurrentPassword { get; init; } = string.Empty;
    
    [Required] [MinLength(6)] 
    public string NewPassword { get; init; } = string.Empty;
    
    [Required] [Compare(nameof(NewPassword))] 
    public string ConfirmNewPassword { get; init; } = string.Empty;
}

/// <summary>The name fields a user may edit about themselves.</summary>
/// <remarks>
/// No picture field, on purpose. It used to carry one, validated only as <c>[Url]</c> — which
/// accepted any address on any host, so a profile picture could be made to point anywhere, and
/// omitting the field (which the profile form does) cleared the avatar instead of leaving it. The
/// picture is set through the upload endpoints instead, where the API knows the image is one it
/// stored because it read the bytes back out of its own container.
/// </remarks>
public record UpdateProfileRequest(
    [Required] [MaxLength(50)] string FirstName,
    [Required] [MaxLength(50)] string LastName,
    [MaxLength(100)] string? DisplayName = null
);

/// <summary>Asks for a signed URL to upload one profile picture to.</summary>
/// <param name="ContentType">What the client says it is about to send. Verified again on commit.</param>
/// <param name="ByteSize">Size of the file, so an oversized upload is refused before it starts.</param>
public record AvatarUploadTicketRequest(
    [Required] string ContentType,
    [Range(1, long.MaxValue)] long ByteSize
);

/// <summary>Claims an uploaded blob as the caller's profile picture.</summary>
/// <param name="BlobPath">The path handed out with the upload ticket.</param>
public record SetProfilePictureRequest(
    [Required] [MaxLength(512)] string BlobPath
);