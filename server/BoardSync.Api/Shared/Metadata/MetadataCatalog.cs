using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.WorkItems.Domain;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Shared.Metadata;

/// <summary>
/// Builds the published vocabulary from the declarations the server itself runs on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is projected, never restated.</b> Roles and their permission sets come from
/// <see cref="RolePermissions"/>, which is the table the evaluator reads. Legal state transitions
/// come from <see cref="WorkItemStateMachine"/>, which is the table the service enforces. Allowed
/// children come from <see cref="WorkItemHierarchy"/>, which is the table that rejects a bad parent.
/// Labels and sort order come from <see cref="DisplayMetadataAttribute"/> on the values themselves.
/// </para>
/// <para>
/// That is the whole point. A metadata endpoint assembled from hand-written lists would be a fourth
/// copy of the vocabulary — one more thing to forget when a role is added — which is the problem it
/// exists to solve rather than a new instance of it. <c>MetadataCatalogTests</c> holds the line by
/// failing when any enum member is undecorated or missing from the document.
/// </para>
/// <para>
/// Built once, on first read. The content is fixed at compile time, so rebuilding it per request
/// would be pure waste; <see cref="MetadataDocument.Version"/> is a hash of the serialized result,
/// which makes it a usable ETag and means nobody has to remember to bump a version number.
/// </para>
/// </remarks>
public static class MetadataCatalog
{
    private static readonly Lazy<MetadataDocument> Cached = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The published vocabulary.</summary>
    public static MetadataDocument Document => Cached.Value;

    private static MetadataDocument Build()
    {
        var document = new MetadataDocument(
            Version: "",
            Roles: BuildRoles(),
            Permissions: BuildPermissions(),
            WorkItemTypes: BuildWorkItemTypes(),
            WorkItemStates: BuildWorkItemStates(),
            Priorities: Enumerate<WorkItemPriority>(),
            SprintStatuses: Enumerate<SprintStatus>(),
            WorkItemLinkTypes: BuildLinkTypes(),
            TeamPositions: [.. Modules.Rbac.Models.TeamPositions.All.Select(p => p.ToString())]);

        return document with { Version = Fingerprint(document) };
    }

    // ── Roles ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// One entry per (role, scope) pair, driven by <see cref="RolePermissions.AssignableAt"/>.
    /// </summary>
    /// <remarks>
    /// Iterating the scopes and asking which roles belong to each — rather than iterating
    /// <see cref="RoleType"/> and deciding where each fits — is what keeps this honest. The same
    /// method backs the endpoints that hand out roles and the "valid roles are…" rejection message,
    /// so the list a client renders in a picker is the list the server will accept.
    /// </remarks>
    private static List<RoleMetadata> BuildRoles()
    {
        var roles = new List<RoleMetadata>();

        foreach (var scope in Enum.GetValues<RoleScope>())
        {
            foreach (var role in RolePermissions.AssignableAt(scope))
            {
                var display = DisplayOf(role);

                roles.Add(new RoleMetadata(
                    Value: role.ToString(),
                    Label: display.Label,
                    Order: display.Order,
                    Scope: scope.ToString(),
                    IsPosition: scope == RoleScope.Team && Modules.Rbac.Models.TeamPositions.Includes(role),
                    Permissions: Sorted(PermissionsFor(role, scope)),

                    // Only meaningful at team scope: this is the team → project edge, which has no
                    // counterpart at the other two.
                    InheritedProjectPermissions: scope == RoleScope.Team
                        ? Sorted(RolePermissions.ForProjectViaTeam(role))
                        : null,

                    Description: display.Description));
            }
        }

        return [.. roles.OrderBy(r => r.Order).ThenBy(r => r.Scope, StringComparer.Ordinal)];
    }

    private static IEnumerable<string> PermissionsFor(RoleType role, RoleScope scope) => scope switch
    {
        RoleScope.Organization => RolePermissions.ForOrganization(role),
        RoleScope.Team => RolePermissions.ForTeam(role),
        RoleScope.Project => RolePermissions.ForProject(role),
        _ => []
    };

    // ── Permissions ───────────────────────────────────────────────────────────

