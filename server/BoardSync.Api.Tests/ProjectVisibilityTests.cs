using System.Reflection;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services;

namespace BoardSync.Api.Tests;

/// <summary>
/// That asking "may they, on this project?" and "which projects may they?" always agree.
/// </summary>
/// <remarks>
/// <para>
/// There are now two authorization paths into a project. <see cref="AccessEvaluator.GrantsAtProject"/>
/// takes one project and answers yes or no; <see cref="AccessEvaluator.VisibleProjects"/> takes the
/// grants and describes the set. Endpoints with a scope in the route use the first, and the reads
/// that span everything — search, the notification feed, the workspace dashboard — use the second.
/// </para>
/// <para>
/// Two paths mean two chances to be wrong, and a disagreement between them would be invisible: the
/// set-based path is used precisely where no single scope is being checked, so nothing else would
/// contradict it. The exhaustive sweep below is what stops that. It is the reason the second path
/// was written as a derivation of the same <see cref="RolePermissions"/> tables rather than as a
/// second description of the same rules.
/// </para>
/// </remarks>
public class ProjectVisibilityTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Team = Guid.NewGuid();
    private static readonly Guid Project = Guid.NewGuid();

    private static readonly Guid OtherOrg = Guid.NewGuid();
    private static readonly Guid OtherTeam = Guid.NewGuid();
    private static readonly Guid OtherProject = Guid.NewGuid();

    private static readonly ProjectLocation Location = new(Org, Team);

    /// <summary>Every permission the system gates on, read off the constants so none can be missed.</summary>
    public static readonly IReadOnlyList<string> AllPermissions =
        typeof(Permissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

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

    /// <summary>
    /// Asserts the two paths agree for this snapshot, across every permission and against a project
    /// that is in the tree as well as one that is not.
    /// </summary>
    private static void AssertPathsAgree(AccessSnapshot snapshot)
    {
        foreach (var permission in AllPermissions)
        {
            var visibility = AccessEvaluator.VisibleProjects(snapshot, permission);

            foreach (var (projectId, location) in new[]
                     {
                         (Project, Location),
                         (OtherProject, new ProjectLocation(OtherOrg, OtherTeam)),

                         // The mixed cases are the ones a naive implementation gets wrong: a project
                         // in an organization the user administers but assigned to a team they are
                         // not on, and the reverse.
                         (OtherProject, new ProjectLocation(Org, OtherTeam)),
                         (OtherProject, new ProjectLocation(OtherOrg, Team))
                     })
            {
                var single = AccessEvaluator.GrantsAtProject(snapshot, permission, projectId, location);
                var set = visibility.Includes(projectId, location);

                Assert.True(single == set,
                    $"Disagreement on '{permission}' for project in org/team {location.OrganizationId}/" +
                    $"{location.AssignedTeamId}: GrantsAtProject={single}, VisibleProjects={set}.");
            }
        }
    }

    // ── The sweep ─────────────────────────────────────────────────────────────

    public static TheoryData<RoleScope, RoleType> EveryRoleAtEveryValidScope()
    {
        var data = new TheoryData<RoleScope, RoleType>();

        foreach (var scope in Enum.GetValues<RoleScope>())
            foreach (var role in RolePermissions.AssignableAt(scope))
                data.Add(scope, role);

        return data;
    }

    /// <summary>
    /// One role, held once, against every permission. Drawn from
    /// <see cref="RolePermissions.AssignableAt"/> rather than a hand-written list, so a role added to
    /// the vocabulary is swept without anyone remembering to add it here.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRoleAtEveryValidScope))]
    public void SingleGrantAgreesAcrossEveryPermission(RoleScope scope, RoleType role) =>
        AssertPathsAgree(scope switch
        {
            RoleScope.Organization => Snapshot(orgs: [(Org, role)]),
            RoleScope.Team => Snapshot(teams: [(Team, role)]),
            RoleScope.Project => Snapshot(projects: [(Project, role)]),
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        });

    /// <summary>
    /// Every pair of roles across different scopes. Union resolution means a second grant can only
    /// add, and this checks that both paths add the same thing.
    /// </summary>
    [Fact]
    public void CombinedGrantsAgreeAcrossEveryPermission()
    {
        var orgRoles = RolePermissions.AssignableAt(RoleScope.Organization);
        var teamRoles = RolePermissions.AssignableAt(RoleScope.Team);
        var projectRoles = RolePermissions.AssignableAt(RoleScope.Project);

        foreach (var orgRole in orgRoles)
            foreach (var teamRole in teamRoles)
                foreach (var projectRole in projectRoles)
                    AssertPathsAgree(Snapshot(
                        orgs: [(Org, orgRole)],
                        teams: [(Team, teamRole)],
                        projects: [(Project, projectRole)]));
    }

    /// <summary>
    /// Grants held somewhere else entirely must not widen the set. The sweep above would catch this
    /// too, but stated on its own because it is the failure mode that leaks across tenants.
    /// </summary>
    [Fact]
    public void GrantsInAnotherOrganizationAgreeAndReachNothingHere() =>
        AssertPathsAgree(Snapshot(
            orgs: [(OtherOrg, RoleType.OrgAdmin)],
            teams: [(OtherTeam, RoleType.TeamMember)],
            projects: [(OtherProject, RoleType.ProjectAdmin)]));

    [Fact]
    public void NoGrantsAgreesEverywhere() => AssertPathsAgree(AccessSnapshot.Empty);

    // ── The regression these were written for ─────────────────────────────────

    /// <summary>
    /// The defect: search and the notification feed treated organization membership as access to
    /// every project inside the organization.
    /// </summary>
    /// <remarks>
    /// <c>Member</c> carries <c>org:read</c> and nothing else, so it must select no project at all —
    /// which is what makes the feed and the search results agree with the 404 that
    /// <c>GET /api/projects/{id}</c> returns to the same person.
    /// </remarks>
    [Theory]
    [InlineData(Permissions.ProjectRead)]
    [InlineData(Permissions.WorkItemRead)]
    [InlineData(Permissions.BoardRead)]
    [InlineData(Permissions.SprintRead)]
    public void OrganizationMemberSeesNoProjects(string permission)
    {
        var visibility = AccessEvaluator.VisibleProjects(
            Snapshot(orgs: [(Org, RoleType.Member)]), permission);

        Assert.True(visibility.IsEmpty);
        Assert.False(visibility.Includes(Project, Location));
    }

    /// <summary>An organization member can still read the organization itself.</summary>
    [Fact]
    public void OrganizationMemberSeesTheirOrganization() =>
        Assert.Equal(
            [Org],
            AccessEvaluator.VisibleOrganizations(
                Snapshot(orgs: [(Org, RoleType.Member)]), Permissions.OrgRead));

    [Fact]
    public void OrganizationMemberCannotAdministerAnyOrganization() =>
        Assert.Empty(AccessEvaluator.VisibleOrganizations(
            Snapshot(orgs: [(Org, RoleType.Member)]), Permissions.OrgAdmin));

    // ── The three routes, named ───────────────────────────────────────────────

    /// <summary>
    /// OrgAdmin selects by organization rather than by enumerating projects — the property that keeps
    /// the visibility set the size of the grant instead of the size of the organization.
    /// </summary>
    [Fact]
    public void OrgAdminSelectsByOrganization()
    {
        var visibility = AccessEvaluator.VisibleProjects(
            Snapshot(orgs: [(Org, RoleType.OrgAdmin)]), Permissions.WorkItemRead);

        Assert.Equal([Org], visibility.OrganizationIds);
        Assert.Empty(visibility.TeamIds);
        Assert.Empty(visibility.ProjectIds);
        Assert.True(visibility.Includes(Project, Location));
    }

    [Fact]
    public void TeamMemberSelectsByTeam()
    {
        var visibility = AccessEvaluator.VisibleProjects(
            Snapshot(teams: [(Team, RoleType.TeamMember)]), Permissions.WorkItemWrite);

        Assert.Equal([Team], visibility.TeamIds);
        Assert.Empty(visibility.OrganizationIds);
        Assert.True(visibility.Includes(Project, Location));

        // …and not a project the team does not serve, even inside the same organization.
        Assert.False(visibility.Includes(OtherProject, new ProjectLocation(Org, OtherTeam)));
    }

    [Fact]
    public void DirectProjectGrantSelectsByProject()
    {
        var visibility = AccessEvaluator.VisibleProjects(
            Snapshot(projects: [(Project, RoleType.Contributor)]), Permissions.WorkItemWrite);

        Assert.Equal([Project], visibility.ProjectIds);
        Assert.Empty(visibility.OrganizationIds);
        Assert.Empty(visibility.TeamIds);
    }

    /// <summary>
    /// A Viewer may find a work item; they may not write one. The set narrows per permission rather
    /// than being computed once and reused, which is why each caller asks for the permission it means.
    /// </summary>
    [Fact]
    public void VisibilityNarrowsWithThePermissionAsked()
    {
        var snapshot = Snapshot(projects: [(Project, RoleType.Viewer)]);

        Assert.False(AccessEvaluator.VisibleProjects(snapshot, Permissions.WorkItemRead).IsEmpty);
        Assert.True(AccessEvaluator.VisibleProjects(snapshot, Permissions.WorkItemWrite).IsEmpty);
    }
}
