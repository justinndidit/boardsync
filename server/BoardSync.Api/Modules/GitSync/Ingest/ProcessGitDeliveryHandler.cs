using BoardSync.Api.Modules.GitSync.Providers;
using BoardSync.Api.Modules.GitSync.Repositories;
using BoardSync.Api.Modules.GitSync.Services;
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
/// The whole chain: normalize the payload, find the projects the repository feeds, resolve the work
/// items its branch and commits refer to, and move them. This is the mechanism that means nobody
/// drags cards.
/// </para>
/// <para>
/// <b>Idempotent</b>, as every job handler must be: a worker that dies mid-job leaves a lease that
/// expires and the job runs again. Re-running is safe because the transition rules are — a second
/// pass finds the items already in their target state and reports "already there" rather than
/// writing a duplicate history row.
/// </para>
/// </remarks>
public class ProcessGitDeliveryHandler : IJobHandler<ProcessGitDelivery>
{
    private readonly IGitRepository _repository;
    private readonly IGitProviderRegistry _providers;
    private readonly IGitBindingService _binding;
    private readonly IGitTransitionService _transitions;
    private readonly ILogger<ProcessGitDeliveryHandler> _logger;

    public ProcessGitDeliveryHandler(
        IGitRepository repository,
        IGitProviderRegistry providers,
        IGitBindingService binding,
        IGitTransitionService transitions,
        ILogger<ProcessGitDeliveryHandler> logger)
    {
        _repository = repository;
        _providers = providers;
        _binding = binding;
        _transitions = transitions;
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

        var bound = await _binding.ResolveAsync(
            normalized, [.. links.Select(l => l.ProjectId)], ct);

        if (bound.Count == 0)
        {
            // The commonest outcome by far, and not a fault: a branch named without a reference, a
            // reference to another tool's ticket, a typo. Recorded so an unbound-commits view can
            // show a team how well the convention is actually landing.
            await CompleteAsync(delivery.Id, $"{Describe(normalized)}: no work item referenced.", ct);
            return;
        }

        // Every link is for the same repository, so they share a default branch; taking the first is
        // exact rather than a simplification.
        var results = await _transitions.ApplyAsync(
            normalized, bound, delivery.InstallationId, links[0].DefaultBranch, ct);

        await CompleteAsync(delivery.Id, Summarize(normalized, bound, results), ct);
    }

    private async Task CompleteAsync(Guid deliveryId, string outcome, CancellationToken ct)
    {
        _logger.LogInformation("Delivery {DeliveryId}: {Outcome}", deliveryId, outcome);
        await _repository.MarkDeliveryProcessedAsync(deliveryId, outcome, ct);
    }

    private static string Describe(NormalizedGitEvent e) =>
        $"{e.Kind} on {e.RepositoryName}{(e.BranchName is { } b ? $" ({b})" : "")}";

    /// <summary>
    /// Says what the delivery amounted to, including what it declined to do.
    /// </summary>
    /// <remarks>
    /// The skipped reasons matter as much as the moves. "A person changed it after this event" and
    /// "would move backwards" are the invariants doing their job, and without them recorded an item
    /// that did not move looks identical to an integration that is broken.
    /// </remarks>
    private static string Summarize(
        NormalizedGitEvent gitEvent,
        IReadOnlyList<BoundWorkItem> bound,
        IReadOnlyList<TransitionResult> results)
    {
        var moved = results.Where(r => r.Moved)
            .Select(r => $"{r.Reference} {r.From}→{r.To}")
            .ToList();

        var skipped = results.Where(r => !r.Moved)
            .Select(r => $"{r.Reference} unchanged ({r.Skipped})")
            .ToList();

        var parts = new List<string> { $"{Describe(gitEvent)}: bound {bound.Count}" };

        if (moved.Count > 0) parts.Add($"moved {string.Join(", ", moved)}");
        if (skipped.Count > 0) parts.Add(string.Join(", ", skipped));
        if (results.Count == 0) parts.Add("no transition for this event kind");

        return string.Join("; ", parts) + ".";
    }
}
