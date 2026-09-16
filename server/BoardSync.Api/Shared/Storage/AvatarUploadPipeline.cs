using BoardSync.Api.Shared.Auth.DTOs;
using Microsoft.Extensions.Options;

namespace BoardSync.Api.Shared.Storage;

/// <summary>The outcome of accepting an uploaded blob: the URL to store, or why it was refused.</summary>
/// <param name="Url">Where the image is served from, once verified. Null when it was refused.</param>
/// <param name="Error">Why it was refused, in words meant for the person who uploaded it.</param>
public record AcceptedAvatar(string? Url, string? Error)
{
    public bool Accepted => Url is not null;
}

/// <summary>
/// The two halves of a direct upload, shared by everything that has an avatar.
/// </summary>
/// <remarks>
/// <para>
/// A profile picture and an organization logo differ only in who may replace one and where the URL
/// is recorded. Everything between — which content types earn a signed URL, what the size ceiling
/// is, reading the bytes back to see what actually landed, deleting what fails — is identical, and
/// is here so there is one copy of it.
/// </para>
/// <para>
/// That is not tidiness. The verification below is the only thing standing between a signed upload
/// URL and an arbitrary file served from the app's own storage; a second copy of it is a second
/// thing that can quietly fall behind the first.
/// </para>
/// </remarks>
public class AvatarUploadPipeline
{
    private readonly IAvatarStorage _storage;
    private readonly StorageSettings _settings;
    private readonly ILogger<AvatarUploadPipeline> _logger;

    public AvatarUploadPipeline(
        IAvatarStorage storage,
        IOptions<StorageSettings> settings,
        ILogger<AvatarUploadPipeline> logger)
    {
        _storage = storage;
        _settings = settings.Value;
        _logger = logger;
    }

    public bool IsAvailable => _storage.IsConfigured;

    /// <summary>Validates the client's declared image and signs a URL for it.</summary>
    public async Task<ApiResponse<AvatarUploadTicketResponse>> CreateTicketAsync(
        AvatarOwner owner, AvatarUploadTicketRequest request, CancellationToken ct)
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
             * The path is built from the owner the caller was authorized as, never from the
             * request. That is the whole safety argument for handing a browser a write URL: the
             * token is scoped to one blob, and that blob is under a prefix only this owner's
             * tickets are ever issued for.
             */
            var ticket = await _storage.CreateUploadTicketAsync(owner, contentType, extension, ct);

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
            _logger.LogError(ex, "Could not issue an avatar upload URL for {Owner}", owner);

            return new ApiResponse<AvatarUploadTicketResponse>(
                false, "Could not start the upload. Try again.");
        }
    }

    /// <summary>
    /// Checks that <paramref name="blobPath"/> is this owner's, and that what landed there is
    /// really an image. Returns the URL to store, or the reason it was refused.
    /// </summary>
    /// <remarks>
    /// Everything that fails here is refused <i>and deleted</i>. A blob that failed verification is
    /// not a picture, and leaving it would mean the one place a caller can write to accumulates
    /// whatever they chose to put there — a small file drop, dressed as a settings page.
    /// </remarks>
    public async Task<AcceptedAvatar> AcceptAsync(
        AvatarOwner owner, string blobPath, CancellationToken ct)
    {
        /*
         * The client hands back the path it was given, so this is where the ticket's scope is
         * re-checked. Without it, a caller could commit any path in the container — including an
         * avatar belonging to someone else, which they were never given a URL for.
         */
        if (!AvatarUploads.BelongsTo(blobPath, owner))
        {
            _logger.LogWarning(
                "{Owner} tried to claim {BlobPath}, which is not theirs", owner, blobPath);

            return new AcceptedAvatar(null, "That upload does not belong to you.");
        }

        StoredBlob? uploaded;

        try
        {
            uploaded = await _storage.InspectAsync(blobPath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read back the avatar at {BlobPath}", blobPath);

            return new AcceptedAvatar(null, "Could not read the uploaded image.");
        }

        if (uploaded is null)
        {
            return new AcceptedAvatar(
                null, "That upload was not found. It may have expired before it finished.");
        }

        if (uploaded.ContentLength > _settings.MaxAvatarBytes)
        {
            await _storage.DeleteAsync(blobPath, ct);

            return new AcceptedAvatar(
                null,
                $"That image is {Megabytes(uploaded.ContentLength)}MB. The limit is "
                + $"{Megabytes(_settings.MaxAvatarBytes)}MB.");
        }

        // The bytes, not the label. The content type on the blob is whatever the client asked for;
        // the file's own header is the only thing here that the client did not simply assert.
        var actual = AvatarUploads.Sniff(uploaded.Head);

        if (actual is null)
        {
            await _storage.DeleteAsync(blobPath, ct);

            _logger.LogWarning(
                "{Owner} uploaded {BlobPath} declared as {Declared}, but its header is not an "
                + "image we accept", owner, blobPath, uploaded.ContentType);

            return new AcceptedAvatar(
                null, "That file is not an image we can use. Try a PNG, JPEG, WebP or GIF.");
        }

        _logger.LogInformation(
            "Avatar accepted for {Owner} ({ContentType}, {Bytes} bytes)",
            owner, actual, uploaded.ContentLength);

        return new AcceptedAvatar(_storage.ToPublicUrl(blobPath), null);
    }

    /// <summary>
    /// Deletes the image a stored URL points at, when it is one of ours.
    /// </summary>
    /// <remarks>
    /// Call this <i>after</i> the new URL is committed. Deleting first would, on a failed save,
    /// leave the owner pointing at a URL that 404s — a working avatar traded for a broken one. A
    /// URL that does not map back to our container is left alone entirely.
    /// </remarks>
    public async Task DeleteIfOursAsync(string? url, CancellationToken ct)
    {
        if (!IsAvailable) return;

        var path = _storage.ToBlobPath(url);

        if (path is not null)
            await _storage.DeleteAsync(path, ct);
    }

    /// <summary>Byte counts, in the unit the person reading the error thinks in.</summary>
    private static string Megabytes(long bytes) =>
        (bytes / 1024d / 1024d).ToString("0.#");
}
