using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services;

namespace BoardSync.Api.Tests;

/// <summary>
/// The permission rules, tested without a database.
/// </summary>
/// <remarks>
/// <see cref="AccessEvaluator"/> is pure precisely so these can exist: every rule about who may do
/// what reduces to a snapshot, a scope, and an expected answer. The cases below are the ones the
/// model would be wrong in a dangerous direction if it got them backwards.
/// </remarks>
public class AccessEvaluatorTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Team = Guid.NewGuid();
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid OtherTeam = Guid.NewGuid();

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

    // ── Organization ──────────────────────────────────────────────────────────

    [Fact]
    public void OrgAdminMayAdministerTheirOrganization() =>
        Assert.True(AccessEvaluator.GrantsAtOrganization(
            Snapshot(orgs: [(Org, RoleType.OrgAdmin)]), Permissions.OrgAdmin, Org));

    [Fact]
    public void OrgAdminOfOneOrganizationHasNothingInAnother() =>
        Assert.False(AccessEvaluator.GrantsAtOrganization(
            Snapshot(orgs: [(Org, RoleType.OrgAdmin)]), Permissions.OrgRead, Guid.NewGuid()));

    [Fact]
    public void OrganizationMemberCannotAdminister() =>
        Assert.False(AccessEvaluator.GrantsAtOrganization(
            Snapshot(orgs: [(Org, RoleType.Member)]), Permissions.OrgAdmin, Org));

    // ── Inheritance runs down, never up ───────────────────────────────────────

    [Fact]
    public void OrgAdminInheritsIntoTeamsAndProjects()
    {
        var snapshot = Snapshot(orgs: [(Org, RoleType.OrgAdmin)]);

        Assert.True(AccessEvaluator.GrantsAtTeam(snapshot, Permissions.SprintManage, Team, Org));
        Assert.True(AccessEvaluator.GrantsAtProject(snapshot, Permissions.ProjectAdmin, Project, Location));
    }

    [Fact]
    public void OrganizationReaderReachesNothingBelowTheOrganization()
    {
        var snapshot = Snapshot(orgs: [(Org, RoleType.Member)]);

        Assert.False(AccessEvaluator.GrantsAtTeam(snapshot, Permissions.TeamRead, Team, Org));
        Assert.False(AccessEvaluator.GrantsAtProject(snapshot, Permissions.ProjectRead, Project, Location));
    }

    /// <summary>
    /// The rule that stops one project's administrator reaching the sprints of every sibling project
    /// through the team they share.
    /// </summary>
    [Fact]
    public void ProjectAdminDoesNotReachTheProjectsTeam() =>
        Assert.False(AccessEvaluator.GrantsAtTeam(
            Snapshot(projects: [(Project, RoleType.ProjectAdmin)]),
            Permissions.SprintManage, Team, Org));

    // ── The team → project edge ───────────────────────────────────────────────

    [Fact]
    public void TeamMembershipGrantsContributionOnTheTeamsProjects()
    {
        var snapshot = Snapshot(teams: [(Team, RoleType.TeamMember)]);

        Assert.True(AccessEvaluator.GrantsAtProject(snapshot, Permissions.WorkItemWrite, Project, Location));
        Assert.False(AccessEvaluator.GrantsAtProject(snapshot, Permissions.ProjectAdmin, Project, Location));
    }

    [Fact]
    public void AGrantOnOneTeamDoesNotReachAnotherTeamsProject()
    {
        var snapshot = Snapshot(teams: [(OtherTeam, RoleType.TeamMember)]);

        Assert.False(AccessEvaluator.GrantsAtProject(snapshot, Permissions.ProjectRead, Project, Location));
    }

    /// <summary>
    /// Team positions must not become project administration, however senior they are on the team.
    /// </summary>
    [Theory]
    [InlineData(RoleType.TeamLead)]
    [InlineData(RoleType.ScrumMaster)]
    [InlineData(RoleType.ProductOwner)]
    public void TeamPositionsContributeToProjectsButDoNotAdministerThem(RoleType position)
    {
        var snapshot = Snapshot(teams: [(Team, position)]);

        Assert.True(AccessEvaluator.GrantsAtProject(snapshot, Permissions.WorkItemWrite, Project, Location));
        Assert.False(AccessEvaluator.GrantsAtProject(snapshot, Permissions.ProjectAdmin, Project, Location));
    }

    // ── Positions are peers, not ranks ────────────────────────────────────────

    [Fact]
    public void ScrumMasterRunsSprintsButDoesNotManageTheTeam()
    {
        var snapshot = Snapshot(teams: [(Team, RoleType.ScrumMaster)]);

        Assert.True(AccessEvaluator.GrantsAtTeam(snapshot, Permissions.SprintManage, Team, Org));
        Assert.False(AccessEvaluator.GrantsAtTeam(snapshot, Permissions.TeamMemberManage, Team, Org));
        Assert.False(AccessEvaluator.GrantsAtTeam(snapshot, Permissions.TeamRoleAssign, Team, Org));
    }

    [Fact]
    public void TeamLeadManagesTheTeamAndItsPositions()
    {
        var snapshot = Snapshot(teams: [(Team, RoleType.TeamLead)]);

        Assert.True(AccessEvaluator.GrantsAtTeam(snapshot, Permissions.TeamMemberManage, Team, Org));
        Assert.True(AccessEvaluator.GrantsAtTeam(snapshot, Permissions.TeamRoleAssign, Team, Org));
    }

    [Fact]
    public void PlainTeamMemberOrdersTheSprintButDoesNotSetItsScope()
    {
        var snapshot = Snapshot(teams: [(Team, RoleType.TeamMember)]);

        Assert.True(AccessEvaluator.GrantsAtTeam(snapshot, Permissions.SprintOrder, Team, Org));
        Assert.False(AccessEvaluator.GrantsAtTeam(snapshot, Permissions.SprintScope, Team, Org));
        Assert.False(AccessEvaluator.GrantsAtTeam(snapshot, Permissions.SprintManage, Team, Org));
    }

    /// <summary>
    /// Holding two positions grants the union of both. This is the case a privilege ladder could not
    /// represent, and the reason the snapshot holds a set rather than a single role.
    /// </summary>
    [Fact]
    public void HoldingTwoPositionsGrantsBoth()
    {
        var snapshot = Snapshot(teams: [(Team, RoleType.ScrumMaster), (Team, RoleType.TeamLead)]);

        Assert.True(AccessEvaluator.GrantsAtTeam(snapshot, Permissions.SprintManage, Team, Org));
        Assert.True(AccessEvaluator.GrantsAtTeam(snapshot, Permissions.TeamRoleAssign, Team, Org));
    }

    // ── Absent scope tree ─────────────────────────────────────────────────────

    /// <summary>
    /// A null location means the project could not be found. Inheritance cannot be applied, so only
    /// a direct grant can answer — and nothing else may leak through.
    /// </summary>
    [Fact]
    public void AnUnresolvableProjectFallsBackToDirectGrantsOnly()
    {
        var viaTeam = Snapshot(teams: [(Team, RoleType.TeamMember)]);
        var direct = Snapshot(projects: [(Project, RoleType.ProjectAdmin)]);

        Assert.False(AccessEvaluator.GrantsAtProject(viaTeam, Permissions.ProjectRead, Project, null));
        Assert.True(AccessEvaluator.GrantsAtProject(direct, Permissions.ProjectAdmin, Project, null));
    }

    // ── "Anywhere" checks, for endpoints with no scope ────────────────────────

    [Fact]
    public void OrgAdminManagesMembersSomewhere() =>
        Assert.True(AccessEvaluator.GrantsAnywhere(
            Snapshot(orgs: [(Org, RoleType.OrgAdmin)]), Permissions.OrgMemberManage));

    [Fact]
    public void APlainTeamMemberManagesMembersNowhere() =>
        Assert.False(AccessEvaluator.GrantsAnywhere(
            Snapshot(teams: [(Team, RoleType.TeamMember)]), Permissions.OrgMemberManage));

    [Fact]
    public void AnOrganizationReaderManagesMembersNowhere() =>
        Assert.False(AccessEvaluator.GrantsAnywhere(
            Snapshot(orgs: [(Org, RoleType.Member)]), Permissions.OrgMemberManage));

    /// <summary>
    /// A team lead manages team membership, which is not organization membership. The two are
    /// different permissions and the "anywhere" check must not blur them.
    /// </summary>
    [Fact]
    public void ATeamLeadManagesTeamMembersButNotOrganizationMembers()
    {
        var snapshot = Snapshot(teams: [(Team, RoleType.TeamLead)]);

        Assert.True(AccessEvaluator.GrantsAnywhere(snapshot, Permissions.TeamMemberManage));
        Assert.False(AccessEvaluator.GrantsAnywhere(snapshot, Permissions.OrgMemberManage));
    }

    [Fact]
    public void AUserWithNoGrantsHoldsNothingAnywhere() =>
        Assert.False(AccessEvaluator.GrantsAnywhere(
            AccessSnapshot.Empty, Permissions.OrgMemberManage));

    [Fact]
    public void AUserWithNoGrantsMayDoNothing()
    {
        var empty = AccessSnapshot.Empty;

        Assert.False(AccessEvaluator.GrantsAtOrganization(empty, Permissions.OrgRead, Org));
        Assert.False(AccessEvaluator.GrantsAtTeam(empty, Permissions.TeamRead, Team, Org));
        Assert.False(AccessEvaluator.GrantsAtProject(empty, Permissions.ProjectRead, Project, Location));
    }
}
