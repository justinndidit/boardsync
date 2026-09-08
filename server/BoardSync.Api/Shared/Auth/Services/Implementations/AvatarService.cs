using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth.Repositories;
using BoardSync.Api.Shared.Storage;
using Microsoft.Extensions.Options;

namespace BoardSync.Api.Shared.Auth.Services.Implementations;

/// <inheritdoc cref="IAvatarService"/>
public class AvatarService : IAvatarService
{
    private readonly IUserRepository _users;
    private readonly IAvatarStorage _storage;
    private readonly StorageSettings _settings;
    private readonly ILogger<AvatarService> _logger;

    public AvatarService(
        IUserRepository users,
        IAvatarStorage storage,
        IOptions<StorageSettings> settings,
        ILogger<AvatarService> logger)
    {
        _users = users;
        _storage = storage;
        _settings = settings.Value;
        _logger = logger;
    }

    public bool IsAvailable => _storage.IsConfigured;

    public async Task<ApiResponse<AvatarUploadTicketResponse>> CreateUploadTicketAsync(
        Guid userId, AvatarUploadTicketRequest request, CancellationToken ct)
    {
        if (!AvatarUploads.TryNormalizeContentType(
                request.ContentType, out var contentType, out var extension))
        {
            return new ApiResponse<AvatarUploadTicketResponse>(
                false,
                $"{request.ContentType} is not an image type we accept. Use one of: "
                + string.Join(", ", AvatarUploads.AllowedContentTypes) + ".");
        }

        if (request.ByteSize > _settings.MaxAvatarBytes)
        {
            return new ApiResponse<AvatarUploadTicketResponse>(
                false,
                $"That image is {Megabytes(request.ByteSize)}MB. The limit is "
                + $"{Megabytes(_settings.MaxAvatarBytes)}MB.");
        }

        try
        {
            /*
             * The path is built from the token's subject, never from the request. That is the whole
             * safety argument for handing a browser a write URL: the token is scoped to one blob,
             * and that blob is under a prefix only this user's tickets are ever issued for.
             */
            var ticket = await _storage.CreateUploadTicketAsync(userId, contentType, extension, ct);

            return new ApiResponse<AvatarUploadTicketResponse>(
                true,
                "Upload URL issued",
                new AvatarUploadTicketResponse(
                    ticket.BlobPath,
                    ticket.UploadUrl,
                    ticket.Headers,
                    ticket.ExpiresAt,
                    _settings.MaxAvatarBytes));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not issue an avatar upload URL for {UserId}", userId);

            return new ApiResponse<AvatarUploadTicketResponse>(
                false, "Could not start the upload. Try again.");
        }
    }

    public async Task<ApiResponse<UserProfile>> CommitAsync(
        Guid userId, SetProfilePictureRequest request, CancellationToken ct)
    {
        /*
         * The client hands back the path it was given, so this is where the ticket's scope is
         * re-checked. Without it, a caller could commit any path in the container — including
         * another user's avatar, which is a stored blob they were never given a URL for.
         */
        if (!AvatarUploads.BelongsTo(request.BlobPath, userId))
        {
            _logger.LogWarning(
                "User {UserId} tried to claim {BlobPath}, which is not theirs",
                userId, request.BlobPath);

            return new ApiResponse<UserProfile>(false, "That upload does not belong to you.");
        }

        var user = await _users.GetByIdAsync(userId);

        if (user is null)
            return new ApiResponse<UserProfile>(false, "User not found");

        StoredBlob? uploaded;

        try
        {
            uploaded = await _storage.InspectAsync(request.BlobPath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read back the avatar at {BlobPath}", request.BlobPath);

            return new ApiResponse<UserProfile>(false, "Could not read the uploaded image.");
        }

        if (uploaded is null)
        {
            return new ApiResponse<UserProfile>(
                false, "That upload was not found. It may have expired before it finished.");
        }

        /*
         * Everything below rejects *and deletes*. A blob that failed verification is not a picture,
         * and leaving it in the container would mean the one place a user can write to accumulates
         * whatever they chose to put there — a small file drop, dressed as a profile page.
         */
        if (uploaded.ContentLength > _settings.MaxAvatarBytes)
        {
            await _storage.DeleteAsync(request.BlobPath, ct);

            return new ApiResponse<UserProfile>(
                false,
                $"That image is {Megabytes(uploaded.ContentLength)}MB. The limit is "
                + $"{Megabytes(_settings.MaxAvatarBytes)}MB.");
        }

        // The bytes, not the label. The content type on the blob is whatever the client asked for;
        // the file's own header is the only thing here that the client did not simply assert.
        var actual = AvatarUploads.Sniff(uploaded.Head);

        if (actual is null)
        {
            await _storage.DeleteAsync(request.BlobPath, ct);

            _logger.LogWarning(
                "User {UserId} uploaded {BlobPath} declared as {Declared}, but its header is not "
                + "an image we accept", userId, request.BlobPath, uploaded.ContentType);

            return new ApiResponse<UserProfile>(
                false, "That file is not an image we can use. Try a PNG, JPEG, WebP or GIF.");
        }

        var previous = _storage.ToBlobPath(UserProfileMapping.PictureOrNull(user.ProfilePictureUrl));

        user.ProfilePictureUrl = _storage.ToPublicUrl(request.BlobPath);
        user.UpdatedAt = DateTime.UtcNow;

        await _users.SaveChangesAsync(ct);

        /*
         * Only after the profile is committed. Deleting the old blob first would, on a failed save,
         * leave the user pointing at a URL that 404s — a working avatar traded for a broken one.
         */
        if (previous is not null && previous != request.BlobPath)
            await _storage.DeleteAsync(previous, ct);

        _logger.LogInformation(
            "Profile picture updated for {UserId} ({ContentType}, {Bytes} bytes)",
            userId, actual, uploaded.ContentLength);

        return new ApiResponse<UserProfile>(true, "Profile picture updated", user.ToProfile());
    }

    public async Task<ApiResponse<UserProfile>> RemoveAsync(Guid userId, CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(userId);

        if (user is null)
            return new ApiResponse<UserProfile>(false, "User not found");

        var previous = _storage.IsConfigured
            ? _storage.ToBlobPath(UserProfileMapping.PictureOrNull(user.ProfilePictureUrl))
            : null;

        user.ProfilePictureUrl = string.Empty;
        user.UpdatedAt = DateTime.UtcNow;

        await _users.SaveChangesAsync(ct);

        if (previous is not null)
            await _storage.DeleteAsync(previous, ct);

        return new ApiResponse<UserProfile>(true, "Profile picture removed", user.ToProfile());
    }

    /// <summary>Byte counts, in the unit the person reading the error thinks in.</summary>
    private static string Megabytes(long bytes) =>
        (bytes / 1024d / 1024d).ToString("0.#");
}
