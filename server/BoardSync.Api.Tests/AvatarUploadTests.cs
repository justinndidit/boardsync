using System.Text;
using BoardSync.Api.Shared.Storage;

namespace BoardSync.Api.Tests;

/// <summary>
/// The rules that decide what may become a profile picture.
/// </summary>
/// <remarks>
/// These matter more than they look like they should. The browser uploads straight to blob storage
/// with a URL the API signed, so between issuing that URL and the client calling back, the server
/// sees nothing — every guarantee about what is in the container comes from here.
/// </remarks>
public class AvatarUploadTests
{
    // Real leading bytes. Each is the file's own signature, which is what the commit step reads.
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static readonly byte[] Gif = Ascii("GIF89a");

    // RIFF, four bytes of length, then WEBP. The length varies, which is why it is skipped.
    private static readonly byte[] Webp = Riff("WEBPVP8 ");

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Riff(string body) =>
        [.. Ascii("RIFF"), 0x24, 0x01, 0x00, 0x00, .. Ascii(body)];

    // --- What may be uploaded -------------------------------------------------

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/webp", ".webp")]
    [InlineData("image/gif", ".gif")]
    [InlineData("IMAGE/PNG", ".png")]
    [InlineData("image/jpeg; charset=binary", ".jpg")]
    [InlineData("  image/png  ", ".png")]
    public void AnImageTypeYieldsItsExtension(string declared, string expected)
    {
        Assert.True(AvatarUploads.TryNormalizeContentType(declared, out var type, out var extension));

        Assert.Equal(declared.Split(';')[0].Trim().ToLowerInvariant(), type);
        Assert.Equal(expected, extension);
    }

