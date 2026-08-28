using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.Intelligence.Models;

/// <summary>
/// Where a proposal is in its life.
/// </summary>
/// <remarks>
/// Stored by name, like every other enum here, so inserting a value in the middle is safe.
/// </remarks>
public enum ProposalStatus
{
    /// <summary>Queued. The job has not produced anything yet.</summary>
    Pending,

    /// <summary>The model answered and the draft passed structural checking. Awaiting a human.</summary>
    Ready,

    /// <summary>
    /// No draft, and there will not be one — no model configured, the allowance spent, the provider
    /// unreachable, or an answer that could not be made into a valid tree.
    /// </summary>
    Failed,

    /// <summary>A human accepted some or all of it, and real work items exist.</summary>
    Accepted,

    /// <summary>A human looked and declined. Kept, because a rejection is a signal worth having.</summary>
    Rejected
}

/// <summary>
/// A generated artifact awaiting a human decision.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the boundary from build_context.md §8.1.</b> Decomposition writes nothing to the
/// board — it writes one of these. A human reads it and accepts, and the acceptance calls the same
/// <c>WorkItemService.CreateAsync</c> that a person clicking "New work item" calls, with the same
/// permission checks. A proposal itself has no authority, which is why it needs no permission of
/// its own to exist.
/// </para>
/// <para>
/// The draft is stored as JSON rather than as rows. It is not domain data — it is a suggestion, it
/// has no identity anything else refers to, and modelling it as a shadow work item hierarchy would
/// mean a second tree that every query over work items has to learn to exclude. When it is
/// accepted, real rows are created and the draft becomes history.
/// </para>
/// <para>
/// Kept after acceptance or rejection on purpose. Every decision made here is a labelled example of
/// what this team considers a good breakdown, which is the training signal §8.1 says autonomous
/// writes would throw away.
/// </para>
/// </remarks>
public class Proposal : BaseEntity
{
    /// <summary>The project the work would be created in.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>The organization, denormalized — the token allowance is charged per organization.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>
    /// The team the accepted work items will belong to.
    /// </summary>
    /// <remarks>
    /// Fixed when the proposal is requested rather than chosen at acceptance, because it decides
    /// who may be assigned the resulting work: the domain requires every work item to have an
    /// assignee on its owning team, so the team has to be known before anyone can be offered as one.
    /// </remarks>
    public Guid TeamId { get; set; }

    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;

    /// <summary>
    /// What the requester supplied, kept verbatim.
    /// </summary>
    /// <remarks>
    /// Retained so a proposal can be explained after the fact. "Why did it suggest this?" is not
    /// answerable from the output alone, and a regenerated answer would be a different one.
    /// </remarks>
    public string SourceText { get; set; } = string.Empty;

    /// <summary>The draft hierarchy as JSON, or null while pending or after a failure.</summary>
    public string? DraftJson { get; set; }

    /// <summary>Why there is no draft, when there is none. Shown to the requester.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Input plus output tokens, charged whether or not the draft was usable.</summary>
    public int TokensSpent { get; set; }

    /// <summary>Who resolved it, and when. Null while it is still awaiting a decision.</summary>
    public Guid? DecidedBy { get; set; }

    public DateTime? DecidedAt { get; set; }

    /// <summary>
    /// How many work items the acceptance actually created.
    /// </summary>
    /// <remarks>
    /// Recorded rather than inferred from the draft: a human may accept a subset, and the count
    /// that matters afterwards is what reached the board, not what was offered.
    /// </remarks>
    public int AcceptedCount { get; set; }
}
