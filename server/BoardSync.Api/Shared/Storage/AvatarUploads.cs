using System.Diagnostics.CodeAnalysis;

namespace BoardSync.Api.Shared.Storage;

/// <summary>
/// What counts as an avatar, decided without touching the network.
/// </summary>
/// <remarks>
/// <para>
/// The browser uploads straight to blob storage, so between issuing the ticket and the client
/// calling back there is a window in which the API sees nothing. That is the accepted cost of not
/// streaming every image through the request pipeline, and it is why validation happens twice:
/// once on the declared content type before a URL is signed, and once on the bytes that actually
/// landed before the URL is stored on the user.
/// </para>
/// <para>
/// The second check is the one that matters. A declared <c>image/png</c> is a claim by the client;
/// <see cref="Sniff"/> reads the file's own header and answers what it really is. An upload whose
/// header says something else never becomes a profile picture, and the blob is deleted.
/// </para>
/// </remarks>
public static class AvatarUploads
{
    /// <summary>
    /// Formats an avatar may be in.
    /// </summary>
    /// <remarks>
    /// Raster formats a browser renders in an <c>&lt;img&gt;</c> without help. SVG is deliberately
    /// absent: it is a document that can carry script, and serving one from the same origin family
    /// as the app is a stored-XSS shape rather than a picture.
    /// </remarks>
    private static readonly Dictionary<string, string> ExtensionByContentType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = ".png",
            ["image/jpeg"] = ".jpg",
            ["image/webp"] = ".webp",
            ["image/gif"] = ".gif",
        };

    /// <summary>Content types an upload ticket may be issued for.</summary>
    public static IReadOnlyCollection<string> AllowedContentTypes => ExtensionByContentType.Keys;

    /// <summary>Bytes of the file <see cref="Sniff"/> needs to identify it.</summary>
    public const int SniffLength = 16;

    /// <summary>
    /// Normalises a client-declared content type, and yields the extension to store it under.
    /// </summary>
    /// <remarks>
    /// A browser sends <c>image/jpeg</c> for a JPEG, but some send parameters along with it
    /// (<c>image/jpeg; charset=binary</c>), so the parameter list is trimmed before matching.
    /// </remarks>
    public static bool TryNormalizeContentType(
        string? declared,
        [NotNullWhen(true)] out string? contentType,
        [NotNullWhen(true)] out string? extension)
    {
        contentType = null;
        extension = null;

        if (string.IsNullOrWhiteSpace(declared))
            return false;

        var bare = declared.Split(';')[0].Trim().ToLowerInvariant();

        if (!ExtensionByContentType.TryGetValue(bare, out var ext))
            return false;

        contentType = bare;
        extension = ext;

        return true;
    }

    /// <summary>
    /// Identifies an image from its leading bytes, or returns <c>null</c> if it is not one we accept.
    /// </summary>
    /// <remarks>
    /// Signatures are checked rather than guessed at: PNG and GIF have fixed prefixes, JPEG starts
    /// with the SOI marker, and WebP is a RIFF container whose fourth word is <c>WEBP</c> — so the
    /// bytes between the two are skipped, because they are the file length and vary.
    /// </remarks>
    public static string? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 &&
            head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47 &&
            head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
            return "image/png";

        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return "image/jpeg";

        if (head.Length >= 6 &&
            head[0] == 'G' && head[1] == 'I' && head[2] == 'F' && head[3] == '8' &&
            (head[4] == '7' || head[4] == '9') && head[5] == 'a')
            return "image/gif";

        if (head.Length >= 12 &&
            head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' &&
            head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P')
            return "image/webp";

        return null;
    }

    /// <summary>
    /// The path an avatar for <paramref name="owner"/> is written to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prefixed by the owner, which is what makes a signed upload URL safe to hand out: the token
    /// is scoped to this one blob, and the blob is under a prefix nobody else writes to. The server
    /// builds this path from the authorized subject and never from anything the client sent, so a
    /// caller cannot aim an upload at somebody else's avatar.
    /// </para>
    /// <para>
    /// The random segment means a replaced avatar is a new URL rather than the same one with new
    /// content, so a cached copy in a browser or a CDN can never show the old picture.
    /// </para>
    /// </remarks>
    public static string PathFor(AvatarOwner owner, string extension) =>
        $"{owner.Prefix}/{Guid.NewGuid():N}{extension}";

    /// <summary>Whether <paramref name="path"/> is an avatar path belonging to <paramref name="owner"/>.</summary>
    /// <remarks>
    /// Checked on commit. The client hands back the path it was given, and this confirms it is
    /// still the path we issued for them — an unchecked one would let a caller point their profile
    /// at any blob in the container, including someone else's.
    /// </remarks>
    public static bool BelongsTo(string? path, AvatarOwner owner) =>
        path is not null &&
        path.StartsWith($"{owner.Prefix}/", StringComparison.OrdinalIgnoreCase);
}
