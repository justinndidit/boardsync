using BoardSync.Api.Modules.GitSync.Providers;
using BoardSync.Api.Modules.GitSync.Repositories;
using BoardSync.Api.Shared.Kernel.Jobs;

namespace BoardSync.Api.Modules.GitSync.Ingest;

/// <summary>Process one recorded webhook delivery.</summary>
/// <param name="DeliveryId">The <c>git.WebhookDeliveries</c> row to work on.</param>
/// <remarks>
/// Carries only the id, not the payload. The payload is already stored, and duplicating it into the
/// job row would double the write and let the two drift if a delivery were ever corrected.
/// </remarks>
public sealed record ProcessGitDelivery(Guid DeliveryId) : IJobPayload
{
    /// <inheritdoc />
    /// <remarks>Explicit and stable: renaming this record must not strand queued rows.</remarks>
    public static string JobType => "git.delivery.process";
}

/// <summary>
/// Normalizes a delivery and records what it amounted to.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where binding will go.</b> Today it takes the raw payload as far as a
/// <see cref="NormalizedGitEvent"/> and stops: resolving a branch name to a work item, and moving
/// that item, is the next increment. Stopping here on purpose means the ingest half — verification,
/// idempotency, durability, the job pipeline — is provably working before any of it is used to
/// change a board.
/// </para>
/// <para>
/// <b>Idempotent</b>, as every job handler must be: a worker that dies mid-job leaves a lease that
/// expires and the job runs again. Marking the delivery processed is the last thing it does, and
/// re-normalizing a payload has no side effects.
/// </para>
/// </remarks>
public class ProcessGitDeliveryHandler : IJobHandler<ProcessGitDelivery>
{
    private readonly IGitRepository _repository;
    private readonly IGitProviderRegistry _providers;
    private readonly ILogger<ProcessGitDeliveryHandler> _logger;

    public ProcessGitDeliveryHandler(
        IGitRepository repository,
        IGitProviderRegistry providers,
        ILogger<ProcessGitDeliveryHandler> logger)
    {
        _repository = repository;
        _providers = providers;
        _logger = logger;
    }

    public async Task HandleAsync(ProcessGitDelivery payload, CancellationToken ct = default)
    {
        var delivery = await _repository.GetDeliveryAsync(payload.DeliveryId, ct);

        if (delivery is null)
        {
            // The retention sweep removed it before the worker got to it. Nothing to do, and not
            // worth failing the job over — a retry would find it just as absent.
            _logger.LogWarning("Delivery {DeliveryId} no longer exists; skipping.", payload.DeliveryId);
            return;
        }

        if (delivery.ProcessedAt is not null)
        {
            _logger.LogDebug("Delivery {DeliveryId} was already processed.", delivery.Id);
            return;
        }

        var adapter = _providers.For(delivery.Provider)
            ?? throw new InvalidOperationException(
                $"No adapter is registered for provider '{delivery.Provider}'.");

        if (!adapter.TryNormalize(delivery.EventName, delivery.Payload, out var normalized))
        {
            // An event BoardSync does not act on. Recorded rather than dropped: "the integration is
            // quiet" and "the integration is broken" are otherwise the same observation.
            await CompleteAsync(delivery.Id, $"Ignored: no rule for '{delivery.EventName}'.", ct);
            return;
        }

        var links = await _repository.GetActiveLinksForRepositoryAsync(
            delivery.InstallationId, normalized.RepositoryExternalId, ct);

        if (links.Count == 0)
        {
            // A repository the installation can see but nobody wired to a project. Common and
            // harmless — a GitHub App installation covers every repository on the account — and the
            // outcome makes it visible instead of looking like a failure.
            await CompleteAsync(delivery.Id,
                $"Ignored: {normalized.RepositoryName} is not linked to a project.", ct);
            return;
        }

        // Binding lands here next: resolve BS-142 from the branch name or commit messages, check the
        // work item belongs to a linked project, and move it as the integration principal.
        var summary =
            $"Normalized {normalized.Kind} on {normalized.RepositoryName}" +
            $"{(normalized.BranchName is { } branch ? $" ({branch})" : "")}" +
            $", {normalized.Commits.Count} commit(s), {links.Count} linked project(s). " +
            "Binding not yet implemented.";

        _logger.LogInformation("Delivery {DeliveryId}: {Summary}", delivery.Id, summary);

        await CompleteAsync(delivery.Id, summary, ct);
    }

    private async Task CompleteAsync(Guid deliveryId, string outcome, CancellationToken ct) =>
        await _repository.MarkDeliveryProcessedAsync(deliveryId, outcome, ct);
}