    private static List<PermissionMetadata> BuildPermissions()
    {
        var fields = typeof(Modules.Rbac.Models.Permissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .ToDictionary(f => (string)f.GetRawConstantValue()!, f => f.GetCustomAttribute<DisplayMetadataAttribute>());

        return
        [
            .. Modules.Rbac.Models.Permissions.All
                .Select(value =>
                {
                    var display = fields.GetValueOrDefault(value);

                    return new PermissionMetadata(
                        Value: value,
                        Label: display?.Label ?? value,
                        Order: display?.Order ?? int.MaxValue,
                        Group: display?.Group,
                        Description: display?.Description);
                })
                .OrderBy(p => p.Order)
        ];
    }

    // ── Work items ────────────────────────────────────────────────────────────

    private static List<WorkItemTypeMetadata> BuildWorkItemTypes() =>
    [
        .. Enum.GetValues<WorkItemType>()
            .Select(type =>
            {
                var display = DisplayOf(type);

                return new WorkItemTypeMetadata(
                    Value: type.ToString(),
                    Label: display.Label,
                    Order: display.Order,
                    AllowedChildren: [.. WorkItemHierarchy.AllowedChildrenOf(type).Select(c => c.ToString())],
                    Description: display.Description);
            })
            .OrderBy(t => t.Order)
    ];

    private static List<WorkItemStateMetadata> BuildWorkItemStates() =>
    [
        .. Enum.GetValues<WorkItemState>()
            .Select(state =>
            {
                var display = DisplayOf(state);

                return new WorkItemStateMetadata(
                    Value: state.ToString(),
                    Label: display.Label,
                    Order: display.Order,

                    // Carried in Group: a state's lane is exactly the kind of heading Group means,
                    // and it saves the client switching on state names to colour a column.
                    Category: display.Group,

                    TransitionsTo:
                    [
                        .. WorkItemStateMachine.AllowedFrom(state)
                            .Select(next => new StateTransitionMetadata(
                                next.ToString(),
                                WorkItemStateMachine.RequiredPermission(state, next)))
                    ],

                    Description: display.Description);
            })
            .OrderBy(s => s.Order)
    ];

    private static List<LinkTypeMetadata> BuildLinkTypes() =>
    [
        .. Enum.GetValues<WorkItemLinkType>()
            .Select(link =>
            {
                var display = DisplayOf(link);

                return new LinkTypeMetadata(
                    Value: link.ToString(),
                    Label: display.Label,
                    Order: display.Order,
                    Inverse: display.Inverse ?? display.Label,
                    Description: display.Description);
            })
            .OrderBy(l => l.Order)
    ];

    // ── Shared plumbing ───────────────────────────────────────────────────────

    /// <summary>Projects any decorated enum into the plain value/label/order shape.</summary>
    private static List<EnumMetadata> Enumerate<TEnum>() where TEnum : struct, Enum =>
    [
        .. Enum.GetValues<TEnum>()
            .Select(value =>
            {
                var display = DisplayOf(value);
                return new EnumMetadata(value.ToString()!, display.Label, display.Order, display.Description);
            })
            .OrderBy(e => e.Order)
    ];

    /// <summary>
    /// The display metadata declared on an enum member.
    /// </summary>
    /// <remarks>
    /// Falls back to the member's own name and an order that sinks it to the bottom, so an
    /// undecorated value degrades to something renderable rather than throwing at startup — but it
    /// is <em>visibly</em> wrong, and <c>MetadataCatalogTests</c> fails on it. A crash here would
    /// take the whole API down for a missing label.
    /// </remarks>
    internal static DisplayMetadataAttribute DisplayOf<TEnum>(TEnum value) where TEnum : struct, Enum =>
        typeof(TEnum).GetField(value.ToString()!)?.GetCustomAttribute<DisplayMetadataAttribute>()
        ?? new DisplayMetadataAttribute(value.ToString()!, int.MaxValue);

    private static IReadOnlyList<string> Sorted(IEnumerable<string> permissions) =>
        [.. permissions.OrderBy(p => p, StringComparer.Ordinal)];

    /// <summary>
    /// A short, stable hash of the document's content, used as both version and ETag.
    /// </summary>
    /// <remarks>
    /// Content-derived rather than hand-maintained: a version somebody has to remember to bump is a
    /// version that will be wrong exactly when it matters, which is the release that changed a role.
    /// </remarks>
    private static string Fingerprint(MetadataDocument document)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(document, FingerprintOptions);
        return Convert.ToHexStringLower(SHA256.HashData(json))[..16];
    }

    private static readonly JsonSerializerOptions FingerprintOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
