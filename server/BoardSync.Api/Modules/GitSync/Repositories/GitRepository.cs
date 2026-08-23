using System.Security.Cryptography;
using BoardSync.Api.Data;
using BoardSync.Api.Modules.GitSync.Models;
using BoardSync.Api.Modules.GitSync.Providers;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.GitSync.Repositories;

/// <summary>Data access for the GitSync module — the <c>git</c> schema.</summary>
public interface IGitRepository
{
    /// <summary>
    /// The installation a webhook URL's token identifies, or null.
    /// </summary>
    /// <remarks>
    /// Looked up by the token rather than by trying each installation's secret, so the cost does not
    /// grow with the number of connected organizations and no timing signal distinguishes "many
    /// installations" from "few".
    /// </remarks>
    Task<GitProviderInstallation?> FindInstallationByEndpointTokenAsync(
        GitProvider provider, string endpointToken, CancellationToken ct = default);

    Task<WebhookDelivery?> GetDeliveryAsync(Guid deliveryId, CancellationToken ct = default);

    /// <summary>The projects a repository is wired to, for this installation.</summary>
    Task<IReadOnlyList<RepositoryLink>> GetActiveLinksForRepositoryAsync(
        Guid installationId, string repositoryExternalId, CancellationToken ct = default);

    /// <summary>Records what a delivery amounted to, and that it is finished with.</summary>
    Task MarkDeliveryProcessedAsync(Guid deliveryId, string outcome, CancellationToken ct = default);

    void AddInstallation(GitProviderInstallation installation);
    void AddDelivery(WebhookDelivery delivery);
    void AddLink(RepositoryLink link);

    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public class GitRepository : IGitRepository
{
    private readonly BoardSyncDbContext _context;

    public GitRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    public Task<GitProviderInstallation?> FindInstallationByEndpointTokenAsync(
        GitProvider provider, string endpointToken, CancellationToken ct = default) =>
        _context.GitProviderInstallations
            .FirstOrDefaultAsync(i => i.Provider == provider && i.EndpointToken == endpointToken, ct);

    public Task<WebhookDelivery?> GetDeliveryAsync(Guid deliveryId, CancellationToken ct = default) =>
        _context.WebhookDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct);

    public async Task<IReadOnlyList<RepositoryLink>> GetActiveLinksForRepositoryAsync(
        Guid installationId, string repositoryExternalId, CancellationToken ct = default) =>
        await _context.RepositoryLinks
            .Where(l => l.InstallationId == installationId
                        && l.RepositoryExternalId == repositoryExternalId
                        && l.IsActive)
            .ToListAsync(ct);

    /// <remarks>
    /// An <c>ExecuteUpdate</c> rather than a load-and-save: the handler has nothing else to write,
    /// and this keeps the job's final act to a single statement that is safe to repeat.
    /// </remarks>
    public async Task MarkDeliveryProcessedAsync(
        Guid deliveryId, string outcome, CancellationToken ct = default) =>
        await _context.WebhookDeliveries
            .Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(d => d.ProcessedAt, DateTime.UtcNow)
                .SetProperty(d => d.Outcome, outcome), ct);

    public void AddInstallation(GitProviderInstallation installation) =>
        _context.GitProviderInstallations.Add(installation);

    public void AddDelivery(WebhookDelivery delivery) => _context.WebhookDeliveries.Add(delivery);

    public void AddLink(RepositoryLink link) => _context.RepositoryLinks.Add(link);

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);
}

/// <summary>Finds the adapter for a provider.</summary>
public interface IGitProviderRegistry
{
    /// <summary>The adapter for a provider, or null when it is not supported by this build.</summary>
    IGitProvider? For(GitProvider provider);

    /// <summary>Every provider this build can accept webhooks from.</summary>
    IReadOnlyCollection<GitProvider> Supported { get; }
}

/// <inheritdoc />
public sealed class GitProviderRegistry : IGitProviderRegistry
{
    private readonly Dictionary<GitProvider, IGitProvider> _byProvider;

    public GitProviderRegistry(IEnumerable<IGitProvider> providers) =>
        _byProvider = providers.ToDictionary(p => p.Provider);

    public IGitProvider? For(GitProvider provider) => _byProvider.GetValueOrDefault(provider);

    public IReadOnlyCollection<GitProvider> Supported => _byProvider.Keys;
}

/// <summary>Generates the secrets an installation is verified by.</summary>
/// <remarks>
/// Both are credentials, so both come from a CSPRNG rather than from <c>Guid.NewGuid</c> — a GUID is
/// unique, which is not the same as unguessable, and for Azure DevOps the endpoint token is most of
/// the security there is.
/// </remarks>
public static class InstallationSecrets
{
    /// <summary>A URL-safe path segment identifying an installation's webhook endpoint.</summary>
    public static string NewEndpointToken() => Generate(32);

    /// <summary>A shared secret for signing or presenting.</summary>
    public static string NewWebhookSecret() => Generate(32);

    private static string Generate(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
