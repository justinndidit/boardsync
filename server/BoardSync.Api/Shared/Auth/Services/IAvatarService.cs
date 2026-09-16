using BoardSync.Api.Shared.Auth.DTOs;

namespace BoardSync.Api.Shared.Auth.Services;

/// <summary>
/// Setting and clearing a user's own profile picture.
/// </summary>
/// <remarks>
/// Two steps, because the bytes never pass through the API: <see cref="CreateUploadTicketAsync"/>
/// signs a short-lived URL for one blob, the browser uploads to it, and
/// <see cref="CommitAsync"/> checks what landed before the URL is written to the user. A ticket
/// that is never committed leaves an orphan in the container and nothing on the profile.
/// </remarks>
public interface IAvatarService
{
    /// <summary>Whether storage is wired up. False means the endpoints answer 503.</summary>
    bool IsAvailable { get; }

    /// <summary>Signs a URL the caller may upload one avatar to.</summary>
    Task<ApiResponse<AvatarUploadTicketResponse>> CreateUploadTicketAsync(
        Guid userId, AvatarUploadTicketRequest request, CancellationToken ct);

    /// <summary>
    /// Verifies an uploaded blob and makes it the caller's picture, replacing whatever was there.
    /// </summary>
    Task<ApiResponse<UserProfile>> CommitAsync(
        Guid userId, SetProfilePictureRequest request, CancellationToken ct);

    /// <summary>Clears the caller's picture and deletes the blob behind it.</summary>
    Task<ApiResponse<UserProfile>> RemoveAsync(Guid userId, CancellationToken ct);
}
