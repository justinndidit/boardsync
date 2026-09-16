using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth.Repositories;
using BoardSync.Api.Shared.Storage;

namespace BoardSync.Api.Shared.Auth.Services.Implementations;

/// <inheritdoc cref="IAvatarService"/>
/// <remarks>
/// Thin on purpose. Validating the upload and verifying what landed is
/// <see cref="AvatarUploadPipeline"/>, shared with the organization logo; what is left here is the
/// part that is specific to a person — which record the URL is written to, and that the owner is
/// the token's subject.
/// </remarks>
public class AvatarService : IAvatarService
{
    private readonly IUserRepository _users;
    private readonly AvatarUploadPipeline _pipeline;
    private readonly ILogger<AvatarService> _logger;

    public AvatarService(
        IUserRepository users,
        AvatarUploadPipeline pipeline,
        ILogger<AvatarService> logger)
    {
        _users = users;
        _pipeline = pipeline;
        _logger = logger;
    }

    public bool IsAvailable => _pipeline.IsAvailable;

    public Task<ApiResponse<AvatarUploadTicketResponse>> CreateUploadTicketAsync(
        Guid userId, AvatarUploadTicketRequest request, CancellationToken ct) =>
        _pipeline.CreateTicketAsync(AvatarOwner.ForUser(userId), request, ct);

    public async Task<ApiResponse<UserProfile>> CommitAsync(
        Guid userId, SetProfilePictureRequest request, CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(userId, ct);

        if (user is null)
            return new ApiResponse<UserProfile>(false, "User not found");

        var accepted = await _pipeline.AcceptAsync(
            AvatarOwner.ForUser(userId), request.BlobPath, ct);

        if (!accepted.Accepted)
            return new ApiResponse<UserProfile>(false, accepted.Error!);

        var previous = UserProfileMapping.PictureOrNull(user.ProfilePictureUrl);

        user.ProfilePictureUrl = accepted.Url!;
        user.UpdatedAt = DateTime.UtcNow;

        await _users.SaveChangesAsync(ct);

        // Only after the profile is committed — see AvatarUploadPipeline.DeleteIfOursAsync.
        if (previous is not null && previous != accepted.Url)
            await _pipeline.DeleteIfOursAsync(previous, ct);

        _logger.LogInformation("Profile picture updated for {UserId}", userId);

        return new ApiResponse<UserProfile>(true, "Profile picture updated", user.ToProfile());
    }

    public async Task<ApiResponse<UserProfile>> RemoveAsync(Guid userId, CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(userId, ct);

        if (user is null)
            return new ApiResponse<UserProfile>(false, "User not found");

        var previous = UserProfileMapping.PictureOrNull(user.ProfilePictureUrl);

        user.ProfilePictureUrl = string.Empty;
        user.UpdatedAt = DateTime.UtcNow;

        await _users.SaveChangesAsync(ct);

        await _pipeline.DeleteIfOursAsync(previous, ct);

        return new ApiResponse<UserProfile>(true, "Profile picture removed", user.ToProfile());
    }
}
