using System.Reflection;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Sprints.Models;
using BoardSync.Api.Modules.WorkItems.Domain;
using BoardSync.Api.Modules.WorkItems.Models;
using BoardSync.Api.Shared.Metadata;

namespace BoardSync.Api.Tests;

/// <summary>
/// That the published vocabulary is a projection of the live declarations, and stays one.
/// </summary>
/// <remarks>
/// <para>
/// <c>GET /api/metadata</c> exists because the client was hardcoding eight lists that only the server
/// knew. It only helps if it cannot itself become a ninth: a catalog assembled from hand-written
/// entries would be one more thing to forget when a role is added, which is the problem rather than
/// the fix.
/// </para>
/// <para>
/// So these tests are deliberately about <em>coverage and agreement</em> rather than about content.
/// They do not assert that Critical sorts above Low — they assert that every enum member is present,
/// labelled, and ordered unambiguously, and that every derived list equals the table it was derived
/// from. Adding an enum value without a label fails the build, which is the only mechanism that
/// reliably prevents drift.
/// </para>
/// </remarks>
public class MetadataCatalogTests
{
    private static readonly MetadataDocument Document = MetadataCatalog.Document;

    /// <summary>
    /// The enums the API publishes.
    /// </summary>
    /// <remarks>
    /// Listed explicitly rather than swept from the assembly: plenty of enums are internal plumbing
    /// (<c>TopicKind</c>, <c>RoleScope</c>) and have no business carrying display labels. This list is
    /// short, and each entry is paired below with an assertion that the document contains all of its
    /// members, so a type added here without being published fails.
    /// </remarks>
    public static TheoryData<Type> PublishedEnums() =>
    [
        typeof(RoleType),
        typeof(WorkItemType),
        typeof(WorkItemState),
        typeof(WorkItemPriority),
        typeof(SprintStatus),
        typeof(WorkItemLinkType)
    ];

    // ── Nothing is unlabelled ─────────────────────────────────────────────────

    /// <summary>
    /// Every member of every published enum carries a label and an order.
    /// </summary>
    /// <remarks>
    /// The test that actually earns its keep. Adding a value to one of these enums and shipping it
    /// would otherwise put a raw identifier — "UserStory", "OrgAdmin" — in front of a user, and the
    /// fallback in <c>MetadataCatalog.DisplayOf</c> is deliberately silent so that a missing label is
    /// caught here rather than crashing the API at startup.
    /// </remarks>
    [Theory]
    [MemberData(nameof(PublishedEnums))]
    public void EveryPublishedEnumMemberIsLabelled(Type enumType)
    {
        var undecorated = enumType
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.GetCustomAttribute<DisplayMetadataAttribute>() is null)
            .Select(f => f.Name)
            .ToList();