    /// <summary>
    /// SVG is refused, and not because it is an unusual choice of avatar.
    /// </summary>
    /// <remarks>
    /// An SVG is a document that can carry script. Serving one from storage the app links to, as an
    /// image every teammate's browser loads, is a stored-XSS shape rather than a picture — so it is
    /// excluded by the allowlist rather than sanitised.
    /// </remarks>
    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    [InlineData("application/pdf")]
    [InlineData("image/")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsRefused(string? declared) =>
        Assert.False(AvatarUploads.TryNormalizeContentType(declared, out _, out _));

    // --- What actually landed -------------------------------------------------

    [Fact]
    public void EachFormatIsRecognisedFromItsOwnHeader()
    {
        Assert.Equal("image/png", AvatarUploads.Sniff(Png));
        Assert.Equal("image/jpeg", AvatarUploads.Sniff(Jpeg));
        Assert.Equal("image/gif", AvatarUploads.Sniff(Gif));
        Assert.Equal("image/webp", AvatarUploads.Sniff(Webp));
    }

    /// <summary>
    /// The check the direct-upload design exists to keep.
    /// </summary>
    /// <remarks>
    /// A SAS constrains who may write and for how long, but not what. The declared content type is
    /// a claim by the client and is written onto the blob verbatim, so the only thing standing
    /// between "I am sending a PNG" and an HTML document served from the app's own storage is that
    /// the commit step reads the bytes rather than the label.
    /// </remarks>
    [Theory]
    [InlineData("<html><script>alert(1)</script>")]
    [InlineData("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\">")]
    [InlineData("%PDF-1.7")]
    [InlineData("MZ ")]
    public void SomethingThatIsNotAnImageIsNotAnImage(string content) =>
        Assert.Null(AvatarUploads.Sniff(Ascii(content)));

    /// <summary>A truncated upload identifies as nothing rather than as whatever it starts like.</summary>
    [Fact]
    public void TooFewBytesToTellIsNotAGuess()
    {
        Assert.Null(AvatarUploads.Sniff([]));
        Assert.Null(AvatarUploads.Sniff([0x89, 0x50]));

        // RIFF, but cut off before the part that would say WEBP.
        Assert.Null(AvatarUploads.Sniff(Ascii("RIFF$")));
    }

    /// <summary>A RIFF container that is not a WebP — a WAV, say — is not an image.</summary>
    [Fact]
    public void ARiffContainerAloneIsNotAWebp() =>
        Assert.Null(AvatarUploads.Sniff(Riff("WAVEfmt ")));

    // --- Whose upload it is ---------------------------------------------------

    /// <summary>
    /// The path is derived from the owner, which is the whole safety argument for a signed URL.
    /// </summary>
    [Fact]
    public void APathIsPrefixedByItsOwner()
    {
        var user = AvatarOwner.ForUser(Guid.NewGuid());

        Assert.StartsWith($"users/{user.Id:D}/", AvatarUploads.PathFor(user, ".png"));
        Assert.EndsWith(".png", AvatarUploads.PathFor(user, ".png"));

        var org = AvatarOwner.ForOrganization(Guid.NewGuid());

        Assert.StartsWith(
            $"organizations/{org.Id:D}/", AvatarUploads.PathFor(org, ".png"));
    }

    /// <summary>
    /// A person and an organization never share a prefix, even with the same id.
    /// </summary>
    /// <remarks>
    /// Guids do not collide in practice, but the kind segment means the argument does not have to
    /// rest on that: an <c>org:admin</c> ticket cannot address a user's avatar however the ids
    /// fall, because the two namespaces do not overlap at all.
    /// </remarks>
    [Fact]
    public void AUserAndAnOrganizationCannotShareAPath()
    {
        var id = Guid.NewGuid();

        var user = AvatarOwner.ForUser(id);
        var org = AvatarOwner.ForOrganization(id);

        Assert.NotEqual(user.Prefix, org.Prefix);

        Assert.False(
            AvatarUploads.BelongsTo(AvatarUploads.PathFor(org, ".png"), user));

        Assert.False(
            AvatarUploads.BelongsTo(AvatarUploads.PathFor(user, ".png"), org));
    }

    /// <summary>
    /// Replacing a picture is a new path, never the same one with new content.
    /// </summary>
    /// <remarks>
    /// The URL is public, cacheable and long-lived by design. Reusing the path would mean a browser
    /// or CDN holding the previous image could keep showing it after the user changed it — the
    /// change appearing to fail rather than to take effect.
    /// </remarks>
    [Fact]
    public void TwoUploadsForOneOwnerNeverCollide()
    {
        var owner = AvatarOwner.ForUser(Guid.NewGuid());

        Assert.NotEqual(
            AvatarUploads.PathFor(owner, ".png"),
            AvatarUploads.PathFor(owner, ".png"));
    }

    /// <summary>
    /// The commit-time check that a caller is claiming the blob they were given.
    /// </summary>
    /// <remarks>
    /// Without it, a caller could commit any path in the container — including a blob uploaded by
    /// somebody else, which they were never issued a URL for.
    /// </remarks>
    [Fact]
    public void OnlyTheOwnerMayClaimAPath()
    {
        var owner = AvatarOwner.ForUser(Guid.NewGuid());
        var other = AvatarOwner.ForUser(Guid.NewGuid());

        var path = AvatarUploads.PathFor(owner, ".png");

        Assert.True(AvatarUploads.BelongsTo(path, owner));
        Assert.False(AvatarUploads.BelongsTo(path, other));
    }

    /// <summary>The same check, for a logo: one OrgAdmin cannot claim another org's upload.</summary>
    [Fact]
    public void OneOrganizationMayNotClaimAnothersUpload()
    {
        var owner = AvatarOwner.ForOrganization(Guid.NewGuid());
        var other = AvatarOwner.ForOrganization(Guid.NewGuid());

        Assert.False(
            AvatarUploads.BelongsTo(AvatarUploads.PathFor(owner, ".png"), other));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("someone-elses.png")]
    [InlineData("../secrets.png")]
    public void APathThatIsNotOursBelongsToNobody(string? path) =>
        Assert.False(
            AvatarUploads.BelongsTo(path, AvatarOwner.ForUser(Guid.NewGuid())));

    /// <summary>
    /// A prefix that merely starts with the id is not the id.
    /// </summary>
    /// <remarks>
    /// The separator is part of the comparison. Matching on the GUID alone would accept a sibling
    /// path whose first segment happens to begin with it.
    /// </remarks>
    [Fact]
    public void ThePrefixMustBeAWholeSegment()
    {
        var owner = AvatarOwner.ForUser(Guid.NewGuid());

        Assert.False(
            AvatarUploads.BelongsTo($"{owner.Prefix}-other/file.png", owner));
    }
}
