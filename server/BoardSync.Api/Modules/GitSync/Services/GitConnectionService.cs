using BoardSync.Api.Modules.GitSync.DTOs;
using BoardSync.Api.Modules.GitSync.Models;
using BoardSync.Api.Modules.GitSync.Repositories;
using BoardSync.Api.Modules.OrgProject.Services.Interfaces;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Shared.Kernel.Exceptions;

namespace BoardSync.Api.Modules.GitSync.Services;

/// <summary>Connecting an organization to a git host, and wiring its repositories to projects.</summary>
public interface IGitConnectionService
{
    Task<(InstallationResponse Installation, InstallationSecretsResponse Secrets)> ConnectAsync(
        Guid orgId, ConnectInstallationRequest request, Guid connectedBy, string baseUrl,
        CancellationToken ct = default);

    Task<IReadOnlyList<InstallationResponse>> GetInstallationsAsync(
        Guid orgId, CancellationToken ct = default);

    Task<InstallationSecretsResponse> RotateSecretAsync(
        Guid installationId, string baseUrl, CancellationToken ct = default);

    Task DisconnectAsync(Guid installationId, CancellationToken ct = default);

    Task<PagedResult<DeliveryResponse>> GetDeliveriesAsync(
        Guid installationId, PaginationQuery pagination, CancellationToken ct = default);

    Task<IReadOnlyList<RepositoryLinkResponse>> GetLinksAsync(
        Guid projectId, CancellationToken ct = default);

    Task<RepositoryLinkResponse> LinkAsync(
        Guid projectId, LinkRepositoryRequest request, Guid linkedBy, CancellationToken ct = default);

    Task UnlinkAsync(Guid projectId, Guid linkId, CancellationToken ct = default);
}

/// <inheritdoc />
public class GitConnectionService : IGitConnectionService
{
    private readonly IGitRepository _repository;
    private readonly IGitProviderRegistry _providers;
    private readonly IRepositoryLinkService _links;
    private readonly IProjectService _projects;
    private readonly IRbacService _rbac;
    private readonly ILogger<GitConnectionService> _logger;

    public GitConnectionService(
        IGitRepository repository,
        IGitProviderRegistry providers,
        IRepositoryLinkService links,
        IProjectService projects,
        IRbacService rbac,
        ILogger<GitConnectionService> logger)
    {
        _repository = repository;
        _providers = providers;
        _links = links;
        _projects = projects;
        _rbac = rbac;
        _logger = logger;
    }

    // ── Installations ─────────────────────────────────────────────────────────

    public async Task<(InstallationResponse, InstallationSecretsResponse)> ConnectAsync(
        Guid orgId,
        ConnectInstallationRequest request,
        Guid connectedBy,
        string baseUrl,
        CancellationToken ct = default)
    {
        var adapter = _providers.For(request.Provider)
            ?? throw new BusinessRuleException(
                $"'{request.Provider}' is not supported by this build. Supported: " +
                string.Join(", ", _providers.Supported));

        var externalId = request.ExternalId.Trim();

        if (await _repository.InstallationExistsAsync(orgId, request.Provider, externalId, ct))
            throw new ConflictException(
                $"{request.Provider} '{externalId}' is already connected to this organization.");

        var installation = new GitProviderInstallation
        {
            OrganizationId = orgId,
            Provider = request.Provider,
            ExternalId = externalId,
            AccountName = request.AccountName.Trim(),

            // Both from a CSPRNG. A GUID is unique, which is not the same as unguessable, and for a
            // provider that cannot sign payloads the endpoint token is most of the security.
            WebhookSecret = InstallationSecrets.NewWebhookSecret(),
            EndpointToken = InstallationSecrets.NewEndpointToken(),

            Verification = adapter.Verification,
            CreatedBy = connectedBy
        };

        _repository.AddInstallation(installation);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Connected {Provider} '{Account}' to organization {OrgId}.",
            request.Provider, installation.AccountName, orgId);

