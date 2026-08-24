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

    Task<GitProviderInstallation?> GetInstallationAsync(Guid installationId, CancellationToken ct = default);

    /// <summary>The organization an installation belongs to, for resolving its permission scope.</summary>
    Task<Guid?> GetInstallationOrganizationIdAsync(Guid installationId, CancellationToken ct = default);

    Task<IReadOnlyList<GitProviderInstallation>> GetInstallationsForOrganizationAsync(
        Guid orgId, CancellationToken ct = default);

    Task<bool> InstallationExistsAsync(
        Guid orgId, GitProvider provider, string externalId, CancellationToken ct = default);

    /// <summary>How many active repositories each installation feeds, for the settings list.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetLinkCountsAsync(
        IEnumerable<Guid> installationIds, CancellationToken ct = default);

    Task<RepositoryLink?> GetLinkAsync(Guid linkId, CancellationToken ct = default);

    Task<IReadOnlyList<RepositoryLink>> GetLinksForProjectAsync(Guid projectId, CancellationToken ct = default);

    Task<IReadOnlyList<RepositoryLink>> GetLinksForInstallationAsync(
        Guid installationId, CancellationToken ct = default);

    Task<bool> LinkExistsAsync(
        Guid installationId, string repositoryExternalId, Guid projectId, CancellationToken ct = default);

    /// <summary>Active links between one installation and one project — a monorepo may have several.</summary>
    Task<int> CountActiveLinksAsync(Guid installationId, Guid projectId, CancellationToken ct = default);

    /// <summary>Recent deliveries, newest first. The "is the integration working?" view.</summary>
    Task<(IReadOnlyList<WebhookDelivery> Items, int Total)> GetDeliveriesAsync(
        Guid installationId, int skip, int take, CancellationToken ct = default);

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

    public Task<GitProviderInstallation?> GetInstallationAsync(
        Guid installationId, CancellationToken ct = default) =>
        _context.GitProviderInstallations.FirstOrDefaultAsync(i => i.Id == installationId, ct);

    public async Task<Guid?> GetInstallationOrganizationIdAsync(
        Guid installationId, CancellationToken ct = default) =>
        await _context.GitProviderInstallations
            .Where(i => i.Id == installationId)
            .Select(i => (Guid?)i.OrganizationId)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<GitProviderInstallation>> GetInstallationsForOrganizationAsync(
        Guid orgId, CancellationToken ct = default) =>
        await _context.GitProviderInstallations
            .Where(i => i.OrganizationId == orgId)
            .OrderBy(i => i.AccountName)
            .ToListAsync(ct);

    public Task<bool> InstallationExistsAsync(
        Guid orgId, GitProvider provider, string externalId, CancellationToken ct = default) =>
        _context.GitProviderInstallations.AnyAsync(
            i => i.OrganizationId == orgId && i.Provider == provider && i.ExternalId == externalId, ct);

    public async Task<IReadOnlyDictionary<Guid, int>> GetLinkCountsAsync(
        IEnumerable<Guid> installationIds, CancellationToken ct = default)
    {
        var ids = installationIds as List<Guid> ?? [.. installationIds];

        if (ids.Count == 0) return new Dictionary<Guid, int>();

        return await _context.RepositoryLinks
            .Where(l => ids.Contains(l.InstallationId) && l.IsActive)
            .GroupBy(l => l.InstallationId)
            .Select(g => new { InstallationId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.InstallationId, x => x.Count, ct);
    }

    public Task<RepositoryLink?> GetLinkAsync(Guid linkId, CancellationToken ct = default) =>
        _context.RepositoryLinks.FirstOrDefaultAsync(l => l.Id == linkId, ct);

    public async Task<IReadOnlyList<RepositoryLink>> GetLinksForProjectAsync(
        Guid projectId, CancellationToken ct = default) =>
        await _context.RepositoryLinks
            .Include(l => l.Installation)
            .Where(l => l.ProjectId == projectId && l.IsActive)
            .OrderBy(l => l.RepositoryName)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<RepositoryLink>> GetLinksForInstallationAsync(
        Guid installationId, CancellationToken ct = default) =>
        await _context.RepositoryLinks
            .Where(l => l.InstallationId == installationId && l.IsActive)
            .ToListAsync(ct);

    public Task<bool> LinkExistsAsync(
        Guid installationId, string repositoryExternalId, Guid projectId, CancellationToken ct = default) =>
        _context.RepositoryLinks.AnyAsync(
            l => l.InstallationId == installationId
                 && l.RepositoryExternalId == repositoryExternalId
                 && l.ProjectId == projectId
                 && l.IsActive, ct);

    public Task<int> CountActiveLinksAsync(
        Guid installationId, Guid projectId, CancellationToken ct = default) =>
        _context.RepositoryLinks.CountAsync(
            l => l.InstallationId == installationId && l.ProjectId == projectId && l.IsActive, ct);

    public async Task<(IReadOnlyList<WebhookDelivery> Items, int Total)> GetDeliveriesAsync(
        Guid installationId, int skip, int take, CancellationToken ct = default)
    {
        var query = _context.WebhookDeliveries.Where(d => d.InstallationId == installationId);

        var total = await query.CountAsync(ct);

        // The payload is deliberately not projected: it is the largest column in the table and
        // nothing in this view renders it.
        var items = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(d => new WebhookDelivery
            {
                Id = d.Id,
                Provider = d.Provider,
                EventName = d.EventName,
                Verification = d.Verification,
                ProcessedAt = d.ProcessedAt,
                Outcome = d.Outcome,
                CreatedAt = d.CreatedAt
            })
            .ToListAsync(ct);

        return (items, total);
    }

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

/// <summary>
/// Resolves <c>installationId</c> to the organization that administers it.
/// </summary>
/// <remarks>
/// A git connection is an organization-wide credential — one installation feeds many projects — so
/// the permission that governs it is <c>org:admin</c>, not anything project-scoped.
/// </remarks>
public sealed class InstallationScopeResolver : Shared.Auth.Authorization.IScopeResolver
{
    private readonly IGitRepository _repository;

    public InstallationScopeResolver(IGitRepository repository) => _repository = repository;

    public string RouteParameter => "installationId";

    public async Task<Shared.Auth.Authorization.ResolvedScope?> ResolveAsync(
        Guid value, CancellationToken ct)
    {
        var organizationId = await _repository.GetInstallationOrganizationIdAsync(value, ct);

        return organizationId is { } orgId
            ? new Shared.Auth.Authorization.ResolvedScope(Rbac.Models.RoleScope.Organization, orgId)
            : null;
    }
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
