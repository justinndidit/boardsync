using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Modules.Intelligence.DTOs;

/// <summary>
/// One node of a proposed hierarchy.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="WorkItemType"/> and <see cref="WorkItemPriority"/> exactly, so acceptance is a
/// direct map with no interpretation layer — build_context.md §8.2 asked for that specifically, and
/// the reason is that any translation step is a place for the model's vocabulary to leak into the
/// domain's.
/// </para>
/// <para>
/// <see cref="Id"/> is assigned by us after the model answers, not by the model. Asking a model for
/// unique identifiers it must then use consistently in parent references is asking for the one
/// mistake that makes the whole tree unusable; the nesting is carried by the shape of
/// <see cref="Children"/> instead, which it cannot get wrong.
/// </para>
/// </remarks>
public sealed class ProposedNode
{
    /// <summary>Our identifier for this node, stable for the life of the proposal.</summary>
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<WorkItemType>))]
    public WorkItemType Type { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<WorkItemPriority>))]
    public WorkItemPriority Priority { get; set; } = WorkItemPriority.Medium;

    /// <summary>An estimate, when the model offered one. Never invented on its behalf.</summary>
    public int? StoryPoints { get; set; }

    public List<ProposedNode> Children { get; set; } = [];

    /// <summary>
    /// Which delivery phase this item belongs to, counting from 1. Null on containers.
    /// </summary>
    /// <remarks>
    /// Only leaves carry one — an Epic spans its children's phases by definition, so a phase on it
    /// would be a fourth thing that can disagree with the other three.
    /// </remarks>
    public int? Phase { get; set; }
}

/// <summary>One phase of a delivery plan: what it is, and why it is a phase.</summary>
/// <remarks>
/// The model's judgment about ordering, which is the part it is genuinely qualified to give — what
/// must be true before something else can start. It carries no dates and no durations: how long the
/// phases take is arithmetic over the team's measured velocity, not something a model can know.
/// </remarks>
public sealed class ProposedPhase
{
    public string Name { get; set; } = string.Empty;

    /// <summary>What makes this a phase — the dependency that puts it where it is.</summary>
    public string? Rationale { get; set; }
}

/// <summary>The draft as it is stored and served.</summary>
/// <param name="Roots">Top of the tree. Usually epics, but a small PRD may decompose to features.</param>
/// <param name="Notes">
/// What the model could not place — an ambiguity, a requirement it declined to guess at. Surfaced
/// rather than dropped, because a gap the reader can see is worth more than a tidy tree that
/// quietly omitted something.
/// </param>
/// <param name="Phases">
/// The suggested delivery order. May be empty — a document small enough to build in one go does
/// not have phases, and an older proposal was drafted before they existed. Every leaf's
/// <c>Phase</c> indexes into it, counting from 1.
/// </param>
public sealed record Decomposition(
    IReadOnlyList<ProposedNode> Roots,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ProposedPhase>? Phases = null);

/// <summary>Asks for a PRD to be decomposed.</summary>
public sealed class DecomposeRequest
{
    /// <summary>The PRD itself.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>The team the resulting work would belong to.</summary>
    public Guid TeamId { get; set; }
}

/// <summary>A proposal as the client sees it.</summary>
/// <param name="Draft">Null unless <paramref name="Status"/> is Ready or Accepted.</param>
/// <param name="Detail">Why there is no draft, when there is none.</param>
public sealed record ProposalView(
    Guid Id,
    Guid ProjectId,
    string Status,
    Decomposition? Draft,
    string? Detail,
    int TokensSpent,
    int AcceptedCount,
    DateTime CreatedAt);

/// <summary>Accepts some or all of a proposal.</summary>
public sealed class AcceptProposalRequest
{
    /// <summary>
    /// Which nodes to create. Empty means all of them.
    /// </summary>
    /// <remarks>
    /// Including a node implicitly includes its ancestors — see
    /// <c>docs/adr-002-proposals.md</c>. A story cannot be created under a feature that was not.
    /// </remarks>
    public List<string> Include { get; set; } = [];

    /// <summary>
    /// Who the created work items are assigned to. Defaults to the accepting user.
    /// </summary>
    /// <remarks>
    /// Every work item must have an assignee on the owning team — the domain enforces it, and a
    /// proposal has no way to know who should own anything. Defaulting to the accepter puts the
    /// work on the person who chose to create it rather than on nobody.
    /// </remarks>
    public Guid? AssignTo { get; set; }

    /// <summary>
    /// A sprint to plan the accepted work into. Null creates the items and schedules nothing.
    /// </summary>
    /// <remarks>
    /// Optional because the two decisions are separate: a PRD broken down in March may be work for
    /// May, and forcing dates at acceptance would make somebody invent them.
    /// </remarks>
    public SprintPlanRequest? Sprint { get; set; }
}

/// <summary>Dates for the sprint an acceptance creates.</summary>
/// <remarks>
/// The sprint is created in <c>Planning</c>, never started. A plan a model drafted should not put
/// itself into a team's current work — the same reason acceptance exists at all. Somebody starts
/// it, and can edit the dates first.
/// </remarks>
public sealed class SprintPlanRequest
{
    [MaxLength(500)]
    public string? Goal { get; init; }

    [Required]
    public DateTime StartDate { get; init; }

    [Required]
    public DateTime EndDate { get; init; }
}

/// <summary>What an acceptance produced.</summary>
/// <remarks>
/// <c>SprintId</c> is the sprint the work was planned into, or null when none was asked for.
/// <c>Scheduled</c> is how many of the created items went into it — fewer than <c>Created</c>
/// whenever the tree has parents, because an epic and its stories in one sprint would count the
/// same work twice against the commitment. Only the leaves of the accepted tree are scheduled.
/// </remarks>
public sealed record AcceptanceResult(
    Guid ProposalId,
    int Created,
    IReadOnlyList<Guid> WorkItemIds,
    Guid? SprintId = null,
    int Scheduled = 0);

/// <summary>
/// One proposal in a list — enough to choose between them, without the draft.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately excludes <c>Draft</c>. A list of thirty proposals would otherwise carry thirty
/// hierarchies nobody has asked to read, and the draft is what <c>GET /proposals/{id}</c> is for.
/// </para>
/// <para>
/// <c>NodeCount</c> is how many nodes the draft holds, or null when there is none. <c>Preview</c>
/// is the opening of the document this came from, so a reader can tell two proposals apart — the
/// source text is kept verbatim precisely so a proposal can be explained after the fact, and "why
/// did it suggest this?" is not answerable from the output alone.
/// </para>
/// </remarks>
public sealed record ProposalSummary(
    Guid Id,
    Guid ProjectId,
    string Status,
    string? Detail,
    int TokensSpent,
    int AcceptedCount,
    int? NodeCount,
    string Preview,
    DateTime CreatedAt,
    DateTime? DecidedAt);
