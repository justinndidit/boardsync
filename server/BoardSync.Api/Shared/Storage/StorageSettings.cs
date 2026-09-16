namespace BoardSync.Api.Shared.Storage;

/// <summary>
/// Where uploaded images live, and what the API is willing to sign an upload URL for.
/// </summary>
/// <remarks>
/// Bound from the <c>Storage</c> configuration section. Absent a connection string the whole
/// feature reports itself unconfigured and the endpoints answer 503 — the same shape the
/// Intelligence module uses for a missing API key, and for the same reason: a half-wired
/// integration should say so rather than fail somewhere further in.
/// </remarks>
public class StorageSettings
{
    /// <summary>
    /// Azure Storage connection string. Must carry an account key — the upload URLs are
    /// user-delegation-free service SAS tokens, which can only be signed with a shared key.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Container the avatars are written to. Created on first use with public blob read.</summary>
    public string Container { get; set; } = "avatars";

    /// <summary>
    /// Origin to build stored URLs from, when the blobs are served through something other than the
    /// storage account directly — a CDN, or a reverse proxy. No trailing slash.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// How long a signed upload URL stays valid. Short on purpose: it is a write capability handed
    /// to a browser, and the only thing it has to outlive is one file transfer.
    /// </summary>
    public int UploadUrlExpiryMinutes { get; set; } = 10;

    /// <summary>Largest avatar accepted, in bytes. Checked when the ticket is issued and again on commit.</summary>
    public long MaxAvatarBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// Whether to write CORS rules onto the storage account on first use.
    /// </summary>
    /// <remarks>
    /// The browser PUTs straight to the blob endpoint, which is cross-origin, so without CORS rules
    /// on the account every upload fails the preflight. Convenient in development against Azurite,
    /// which starts with no rules at all. In a real deployment the account is usually configured
    /// once out of band and this stays off, because it lets the application rewrite account-wide
    /// settings.
    /// </remarks>
    public bool ConfigureCors { get; set; }

    /// <summary>
    /// Origins allowed to upload straight to the account. Filled from the application's own
    /// <c>AllowedOrigins</c> when left empty, because the only browser that uploads is the one
    /// already allowed to call the API.
    /// </summary>
    public string[] CorsOrigins { get; set; } = [];
}
