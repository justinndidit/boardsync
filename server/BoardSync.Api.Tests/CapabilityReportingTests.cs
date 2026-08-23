using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services;

namespace BoardSync.Api.Tests;

/// <summary>
/// That what <c>GET /api/me/capabilities</c> reports is exactly what the guards will allow.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint exists so a client stops guessing what to show. It is only worth having if its
/// answer is the same answer <c>PermissionAuthorizationFilter</c> will give when the button is
/// pressed — a report that over-states leaves users clicking things that 403, and one that
/// under-states hides features people are entitled to, which is the failure nobody reports.
/// </para>
/// <para>
/// The two cannot drift by construction, because <c>PermissionsAt…</c> is defined as
/// <c>GrantsAt…</c> run across every permission rather than as a second reading of the rules. These
/// tests hold that construction in place.
/// </para>
/// </remarks>
public class CapabilityReportingTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Team = Guid.NewGuid();
    private static readonly Guid Project = Guid.NewGuid();

    private static readonly ProjectLocation Location = new(Org, Team);

    private static AccessSnapshot Snapshot(
        (Guid Scope, RoleType Role)[]? orgs = null,
        (Guid Scope, RoleType Role)[]? teams = null,
        (Guid Scope, RoleType Role)[]? projects = null)
    {
        static Dictionary<Guid, List<RoleType>> Group((Guid Scope, RoleType Role)[]? pairs) =>
            (pairs ?? [])
                .GroupBy(p => p.Scope)
                .ToDictionary(g => g.Key, g => g.Select(p => p.Role).ToList());

        return new AccessSnapshot(Group(orgs), Group(teams), Group(projects));
    }

    public static TheoryData<RoleScope, RoleType> EveryRoleAtEveryValidScope()
    {
        var data = new TheoryData<RoleScope, RoleType>();

        foreach (var scope in Enum.GetValues<RoleScope>())
            foreach (var role in RolePermissions.AssignableAt(scope))
                data.Add(scope, role);

        return data;
    }

    /// <summary>
    /// For any snapshot, the reported set contains a permission if and only if the guard grants it.
    /// </summary>
    private static void AssertReportMatchesGuards(AccessSnapshot snapshot)
    {
        var atOrg = AccessEvaluator.PermissionsAtOrganization(snapshot, Org);
        var atTeam = AccessEvaluator.PermissionsAtTeam(snapshot, Team, Org);
        var atProject = AccessEvaluator.PermissionsAtProject(snapshot, Project, Location);

        foreach (var permission in Permissions.All)
        {
            Assert.Equal(
                AccessEvaluator.GrantsAtOrganization(snapshot, permission, Org),
                atOrg.Contains(permission));

            Assert.Equal(
                AccessEvaluator.GrantsAtTeam(snapshot, permission, Team, Org),
                atTeam.Contains(permission));

            Assert.Equal(
                AccessEvaluator.GrantsAtProject(snapshot, permission, Project, Location),
                atProject.Contains(permission));
        }
    }

    [Theory]
    [MemberData(nameof(EveryRoleAtEveryValidScope))]
    public void ReportMatchesGuardsForEverySingleGrant(RoleScope scope, RoleType role) =>
        AssertReportMatchesGuards(scope switch
        {
            RoleScope.Organization => Snapshot(orgs: [(Org, role)]),
            RoleScope.Team => Snapshot(teams: [(Team, role)]),
            RoleScope.Project => Snapshot(projects: [(Project, role)]),
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        });

    [Fact]
    public void ReportMatchesGuardsForCombinedGrants()
    {
        foreach (var orgRole in RolePermissions.AssignableAt(RoleScope.Organization))
            foreach (var teamRole in RolePermissions.AssignableAt(RoleScope.Team))
                foreach (var projectRole in RolePermissions.AssignableAt(RoleScope.Project))
                    AssertReportMatchesGuards(Snapshot(
                        orgs: [(Org, orgRole)],
                        teams: [(Team, teamRole)],
                        projects: [(Project, projectRole)]));
    }

    [Fact]
    public void ReportMatchesGuardsWithNoGrants() => AssertReportMatchesGuards(AccessSnapshot.Empty);

    // ── The shape a client depends on ─────────────────────────────────────────

    /// <summary>
    /// A user with nothing gets an empty list, not an error — the same answer an unknown scope gets.
    /// </summary>
    /// <remarks>
    /// Deliberate: an empty report must not distinguish "no such project" from "not yours", matching
    /// the 404-on-denial rule in <c>PermissionAuthorizationFilter</c>. If they differed, the
    /// capabilities endpoint would become the existence oracle that the 404 rule exists to close.
    /// </remarks>
    [Fact]
    public void UnknownAndForbiddenScopesBothReportNothing()
    {
        var stranger = Snapshot(orgs: [(Guid.NewGuid(), RoleType.OrgAdmin)]);

        // A project that exists but is not theirs.
        Assert.Empty(AccessEvaluator.PermissionsAtProject(stranger, Project, Location));

        // A project that does not exist — no location could be resolved.
        Assert.Empty(AccessEvaluator.PermissionsAtProject(stranger, Project, null));
    }

    /// <summary>An OrgAdmin reports everything, at every scope beneath them.</summary>
    [Fact]
    public void OrgAdminReportsEverythingBeneathThem()
    {
        var snapshot = Snapshot(orgs: [(Org, RoleType.OrgAdmin)]);

        Assert.Equal(
            [.. Permissions.All.Order()],
            [.. AccessEvaluator.PermissionsAtOrganization(snapshot, Org).Order()]);

        Assert.Contains(Permissions.SprintManage, AccessEvaluator.PermissionsAtTeam(snapshot, Team, Org));
        Assert.Contains(Permissions.ProjectAdmin, AccessEvaluator.PermissionsAtProject(snapshot, Project, Location));
    }

    /// <summary>
    /// A Scrum Master's project capabilities include running the sprint and exclude administering the
    /// project.
    /// </summary>
    /// <remarks>
    /// The case a client cannot possibly derive on its own, and the one a UI most needs right: sprint
    /// controls enabled, project settings not, on a project the user holds no role on at all.
    /// </remarks>
    [Fact]
    public void ScrumMasterReportsSprintAuthorityButNotProjectAdministration()
    {
        var reported = AccessEvaluator.PermissionsAtProject(
            Snapshot(teams: [(Team, RoleType.ScrumMaster)]), Project, Location);

        Assert.Contains(Permissions.SprintManage, reported);
        Assert.Contains(Permissions.SprintScope, reported);
        Assert.Contains(Permissions.WorkItemWrite, reported);

        Assert.DoesNotContain(Permissions.ProjectAdmin, reported);
        Assert.DoesNotContain(Permissions.BoardConfigure, reported);
        Assert.DoesNotContain(Permissions.ProjectMemberManage, reported);
    }

    /// <summary>
    /// An organization member reports organization read, and nothing that belongs to a team or a
    /// project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The team and project reports are not <em>empty</em>: they contain <c>org:read</c>, because
    /// inheritance runs down the tree generically and a permission held at the organization is held
    /// everywhere beneath it. Asking "may they do org:read on this team" is a slightly odd question,
    /// and the honest answer is yes.
    /// </para>
    /// <para>
    /// It stays that way rather than being filtered to the permissions each scope "owns", because the
    /// report has to equal what the guards do — that equality is the whole value of the endpoint, and
    /// tidying the output would break it. Nothing is at risk: no team or project endpoint checks
    /// <c>org:read</c>, so the extra entry gates nothing.
    /// </para>
    /// <para>
    /// What matters is what is absent, and this asserts that directly.
    /// </para>
    /// </remarks>
    [Fact]
    public void OrganizationMemberReportsNothingBelongingToATeamOrProject()
    {
        var snapshot = Snapshot(orgs: [(Org, RoleType.Member)]);

        Assert.Equal([Permissions.OrgRead], AccessEvaluator.PermissionsAtOrganization(snapshot, Org));

        var atTeam = AccessEvaluator.PermissionsAtTeam(snapshot, Team, Org);
        var atProject = AccessEvaluator.PermissionsAtProject(snapshot, Project, Location);

        Assert.DoesNotContain(Permissions.TeamRead, atTeam);
        Assert.DoesNotContain(Permissions.TeamMemberManage, atTeam);

        Assert.DoesNotContain(Permissions.ProjectRead, atProject);
        Assert.DoesNotContain(Permissions.BoardRead, atProject);
        Assert.DoesNotContain(Permissions.WorkItemRead, atProject);
        Assert.DoesNotContain(Permissions.SprintRead, atProject);
    }

    // ── Scope references ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("org", RoleScope.Organization)]
    [InlineData("team", RoleScope.Team)]
    [InlineData("project", RoleScope.Project)]
    public void ScopeReferencesRoundTrip(string prefix, RoleScope expected)
    {
        var id = Guid.NewGuid();

        Assert.True(ScopeRef.TryParse($"{prefix}:{id}", out var parsed));
        Assert.Equal(expected, parsed.Scope);
        Assert.Equal(id, parsed.Id);
        Assert.Equal($"{prefix}:{id}", parsed.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("project")]
    [InlineData("project:")]
    [InlineData(":deadbeef")]
    [InlineData("project:not-a-guid")]
    [InlineData("sprint:0f8fad5b-d9cb-469f-a165-70867728950e")]  // a real topic, but not a role scope
    [InlineData("user:0f8fad5b-d9cb-469f-a165-70867728950e")]
    public void MalformedScopeReferencesAreRejected(string? value) =>
        Assert.False(ScopeRef.TryParse(value, out _));
}
