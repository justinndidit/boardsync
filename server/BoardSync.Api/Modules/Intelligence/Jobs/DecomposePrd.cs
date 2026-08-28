using System.Text.Json;

using BoardSync.Api.Data;
using BoardSync.Api.Modules.Intelligence.Domain;
using BoardSync.Api.Modules.Intelligence.Models;
using BoardSync.Api.Modules.Intelligence.Services;
using BoardSync.Api.Shared.Kernel.Jobs;

using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Intelligence.Jobs;

/// <summary>Decomposes one proposal's document.</summary>
/// <remarks>
/// Carries only the id. The document is on the proposal row already, and copying it into the job
/// payload would put a whole PRD in the queue table twice.
/// </remarks>
public sealed record DecomposePrd(Guid ProposalId) : IJobPayload
{
    public static string JobType => "intelligence.decompose-prd";
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Long-running, expensive and retryable — which is why build_context.md §8.4 puts it here rather
/// than in a request thread. A PRD decomposition is tens of seconds of model time, and a client
/// holding a connection open for it is a client that times out halfway.
/// </para>
/// <para>
/// <b>Idempotent by status.</b> A worker that dies mid-call leaves a lease that expires and another
/// worker redoes the job; if the first one had already written a draft, the second finds the
/// proposal no longer Pending and stops. Without that check a crash at the wrong moment bills the
/// organization twice for the same document.
/// </para>
/// </remarks>
public sealed class DecomposePrdHandler : IJobHandler<DecomposePrd>
{
    private readonly BoardSyncDbContext _context;
    private readonly IDecomposer _decomposer;
    private readonly ITokenBudget _budget;
    private readonly ILogger<DecomposePrdHandler> _logger;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DecomposePrdHandler(
        BoardSyncDbContext context,
        IDecomposer decomposer,
        ITokenBudget budget,
        ILogger<DecomposePrdHandler> logger)
    {
        _context = context;
        _decomposer = decomposer;
        _budget = budget;
        _logger = logger;
    }

    public async Task HandleAsync(DecomposePrd payload, CancellationToken ct = default)
    {
        var proposal = await _context.Proposals
            .FirstOrDefaultAsync(p => p.Id == payload.ProposalId, ct);

        if (proposal is null)
        {
            // Deleted between enqueue and execution. Nothing to do, and nothing wrong.
            _logger.LogInformation("Proposal {ProposalId} no longer exists", payload.ProposalId);
            return;
        }

        // Already done by an earlier attempt — see the remark on idempotency.
        if (proposal.Status != ProposalStatus.Pending) return;

        if (!_decomposer.IsConfigured)
        {
            await FailAsync(proposal, "No language model is configured for this deployment.", ct);
            return;
        }

        /*
         * The allowance is checked before the call, not after.
         *
         * Checking afterwards means the tokens are already spent — the budget would record
         * overruns rather than prevent them, which is the opposite of what §8.4 asks for.
         */
        if (!await _budget.HasRemainingAsync(proposal.OrganizationId, ct))
        {
            await FailAsync(
                proposal,
                "This organization's daily language model allowance is spent. It resets tomorrow.",
                ct);
            return;
        }

        _logger.LogInformation(
            "Decomposing proposal {ProposalId} ({Remaining} tokens left today)",
            proposal.Id,
            await _budget.RemainingAsync(proposal.OrganizationId, ct));

        var outcome = await _decomposer.DecomposeAsync(proposal.SourceText, ct);

        if (outcome is null)
        {
            await FailAsync(proposal, "The language model could not be reached.", ct);
            return;
        }

        // Charged whether or not the draft survives checking. The tokens were spent either way.
        await _budget.RecordAsync(proposal.OrganizationId, outcome.Value.TokensSpent, ct);
        proposal.TokensSpent = outcome.Value.TokensSpent;

        var checkResult = DecompositionGuard.Check(outcome.Value.Draft);

        if (!checkResult.Accepted)
        {
            _logger.LogWarning(
                "Proposal {ProposalId} rejected by the guard: {Reason}",
                proposal.Id, checkResult.Rejection);

            await FailAsync(proposal, checkResult.Rejection!, ct);
            return;
        }

        proposal.DraftJson = JsonSerializer.Serialize(checkResult.Draft, Json);
        proposal.Status = ProposalStatus.Ready;
        proposal.UpdatedAt = DateTime.UtcNow;

        // Normalizations are surfaced, not silent: a reviewer reading the draft should know it is
        // not verbatim.
        if (checkResult.Repairs.Count > 0)
        {
            proposal.FailureReason = string.Join(" ", checkResult.Repairs);

            _logger.LogInformation(
                "Proposal {ProposalId} needed {Count} repairs", proposal.Id, checkResult.Repairs.Count);
        }

        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Records why there is no draft.
    /// </summary>
    /// <remarks>
    /// A failed proposal, not a failed job. Re-running would call the model again and bill for it
    /// again, and every one of these failures is a state the retry cannot change — no key
    /// configured, no allowance left, a tree the domain will not accept.
    /// </remarks>
    private async Task FailAsync(Proposal proposal, string reason, CancellationToken ct)
    {
        proposal.Status = ProposalStatus.Failed;
        proposal.FailureReason = reason;
        proposal.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
    }
}
