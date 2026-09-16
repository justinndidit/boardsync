using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Shared.Auth.DTOs;

namespace BoardSync.Api.Modules.OrgProject.Services.Interfaces;

/// <summary>
/// Setting and clearing an organization's logo.
/// </summary>
/// <remarks>
/// The same two-step upload a profile picture uses — the API signs a short-lived URL for one blob,
/// the browser uploads to it, and the API verifies what landed before recording it — differing only
/// in who is allowed to do it. A profile picture is authorized by being the token's subject; a logo
/// is authorized by holding <c>org:admin</c>, which the controller checks before anything here runs.
/// </remarks>
public interface IOrganizationAvatarService
{
    /// <summary>Whether storage is wired up. False means the endpoints answer 503.</summary>
    bool IsAvailable { get; }

    /// <summary>Signs a URL one logo may be uploaded to.</summary>
    Task<ApiResponse<AvatarUploadTicketResponse>> CreateUploadTicketAsync(
        Guid orgId, AvatarUploadTicketRequest request, CancellationToken ct);

    /// <summary>Verifies an uploaded blob and makes it the organization's logo.</summary>
    Task<ApiResponse<OrganizationResponse>> CommitAsync(
        Guid orgId, SetProfilePictureRequest request, Guid actingUserId, CancellationToken ct);

    /// <summary>Clears the logo and deletes the blob behind it.</summary>
    Task<ApiResponse<OrganizationResponse>> RemoveAsync(
        Guid orgId, Guid actingUserId, CancellationToken ct);
}
