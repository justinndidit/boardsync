namespace BoardSync.Api.Shared.Storage;

/// <summary>A signed, single-blob write capability handed to the browser.</summary>
/// <param name="BlobPath">Where the upload will land. The client hands this back to commit it.</param>
/// <param name="UploadUrl">The URL to PUT the bytes to, SAS token included.</param>
/// <param name="Headers">Headers the PUT must carry for the storage service to accept it.</param>
/// <param name="ExpiresAt">When <paramref name="UploadUrl"/> stops working.</param>
public record AvatarUploadTicket(
    string BlobPath,
    string UploadUrl,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset ExpiresAt);

/// <summary>What actually landed in the container, as read back from it.</summary>
/// <param name="ContentType">The content type recorded on the blob — a client claim, not evidence.</param>
/// <param name="ContentLength">Size in bytes.</param>
/// <param name="Head">The first <see cref="AvatarUploads.SniffLength"/> bytes, for format detection.</param>
public record StoredBlob(string? ContentType, long ContentLength, byte[] Head);

/// <summary>
/// The blob container profile pictures live in.
/// </summary>
/// <remarks>
/// Deliberately narrow. This is not a general file-storage abstraction — it knows about avatars,
/// because the paths, the container's public-read setting and the short SAS lifetime are all
/// choices that only make sense for small public images. A second kind of upload should get its own
/// interface rather than widen this one.
/// </remarks>
public interface IAvatarStorage
{
    /// <summary>Whether storage is configured at all. When false every other member throws.</summary>
    bool IsConfigured { get; }

    /// <summary>Signs a URL that may write exactly one blob, for a short time.</summary>
    Task<AvatarUploadTicket> CreateUploadTicketAsync(
        Guid userId, string contentType, string extension, CancellationToken ct);

    /// <summary>Reads back what is at <paramref name="blobPath"/>, or <c>null</c> if nothing is.</summary>
    Task<StoredBlob?> InspectAsync(string blobPath, CancellationToken ct);

    /// <summary>Removes a blob. Succeeds whether or not it was there.</summary>
    Task DeleteAsync(string blobPath, CancellationToken ct);

    /// <summary>The URL an avatar is served from, which is what gets stored on the user.</summary>
    string ToPublicUrl(string blobPath);

    /// <summary>
    /// The reverse of <see cref="ToPublicUrl"/>, or <c>null</c> when the URL is not one of ours.
    /// </summary>
    /// <remarks>
    /// Needed to delete the picture being replaced. A URL that does not map back — an avatar set
    /// before this container existed, or against a different account — simply stops being
    /// referenced; nothing outside our own container is ever deleted on a user's behalf.
    /// </remarks>
    string? ToBlobPath(string? publicUrl);
}
