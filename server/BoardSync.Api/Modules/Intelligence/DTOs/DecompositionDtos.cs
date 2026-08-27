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
}

/// <summary>The draft as it is stored and served.</summary>
/// <param name="Roots">Top of the tree. Usually epics, but a small PRD may decompose to features.</param>
/// <param name="Notes">
/// What the model could not place — an ambiguity, a requirement it declined to guess at. Surfaced
/// rather than dropped, because a gap the reader can see is worth more than a tidy tree that
/// quietly omitted something.
/// </param>
public sealed record Decomposition(
    IReadOnlyList<ProposedNode> Roots,
    IReadOnlyList<string> Notes);

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
}

/// <summary>What an acceptance produced.</summary>
public sealed record AcceptanceResult(
    Guid ProposalId,
    int Created,
    IReadOnlyList<Guid> WorkItemIds);