        Assert.True(undecorated.Count == 0,
            $"{enumType.Name} has members with no [DisplayMetadata]: {string.Join(", ", undecorated)}. " +
            "Add a label and an order beside the value.");
    }

    /// <summary>Every permission constant carries a label, an order and a group.</summary>
    [Fact]
    public void EveryPermissionIsLabelled()
    {
        var unlabelled = Document.Permissions
            .Where(p => p.Label == p.Value || p.Order == int.MaxValue || p.Group is null)
            .Select(p => p.Value)
            .ToList();

        Assert.True(unlabelled.Count == 0,
            $"Permissions missing label, order or group: {string.Join(", ", unlabelled)}.");
    }

    // ── Nothing is missing ────────────────────────────────────────────────────

    [Fact]
    public void EveryPermissionConstantIsPublished() =>
        Assert.Equal(
            [.. Permissions.All.Order()],
            [.. Document.Permissions.Select(p => p.Value).Order()]);

    [Fact]
    public void EveryWorkItemTypeIsPublished() =>
        Assert.Equal(Enum.GetValues<WorkItemType>().Length, Document.WorkItemTypes.Count);

    [Fact]
    public void EveryWorkItemStateIsPublished() =>
        Assert.Equal(Enum.GetValues<WorkItemState>().Length, Document.WorkItemStates.Count);

    [Fact]
    public void EveryPriorityIsPublished() =>
        Assert.Equal(Enum.GetValues<WorkItemPriority>().Length, Document.Priorities.Count);

    [Fact]
    public void EverySprintStatusIsPublished() =>
        Assert.Equal(Enum.GetValues<SprintStatus>().Length, Document.SprintStatuses.Count);

    [Fact]
    public void EveryLinkTypeIsPublished() =>
        Assert.Equal(Enum.GetValues<WorkItemLinkType>().Length, Document.WorkItemLinkTypes.Count);

    /// <summary>
    /// One role entry per (role, scope) pair that <see cref="RolePermissions.AssignableAt"/> allows.
    /// </summary>
    /// <remarks>
    /// Not one per role: <c>Viewer</c> is valid at team and project scope and permits different
    /// things at each, so it appears twice. That is the shape a picker needs — "which roles may I
    /// grant here" is a per-scope question.
    /// </remarks>
    [Fact]
    public void EveryAssignableRoleAndScopePairIsPublished()
    {
        var expected = Enum.GetValues<RoleScope>()
            .SelectMany(scope => RolePermissions.AssignableAt(scope)
                .Select(role => $"{role}@{scope}"))
            .Order()
            .ToList();

        var published = Document.Roles.Select(r => $"{r.Value}@{r.Scope}").Order().ToList();

        Assert.Equal(expected, published);
    }

    // ── Nothing disagrees with its source ─────────────────────────────────────

    /// <summary>
    /// Each role's published permissions equal what <see cref="RolePermissions"/> grants it at that
    /// scope.
    /// </summary>
    [Fact]
    public void PublishedRolePermissionsMatchTheRoleTable()
    {
        foreach (var role in Document.Roles)
        {
            var parsed = Enum.Parse<RoleType>(role.Value);
            var scope = Enum.Parse<RoleScope>(role.Scope);

            IEnumerable<string> expected = scope switch
            {
                RoleScope.Organization => RolePermissions.ForOrganization(parsed),
                RoleScope.Team => RolePermissions.ForTeam(parsed),
                RoleScope.Project => RolePermissions.ForProject(parsed),
                _ => []
            };

            Assert.Equal([.. expected.Order()], [.. role.Permissions.Order()]);
        }
    }

    /// <summary>
    /// The team → project edge is published for team roles and omitted elsewhere.
    /// </summary>
    /// <remarks>
    /// This is the part of the model a client genuinely cannot work out: why a Scrum Master can run
    /// sprints on a project they hold no role on. If it disagreed with
    /// <see cref="RolePermissions.ForProjectViaTeam"/> the UI would gate sprint controls wrongly.
    /// </remarks>
    [Fact]
    public void PublishedTeamInheritanceMatchesTheEdge()
    {
        foreach (var role in Document.Roles)
        {
            if (role.Scope != nameof(RoleScope.Team))
            {
                Assert.Null(role.InheritedProjectPermissions);
                continue;
            }

            var expected = RolePermissions.ForProjectViaTeam(Enum.Parse<RoleType>(role.Value));

            Assert.NotNull(role.InheritedProjectPermissions);
            Assert.Equal([.. expected.Order()], [.. role.InheritedProjectPermissions.Order()]);
        }
    }

    /// <summary>
    /// Published transitions equal the table the service enforces.
    /// </summary>
    /// <remarks>
    /// The whole reason <see cref="WorkItemStateMachine"/> was lifted out of <c>WorkItemService</c>.
    /// A client builds its "Move to…" menu from this; if it offered a move the service rejects, the
    /// bug would read as the server being wrong.
    /// </remarks>
    [Fact]
    public void PublishedTransitionsMatchTheStateMachine()
    {
        foreach (var state in Document.WorkItemStates)
        {
            var parsed = Enum.Parse<WorkItemState>(state.Value);

            Assert.Equal(
                [.. WorkItemStateMachine.AllowedFrom(parsed).Select(s => s.ToString()).Order()],
                [.. state.TransitionsTo.Select(t => t.State).Order()]);

            foreach (var transition in state.TransitionsTo)
            {
                Assert.Equal(
                    WorkItemStateMachine.RequiredPermission(parsed, Enum.Parse<WorkItemState>(transition.State)),
                    transition.RequiresPermission);
            }
        }
    }

    /// <summary>Published nesting rules equal the table the service enforces.</summary>
    [Fact]
    public void PublishedHierarchyMatchesTheHierarchyTable()
    {
        foreach (var type in Document.WorkItemTypes)
        {
            Assert.Equal(
                [.. WorkItemHierarchy.AllowedChildrenOf(Enum.Parse<WorkItemType>(type.Value))
                       .Select(c => c.ToString()).Order()],
                [.. type.AllowedChildren.Order()]);
        }
    }

    /// <summary>
    /// Every transition a client is told about names a permission that actually exists.
    /// </summary>
    /// <remarks>
    /// Guards the QA gate landing: Phase B changes <c>RequiredPermission</c> to return
    /// <c>workitem:verify</c> on the edges into Closed, and this fails if that constant is not also
    /// added to <see cref="Permissions"/>.
    /// </remarks>
    [Fact]
    public void EveryTransitionPermissionIsARealPermission()
    {
        var known = Permissions.All.ToHashSet();

        foreach (var transition in Document.WorkItemStates.SelectMany(s => s.TransitionsTo))
            Assert.Contains(transition.RequiresPermission, known);
    }

    // ── The client can rely on the shape ──────────────────────────────────────

    /// <summary>
    /// Sort order is unambiguous within every list.
    /// </summary>
    /// <remarks>
    /// A duplicate order makes the client's sort non-deterministic, which shows up as two values
    /// swapping places between page loads — the kind of bug nobody files and everybody notices.
    /// Roles are keyed on scope as well, since one role legitimately appears at two.
    /// </remarks>
    [Fact]
    public void OrdersAreUniqueWithinEachList()
    {
        AssertDistinct(Document.Permissions.Select(p => p.Order), nameof(Document.Permissions));
        AssertDistinct(Document.WorkItemTypes.Select(t => t.Order), nameof(Document.WorkItemTypes));
        AssertDistinct(Document.WorkItemStates.Select(s => s.Order), nameof(Document.WorkItemStates));
        AssertDistinct(Document.Priorities.Select(p => p.Order), nameof(Document.Priorities));
        AssertDistinct(Document.SprintStatuses.Select(s => s.Order), nameof(Document.SprintStatuses));
        AssertDistinct(Document.WorkItemLinkTypes.Select(l => l.Order), nameof(Document.WorkItemLinkTypes));

        foreach (var scope in Document.Roles.GroupBy(r => r.Scope))
            AssertDistinct(scope.Select(r => r.Order), $"Roles at {scope.Key}");

        static void AssertDistinct(IEnumerable<int> orders, string what)
        {
            var all = orders.ToList();
            Assert.True(all.Count == all.Distinct().Count(), $"{what} has duplicate order values.");
        }
    }

    /// <summary>Every list arrives already sorted, so a client that ignores order still renders sanely.</summary>
    [Fact]
    public void ListsArriveSorted()
    {
        Assert.Equal([.. Document.Permissions.Select(p => p.Order).Order()],
                     [.. Document.Permissions.Select(p => p.Order)]);
        Assert.Equal([.. Document.WorkItemStates.Select(s => s.Order).Order()],
                     [.. Document.WorkItemStates.Select(s => s.Order)]);
        Assert.Equal([.. Document.Priorities.Select(p => p.Order).Order()],
                     [.. Document.Priorities.Select(p => p.Order)]);
    }

    /// <summary>
    /// Priority order runs most urgent first, because this is the case that motivated the endpoint.
    /// </summary>
    /// <remarks>
    /// The one content assertion here. <c>Critical = 1 … Low = 4</c> means the numbering <em>is</em>
    /// the ordering, and it is exactly what a string on the wire cannot carry — so if this list ever
    /// came back in enum-declaration or alphabetical order, every board in the product would sort its
    /// priorities wrongly and nothing else would notice.
    /// </remarks>
    [Fact]
    public void PrioritiesRunMostUrgentFirst() =>
        Assert.Equal(
            ["Critical", "High", "Medium", "Low"],
            [.. Document.Priorities.Select(p => p.Value)]);

    /// <summary>Link inverses are always populated — the client cannot derive them.</summary>
    [Fact]
    public void EveryLinkTypeHasAnInverse() =>
        Assert.All(Document.WorkItemLinkTypes, link => Assert.False(string.IsNullOrWhiteSpace(link.Inverse)));

    // ── Versioning ────────────────────────────────────────────────────────────

    /// <summary>
    /// The version is content-derived and stable, so it works as an ETag.
    /// </summary>
    /// <remarks>
    /// Stability matters more than it looks: the document is built from dictionaries and reflection,
    /// neither of which promises enumeration order. If any of that leaked into the output the hash
    /// would change per process, every client would revalidate to a 200 forever, and the cache would
    /// silently do nothing.
    /// </remarks>
    [Fact]
    public void VersionIsStableAcrossReads()
    {
        Assert.False(string.IsNullOrWhiteSpace(Document.Version));
        Assert.Equal(Document.Version, MetadataCatalog.Document.Version);
        Assert.Equal(16, Document.Version.Length);
    }
}
