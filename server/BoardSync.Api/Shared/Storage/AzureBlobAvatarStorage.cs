using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;

namespace BoardSync.Api.Shared.Storage;

/// <summary>
/// Avatars in an Azure Blob container — Azurite in development, a storage account in production.
/// </summary>
/// <remarks>
/// <para>
/// The API never carries the image bytes. It signs a URL good for one blob and a few minutes, the
/// browser PUTs to that, and the client calls back with the path so the API can check what landed
/// and record it. What that buys is that a 4MB upload does not occupy a request thread, a Kestrel
/// buffer and an ingress hop on its way to the same place it was always going.
/// </para>
/// <para>
/// What it costs is that the account key must be able to sign a service SAS, which is why the
/// connection string has to carry a key rather than being a managed-identity endpoint. If this
/// moves to managed identity, the ticket becomes a user-delegation SAS and the account key goes
/// away; nothing above this interface changes.
/// </para>
/// </remarks>
public class AzureBlobAvatarStorage : IAvatarStorage
{
    private readonly StorageSettings _settings;
    private readonly ILogger<AzureBlobAvatarStorage> _logger;
    private readonly BlobContainerClient? _container;

    /// <summary>Guards the one-time container and CORS setup below.</summary>
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public AzureBlobAvatarStorage(
        IOptions<StorageSettings> settings,
        ILogger<AzureBlobAvatarStorage> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_settings.ConnectionString))
        {
            _logger.LogInformation(
                "Storage:ConnectionString is not set. Profile picture uploads are disabled; "
                + "everything else, including avatars set before now, works unchanged.");

            return;
        }

        var service = new BlobServiceClient(_settings.ConnectionString);
        _container = service.GetBlobContainerClient(_settings.Container);
    }

    public bool IsConfigured => _container is not null;

    /// <summary>The container client, or an explanation of why there isn't one.</summary>
    private BlobContainerClient Container =>
        _container ?? throw new InvalidOperationException(
            "Avatar storage is not configured. Set Storage:ConnectionString.");

    public async Task<AvatarUploadTicket> CreateUploadTicketAsync(
        Guid userId, string contentType, string extension, CancellationToken ct)
    {
        await EnsureContainerAsync(ct);

        var blobPath = AvatarUploads.PathFor(userId, extension);
        var blob = Container.GetBlobClient(blobPath);

        if (!blob.CanGenerateSasUri)
        {
            throw new InvalidOperationException(
                "Storage:ConnectionString cannot sign upload URLs. It must include an account key "
                + "(AccountKey=...), not only a SAS or a bare endpoint.");
        }

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_settings.UploadUrlExpiryMinutes);

        /*
         * Create and Write, on this blob and no other. Not Delete — the browser has no reason to
         * remove anything — and not container-scoped, which would make one ticket a key to every
         * avatar in the account.
         *
         * The clock skew allowance is why StartsOn is in the past: a client whose clock runs a
         * minute ahead of the storage service would otherwise be handed a URL that is not valid
         * yet, and see an opaque 403.
         */
        var builder = new BlobSasBuilder
        {
            BlobContainerName = Container.Name,
            BlobName = blobPath,
            Resource = "b",
            StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresOn = expiresAt,
        };

        builder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

        var uploadUrl = blob.GenerateSasUri(builder);

        /*
         * A SAS constrains who may write and for how long, but it cannot constrain what. These
         * headers are the storage service's requirements for a block-blob PUT plus the metadata
         * that makes the result render as a picture rather than download as a file — they are
         * instructions to the client, not a guarantee about the bytes. The guarantee is made on
         * commit, by reading the file's own header back out of the container.
         *
         * The blob path carries a fresh GUID per upload, so its content never changes once
         * written and it can be cached for as long as anything is willing to keep it.
         */
        var headers = new Dictionary<string, string>
        {
            ["x-ms-blob-type"] = "BlockBlob",
            ["x-ms-blob-content-type"] = contentType,
            ["x-ms-blob-cache-control"] = "public, max-age=31536000, immutable",
        };

        return new AvatarUploadTicket(blobPath, uploadUrl.ToString(), headers, expiresAt);
    }

    public async Task<StoredBlob?> InspectAsync(string blobPath, CancellationToken ct)
    {
        var blob = Container.GetBlobClient(blobPath);

        try
        {
            var properties = await blob.GetPropertiesAsync(cancellationToken: ct);

            /*
             * Only the first few bytes are pulled down, not the file. That is enough to identify
             * the format and it means a caller cannot make the API stream an arbitrarily large
             * blob by pointing a commit at one.
             */
            var head = Array.Empty<byte>();

            if (properties.Value.ContentLength > 0)
            {
                var range = new HttpRange(
                    0, Math.Min(AvatarUploads.SniffLength, properties.Value.ContentLength));

                var download = await blob.DownloadStreamingAsync(
                    new BlobDownloadOptions { Range = range }, ct);

                using var buffer = new MemoryStream();
                await download.Value.Content.CopyToAsync(buffer, ct);

                head = buffer.ToArray();
            }

            return new StoredBlob(
                properties.Value.ContentType, properties.Value.ContentLength, head);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string blobPath, CancellationToken ct)
    {
        try
        {
            await Container.GetBlobClient(blobPath).DeleteIfExistsAsync(cancellationToken: ct);
        }
        catch (RequestFailedException ex)
        {
            /*
             * Swallowed on purpose. Deletes here are always of a picture that has already been
             * replaced or cleared on the user, so the profile is correct either way and the worst
             * outcome is an orphaned blob. Failing the caller's request over it would undo a
             * change that did succeed.
             */
            _logger.LogWarning(
                ex, "Could not delete the replaced avatar at {BlobPath}", blobPath);
        }
    }

    public string ToPublicUrl(string blobPath) => $"{PublicBase()}/{blobPath}";

    public string? ToBlobPath(string? publicUrl)
    {
        if (string.IsNullOrWhiteSpace(publicUrl) || !IsConfigured)
            return null;

        // Both spellings, because a URL stored before PublicBaseUrl was introduced — or before it
        // changed — still points at the account directly and is still ours to delete.
        foreach (var prefix in new[] { PublicBase(), Container.Uri.ToString().TrimEnd('/') })
        {
            if (!publicUrl.StartsWith($"{prefix}/", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = publicUrl[(prefix.Length + 1)..];

            // Query strings appear on URLs from a read-SAS era; the path is what identifies a blob.
            var queryStart = path.IndexOf('?');

            return queryStart < 0 ? path : path[..queryStart];
        }

        return null;
    }

    private string PublicBase() =>
        string.IsNullOrWhiteSpace(_settings.PublicBaseUrl)
            ? Container.Uri.ToString().TrimEnd('/')
            : _settings.PublicBaseUrl.TrimEnd('/');

    /// <summary>
    /// Creates the container, and optionally the CORS rules, once per process.
    /// </summary>
    /// <remarks>
    /// Done lazily rather than at startup so that unreachable storage costs one failed upload
    /// rather than an application that will not boot — the rest of the product does not depend on
    /// this, and neither should its ability to start.
    /// </remarks>
    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);

        try
        {
            if (_initialized) return;

            /*
             * Public blob read, not private. Avatars are shown in an <img> by every client that can
             * see the member list, and the alternative — a read SAS minted per view — would put a
             * short expiry on a URL that gets cached, stored in a rendered page, and read again
             * later. Nothing here is a secret; the container holds pictures people chose to publish
             * to their own team, and the path is an unguessable GUID either way.
             */
            await Container.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: ct);

            if (_settings.ConfigureCors)
                await ConfigureCorsAsync(ct);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Lets the app's own origins PUT to the account.
    /// </summary>
    /// <remarks>
    /// Direct upload is a cross-origin request, so without rules on the account the browser's
    /// preflight fails and no upload ever reaches storage. Azurite starts with none, which is what
    /// makes this worth automating in development. It is account-wide state, which is what makes it
    /// opt-in everywhere else.
    /// </remarks>
    private async Task ConfigureCorsAsync(CancellationToken ct)
    {
        if (_settings.CorsOrigins.Length == 0)
        {
            _logger.LogWarning(
                "Storage:ConfigureCors is on but no origins are configured, so no rules were "
                + "written. Uploads from a browser will fail preflight.");

            return;
        }

        try
        {
            var service = new BlobServiceClient(_settings.ConnectionString);
            var properties = await service.GetPropertiesAsync(ct);

            properties.Value.Cors.Clear();

            properties.Value.Cors.Add(new BlobCorsRule
            {
                AllowedOrigins = string.Join(",", _settings.CorsOrigins),
                AllowedMethods = "PUT,OPTIONS",
                AllowedHeaders = "x-ms-blob-type,x-ms-blob-content-type,x-ms-blob-cache-control,content-type",
                ExposedHeaders = "",
                MaxAgeInSeconds = 3600,
            });

            await service.SetPropertiesAsync(properties.Value, ct);

            _logger.LogInformation(
                "Storage CORS configured for {Origins}", string.Join(", ", _settings.CorsOrigins));
        }
        catch (RequestFailedException ex)
        {
            // Not fatal: an account configured out of band already has the rules, and this call
            // needs a permission a scoped credential may not have been given.
            _logger.LogWarning(ex, "Could not write CORS rules to the storage account.");
        }
    }
}