        return (Describe(installation, linkedRepositories: 0), Secrets(installation, baseUrl));
    }

    public async Task<IReadOnlyList<InstallationResponse>> GetInstallationsAsync(
        Guid orgId, CancellationToken ct = default)
    {
        var installations = await _repository.GetInstallationsForOrganizationAsync(orgId, ct);
        var counts = await _repository.GetLinkCountsAsync(installations.Select(i => i.Id), ct);

        return [.. installations.Select(i => Describe(i, counts.GetValueOrDefault(i.Id, 0)))];
    }

    /// <remarks>
    /// Rotating invalidates every delivery signed with the old secret, so the provider's webhook
    /// configuration has to be updated before the next push — which is why the response repeats the
    /// URL alongside the new secret rather than only handing back the secret.
    /// </remarks>
    public async Task<InstallationSecretsResponse> RotateSecretAsync(
        Guid installationId, string baseUrl, CancellationToken ct = default)
    {
        var installation = await _repository.GetInstallationAsync(installationId, ct)
            ?? throw new NotFoundException("Installation", installationId);

        installation.WebhookSecret = InstallationSecrets.NewWebhookSecret();
        installation.UpdatedAt = DateTime.UtcNow;

        await _repository.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Rotated the webhook secret for installation {InstallationId}; deliveries signed with " +
            "the previous secret will now be rejected.",
            installationId);

        return Secrets(installation, baseUrl);
    }

    /// <remarks>
    /// <para>
    /// Deactivated rather than deleted, and the repository links go with it. Deleting would take the
    /// delivery history — the only record of what the integration did to the board — along with it.
    /// </para>
    /// <para>
    /// The installation's project grants are revoked, which is what actually stops it acting: an
    /// inactive row is refused at ingest, and a revoked grant means even a delivery that somehow got
    /// through could move nothing.
    /// </para>
    /// </remarks>
    public async Task DisconnectAsync(Guid installationId, CancellationToken ct = default)
    {
        var installation = await _repository.GetInstallationAsync(installationId, ct)
            ?? throw new NotFoundException("Installation", installationId);

        var links = await _repository.GetLinksForInstallationAsync(installationId, ct);

        installation.IsActive = false;
        installation.UpdatedAt = DateTime.UtcNow;

        foreach (var link in links)
        {
            link.IsActive = false;
            link.UpdatedAt = DateTime.UtcNow;
        }

        await _repository.SaveChangesAsync(ct);

        // Belt and braces, and the part that matters: without the grant, the principal can do
        // nothing even if a delivery somehow reached the processor.
        foreach (var projectId in links.Select(l => l.ProjectId).Distinct())
            await _rbac.RemoveRoleAsync(installationId, RoleType.Integration, RoleScope.Project, projectId, ct);

        _logger.LogInformation(
            "Disconnected installation {InstallationId} and revoked {Count} project grant(s).",
            installationId, links.Select(l => l.ProjectId).Distinct().Count());
    }

    public async Task<PagedResult<DeliveryResponse>> GetDeliveriesAsync(
        Guid installationId, PaginationQuery pagination, CancellationToken ct = default)
    {
        var page = Math.Max(pagination.Page, 1);
        var pageSize = Math.Clamp(pagination.PageSize, 1, 100);

        var (deliveries, total) = await _repository.GetDeliveriesAsync(
            installationId, (page - 1) * pageSize, pageSize, ct);

        var items = deliveries
            .Select(d => new DeliveryResponse(
                d.Id, d.Provider, d.EventName, d.Verification, d.CreatedAt, d.ProcessedAt, d.Outcome))
            .ToList();

        return new PagedResult<DeliveryResponse>(items, total, page, pageSize);
    }

    // ── Repository links ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RepositoryLinkResponse>> GetLinksAsync(
        Guid projectId, CancellationToken ct = default) =>
        [.. (await _repository.GetLinksForProjectAsync(projectId, ct)).Select(Describe)];

    public async Task<RepositoryLinkResponse> LinkAsync(
        Guid projectId,
        LinkRepositoryRequest request,
        Guid linkedBy,
        CancellationToken ct = default)
    {
        var installation = await _repository.GetInstallationAsync(request.InstallationId, ct);

        if (installation is null || !installation.IsActive)
            throw new NotFoundException("Installation", request.InstallationId);

        // The security boundary. Without it, a project administrator in one organization could wire
        // another organization's repository to their project — and thereby receive its commit
        // messages and branch names through the delivery history.
        var organizationId = await _projects.GetOrganizationIdAsync(projectId, ct)
            ?? throw new NotFoundException("Project", projectId);

        if (installation.OrganizationId != organizationId)
            throw new NotFoundException("Installation", request.InstallationId);

        var externalId = request.RepositoryExternalId.Trim();

        if (await _repository.LinkExistsAsync(request.InstallationId, externalId, projectId, ct))
            throw new ConflictException(
                $"'{request.RepositoryName}' is already linked to this project.");

        var link = await _links.LinkAsync(
            request.InstallationId,
            projectId,
            externalId,
            request.RepositoryName.Trim(),
            request.DefaultBranch ?? "main",
            linkedBy,
            ct);

        link.Installation = installation;

        return Describe(link);
    }

    /// <remarks>
    /// The installation's grant is revoked only when this was its last repository on the project —
    /// two repositories feeding one project is the monorepo case, and unlinking one must not stop
    /// the other working.
    /// </remarks>
    public async Task UnlinkAsync(Guid projectId, Guid linkId, CancellationToken ct = default)
    {
        var link = await _repository.GetLinkAsync(linkId, ct);

        if (link is null || link.ProjectId != projectId || !link.IsActive)
            throw new NotFoundException("Repository link", linkId);

        link.IsActive = false;
        link.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveChangesAsync(ct);

        var remaining = await _repository.CountActiveLinksAsync(link.InstallationId, projectId, ct);

        if (remaining == 0)
        {
            await _rbac.RemoveRoleAsync(
                link.InstallationId, RoleType.Integration, RoleScope.Project, projectId, ct);

            _logger.LogInformation(
                "Unlinked the last repository for installation {InstallationId} on project " +
                "{ProjectId}; its grant is revoked.",
                link.InstallationId, projectId);
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static InstallationResponse Describe(GitProviderInstallation i, int linkedRepositories) =>
        new(i.Id, i.Provider, i.ExternalId, i.AccountName, i.Verification, i.IsActive,
            linkedRepositories, i.CreatedAt);

    private static RepositoryLinkResponse Describe(RepositoryLink l) =>
        new(l.Id, l.InstallationId, l.Installation.Provider, l.RepositoryExternalId,
            l.RepositoryName, l.DefaultBranch, l.CreatedAt);

    private static InstallationSecretsResponse Secrets(GitProviderInstallation i, string baseUrl) =>
        new(i.Id,
            $"{baseUrl.TrimEnd('/')}/api/git/{i.Provider.ToString().ToLowerInvariant()}/webhook/{i.EndpointToken}",
            i.WebhookSecret,
            i.Verification,
            GuidanceFor(i.Verification));

    /// <summary>
    /// What the administrator needs to know about how strongly this provider's deliveries are
    /// verified.
    /// </summary>
    /// <remarks>
    /// Said out loud rather than buried, because it genuinely differs and the difference is the
    /// customer's to weigh: GitHub signs the payload, Azure DevOps cannot sign at all. "We verify
    /// GitHub cryptographically; Azure DevOps does not offer that, so we verify by shared secret"
    /// builds trust rather than spending it.
    /// </remarks>
    private static string GuidanceFor(WebhookVerification verification) => verification switch
    {
        WebhookVerification.HmacSha256 =>
            "Paste the secret into the provider's webhook configuration. Deliveries are verified by " +
            "an HMAC-SHA256 signature over the payload, so both their origin and their contents are " +
            "proven. Store the secret now — it cannot be shown again.",

        WebhookVerification.SharedSecret =>
            "Paste the secret into the provider's webhook configuration. Deliveries are verified by " +
            "the secret alone, which proves origin but not that the payload was unaltered in " +
            "transit. Keep the URL secret as well. Store the secret now — it cannot be shown again.",

        WebhookVerification.BasicAuth =>
            "Configure the webhook with this URL and the secret as the Basic auth password. This " +
            "provider cannot sign payloads, so the URL itself is part of the credential — treat it " +
            "as one. Store both now; they cannot be shown again.",

        _ => "Store the secret now — it cannot be shown again."
    };
}
