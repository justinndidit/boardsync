namespace BoardSync.Api.Shared.Metadata;

/// <summary>
/// The human-facing name and sort position of a value the API publishes.
/// </summary>
/// <remarks>
/// <para>
/// Everything the client renders from an enum needs three things: the wire value, a label to show,
/// and an order to sort by. Only the first survives serialization — enums go over the wire as their
/// names, so the numeric value that encodes ordering server-side never arrives. That left the client
/// hardcoding both, and a hardcoded copy of a server-side vocabulary is a second source of truth
/// that no test covers and no migration updates.
/// </para>
/// <para>
/// Putting them here keeps the label beside the value it names, so adding an enum member and
/// forgetting to name it is caught by <c>MetadataCatalogTests</c> rather than shipping as a raw
/// identifier in somebody's dropdown.
/// </para>
/// <para>
/// <b><see cref="Order"/> is positional, not optional.</b> It cannot default to the underlying
/// numeric value, because for <see cref="Modules.Rbac.Models.RoleType"/> those numbers are
/// deliberately meaningless — they were a privilege ladder once, and the whole permission rebuild
/// was about no longer comparing them. An explicit order is the only truthful answer for roles, so
/// it is required of everything rather than being a special case somebody has to remember.
/// </para>
/// </remarks>
/// <param name="label">What a person should see. Title case, no trailing punctuation.</param>
/// <param name="order">
/// Sort position, ascending. Gaps are encouraged — they leave room to insert a value later without
/// renumbering. Values are ordered globally rather than per scope, so a number must read correctly
/// in every subset it appears in.
/// </param>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class DisplayMetadataAttribute(string label, int order) : Attribute
{
    /// <summary>What a person should see.</summary>
    public string Label { get; } = label;

    /// <summary>Sort position, ascending.</summary>
    public int Order { get; } = order;

    /// <summary>
    /// One sentence explaining the value, for tooltips and permission pickers. Optional.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The heading this value belongs under when the client groups a long list — used by
    /// <see cref="Modules.Rbac.Models.Permissions"/>, where twenty-odd capabilities are unusable
    /// as one flat list. Optional.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// What this relationship is called when read from the other end — "Blocks" seen from the target
    /// is "Blocked by". Only meaningful on
    /// <see cref="Modules.WorkItems.Models.WorkItemLinkType"/>, where a link is one row that both
    /// items display differently, and the client cannot derive the other wording.
    /// </summary>
    public string? Inverse { get; init; }
}
