using System.Security.Cryptography;
using System.Text;
using BoardSync.Api.Modules.GitSync.Models;
using BoardSync.Api.Modules.GitSync.Providers;
using BoardSync.Api.Modules.GitSync.Repositories;
using BoardSync.Api.Shared.Kernel.Jobs;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.GitSync.Ingest;

/// <summary>What ingest decided to do with a delivery.</summary>
public enum IngestOutcome
{
    /// <summary>Recorded and queued.</summary>
    Accepted,

    /// <summary>Already seen. The provider is redelivering; nothing more to do.</summary>
    Duplicate,

    /// <summary>Signature or secret did not check out.</summary>
    Unverified,

    /// <summary>No such installation, or it has been disconnected.</summary>
    UnknownInstallation,

    /// <summary>Malformed enough that there is nothing to record.</summary>
    Malformed
}

/// <summary>
/// Accepts webhook deliveries: verify, deduplicate, record, queue.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is processed on the request.</b> A push can carry hundreds of commits touching dozens
/// of work items, and a provider that waits on that will time out and start retrying — or disable
/// the hook. So the request does one insert and answers 202; the work happens in a job.
/// </para>
/// <para>
/// Everything here is ordered so that a failure at any point leaves the system able to recover:
/// verification before anything is stored, the delivery row before the job that processes it, and
/// both in one transaction so a delivery can never exist without its job or the reverse.
/// </para>
/// </remarks>
public interface IWebhookIngestService
{
    Task<IngestOutcome> AcceptAsync(
        GitProvider provider,
        string endpointToken,
        byte[] rawBody,
        IHeaderDictionary headers,
        CancellationToken ct = default);
}

/// <inheritdoc />
public class WebhookIngestService : IWebhookIngestService
{
    private readonly IGitRepository _repository;
    private readonly IGitProviderRegistry _providers;
    private readonly IJobQueue _jobs;
    private readonly ILogger<WebhookIngestService> _logger;

    public WebhookIngestService(
        IGitRepository repository,
        IGitProviderRegistry providers,
        IJobQueue jobs,
        ILogger<WebhookIngestService> logger)
    {
        _repository = repository;
        _providers = providers;
        _jobs = jobs;
        _logger = logger;
    }

    public async Task<IngestOutcome> AcceptAsync(
        GitProvider provider,
        string endpointToken,
        byte[] rawBody,
        IHeaderDictionary headers,
        CancellationToken ct = default)
    {
        var adapter = _providers.For(provider);

        if (adapter is null)
        {
            _logger.LogWarning("Webhook received for unsupported provider {Provider}.", provider);
            return IngestOutcome.Malformed;
        }

        // The endpoint token identifies the installation, so it is looked up by a constant-time
        // comparison inside the query's own index rather than by trying secrets in turn. For the
        // providers that cannot sign, this token is most of the security, which is why it is
        // high-entropy and generated rather than derived from anything guessable.
        var installation = await _repository.FindInstallationByEndpointTokenAsync(provider, endpointToken, ct);

        if (installation is null || !installation.IsActive)
        {
            // Deliberately the same answer as a bad signature, from the caller's point of view: an
            // unknown token must not be distinguishable from a known one with the wrong secret.
            _logger.LogWarning(
                "Webhook for {Provider} presented an endpoint token matching no active installation.",
                provider);

            return IngestOutcome.UnknownInstallation;
        }

        if (!adapter.Verify(rawBody, headers, installation.WebhookSecret))
        {
            _logger.LogWarning(
                "Webhook for installation {InstallationId} failed {Verification} verification.",
                installation.Id, adapter.Verification);

            return IngestOutcome.Unverified;
        }

        var eventName = adapter.EventNameOf(headers);

        if (string.IsNullOrWhiteSpace(eventName))
        {
            _logger.LogWarning(
                "Verified webhook for installation {InstallationId} carried no event name.",
                installation.Id);

            return IngestOutcome.Malformed;
        }

        // Providers that do not send a delivery id get a deterministic one derived from the payload,
        // so redelivering the same bytes still deduplicates. Weaker than a provider-assigned id —
        // two genuinely identical events would collapse — but the alternative is no idempotency at
        // all for those providers.
        var deliveryId = adapter.DeliveryIdOf(headers) ?? DeriveDeliveryId(rawBody);

        var delivery = new WebhookDelivery
        {
            InstallationId = installation.Id,
            Provider = provider,
            ProviderDeliveryId = deliveryId,
            EventName = eventName,
            Payload = Encoding.UTF8.GetString(rawBody),
            Verification = adapter.Verification
        };

        _repository.AddDelivery(delivery);

        // Enqueued before the save, so the delivery row and the job that processes it commit
        // together. Afterwards would leave the job in the change tracker with nothing to persist it
        // — the failure that silently disabled every work item event (audit finding 15).
        _jobs.Enqueue(delivery.Id, new ProcessGitDelivery(delivery.Id), JobPriority.Normal);

        try
        {
            await _repository.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateDelivery(ex))
        {
            // The unique index did its job. A provider redelivering is normal — GitHub reuses the
            // original id when it does, which is exactly what makes this the right key — so this is
            // a success from the caller's point of view.
            _logger.LogInformation(
                "Duplicate delivery {DeliveryId} for installation {InstallationId}; already recorded.",
                deliveryId, installation.Id);

            return IngestOutcome.Duplicate;
        }

        _logger.LogInformation(
            "Accepted {EventName} delivery {DeliveryId} for installation {InstallationId}.",
            eventName, deliveryId, installation.Id);

        return IngestOutcome.Accepted;
    }

    /// <summary>
    /// A stable id for providers that do not supply one: the payload's own digest.
    /// </summary>
    private static string DeriveDeliveryId(byte[] rawBody) =>
        Convert.ToHexStringLower(SHA256.HashData(rawBody))[..32];

    /// <summary>
    /// Whether this failure is the delivery uniqueness index rather than something else.
    /// </summary>
    /// <remarks>
    /// Matched on the constraint name, not on the message text, so it does not depend on the
    /// server's locale or on Npgsql's phrasing.
    /// </remarks>
    private static bool IsDuplicateDelivery(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } postgres
        && postgres.ConstraintName == GitSchema.DeliveryUniqueIndex;
}

/// <summary>Names shared between the model configuration and the code that reacts to constraint violations.</summary>
public static class GitSchema
{
    /// <summary>Uniqueness of (provider, delivery id) — the idempotency key for webhook ingest.</summary>
    public const string DeliveryUniqueIndex = "IX_WebhookDeliveries_Provider_ProviderDeliveryId";
}
