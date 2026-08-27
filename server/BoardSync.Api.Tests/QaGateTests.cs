using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services;
using BoardSync.Api.Modules.WorkItems.Domain;
using BoardSync.Api.Modules.WorkItems.Models;

namespace BoardSync.Api.Tests;

/// <summary>
/// That work cannot be declared done by whoever did it, or by anything automated.
/// </summary>
/// <remarks>
/// <para>
/// The product's premise is that the board updates itself from git. That is only trustworthy if the
/// automation stops somewhere, and the place it stops is <c>Closed</c>: a push, a pull request and a
/// merge can carry an item as far as <c>Resolved</c> — merged, awaiting test — and a human holding
/// <see cref="Permissions.WorkItemVerify"/> is what takes it the last step.
/// </para>
/// <para>
/// These tests are the rules half, run against the pure state machine and evaluator.
/// <c>QaGateEndpointTests</c> is the enforcement half.
/// </para>
/// </remarks>
public class QaGateTests
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

    private static bool CanVerify(AccessSnapshot snapshot) =>
        AccessEvaluator.GrantsAtProject(snapshot, Permissions.WorkItemVerify, Project, Location);

    // ── The shape of the workflow ─────────────────────────────────────────────

    /// <summary>
    /// <c>Closed</c> is reachable from <c>Resolved</c> and from nowhere else.
    /// </summary>
    /// <remarks>
    /// The structural half of the gate. If any other state could reach Closed, the permission on the
    /// Resolved edge would be decoration.
    /// </remarks>
    [Fact]
    public void ClosedIsReachableOnlyFromResolved()
    {
        var reaching = Enum.GetValues<WorkItemState>()
            .Where(state => WorkItemStateMachine.AllowedFrom(state).Contains(WorkItemState.Closed))
            .ToList();

        Assert.Equal([WorkItemState.Resolved], reaching);
    }

    /// <summary>Git's three signals have somewhere to land, in order.</summary>
    [Theory]
    [InlineData(WorkItemState.New, WorkItemState.Active)]        // first commit on a bound branch
    [InlineData(WorkItemState.Active, WorkItemState.InReview)]   // pull request opened
    [InlineData(WorkItemState.InReview, WorkItemState.Resolved)] // merged to the default branch
    [InlineData(WorkItemState.InReview, WorkItemState.Active)]   // closed unmerged, or changes asked
    public void TheGitDrivenPathIsWalkable(WorkItemState from, WorkItemState to) =>
        Assert.True(WorkItemStateMachine.CanTransition(from, to));

    /// <summary>Work that needs no pull request can still be marked ready for test.</summary>
    [Fact]
    public void WorkCanReachResolvedWithoutAPullRequest() =>
        Assert.True(WorkItemStateMachine.CanTransition(WorkItemState.Active, WorkItemState.Resolved));

    // ── The permission on each edge ───────────────────────────────────────────

    /// <summary>
    /// Every move out of the QA lane needs <c>workitem:verify</c>; everything before it does not.
    /// </summary>
    /// <remarks>
    /// Guarding both edges out of <c>Resolved</c> rather than only the one into <c>Closed</c> is
    /// deliberate. Letting the author pull their own item back to <c>Active</c> would take it out of
    /// QA's queue before a rejection was ever recorded — the same bypass by a slower route.
    /// </remarks>
    [Fact]
    public void OnlyTheMovesOutOfTheQaLaneRequireVerification()
    {
        foreach (var from in Enum.GetValues<WorkItemState>())
        {
            foreach (var to in WorkItemStateMachine.AllowedFrom(from))
            {
                var required = WorkItemStateMachine.RequiredPermission(from, to);

                var expected = from is WorkItemState.Resolved or WorkItemState.Closed
                    ? Permissions.WorkItemVerify
                    : Permissions.WorkItemWrite;

                Assert.True(required == expected,
                    $"{from} → {to} requires '{required}', expected '{expected}'.");
            }
        }
    }

    // ── Who may certify ───────────────────────────────────────────────────────

    /// <summary>
    /// The roles that carry certification onto a project, whether granted there or inherited from
    /// the team.
    /// </summary>
    public static TheoryData<RoleScope, RoleType> Certifiers() => new()
    {
        { RoleScope.Project, RoleType.Tester },
        { RoleScope.Project, RoleType.ProjectAdmin },
        { RoleScope.Team, RoleType.Tester },
        { RoleScope.Team, RoleType.TeamLead },
        { RoleScope.Team, RoleType.ProductOwner },
        { RoleScope.Organization, RoleType.OrgAdmin }
    };

    [Theory]
    [MemberData(nameof(Certifiers))]
    public void CertifiersMayVerify(RoleScope scope, RoleType role) =>
        Assert.True(CanVerify(scope switch
        {
            RoleScope.Organization => Snapshot(orgs: [(Org, role)]),
            RoleScope.Team => Snapshot(teams: [(Team, role)]),
            _ => Snapshot(projects: [(Project, role)])
        }));

    /// <summary>
    /// The roles that must not certify — above all, the ones that do the work.
    /// </summary>
    /// <remarks>
    /// <c>Contributor</c> and <c>TeamMember</c> are the point of the whole exercise: whoever writes
    /// the code must not also be the one who declares it correct.
    /// </remarks>
    public static TheoryData<RoleScope, RoleType> NonCertifiers() => new()
    {
        { RoleScope.Project, RoleType.Contributor },
        { RoleScope.Project, RoleType.Viewer },
        { RoleScope.Team, RoleType.TeamMember },
        { RoleScope.Team, RoleType.Viewer },
        { RoleScope.Organization, RoleType.Member }
    };

    [Theory]
    [MemberData(nameof(NonCertifiers))]
    public void NonCertifiersMayNotVerify(RoleScope scope, RoleType role) =>
        Assert.False(CanVerify(scope switch
        {
            RoleScope.Organization => Snapshot(orgs: [(Org, role)]),
            RoleScope.Team => Snapshot(teams: [(Team, role)]),
            _ => Snapshot(projects: [(Project, role)])
        }));

    /// <summary>
    /// A Scrum Master runs the sprint and does not sign work off; a Product Owner does both.
    /// </summary>
    /// <remarks>
    /// build_context.md §11 decision 1, asserted rather than left to comments because it is the one
    /// judgement call in the gate and it is easy to change by accident. In Scrum the Product Owner
    /// accepts the increment — deciding whether what was built is what was asked for is the same act
    /// as certifying it — while the Scrum Master owns the process. Both hold sprint authority; only
    /// one holds acceptance.
    /// </remarks>
    [Fact]
    public void ScrumMasterRunsTheSprintButDoesNotCertify()
    {
        var scrumMaster = Snapshot(teams: [(Team, RoleType.ScrumMaster)]);
        var productOwner = Snapshot(teams: [(Team, RoleType.ProductOwner)]);

        // Sprint authority is at team scope; certification is about the work, so it stays a
        // project question.
        Assert.True(AccessEvaluator.GrantsAtTeam(
            scrumMaster, Permissions.SprintManage, Team, Org));
        Assert.False(CanVerify(scrumMaster));

        Assert.True(AccessEvaluator.GrantsAtTeam(
            productOwner, Permissions.SprintManage, Team, Org));
        Assert.True(CanVerify(productOwner));
    }

    /// <summary>
    /// A Tester contributes as well as certifies, and administers nothing.
    /// </summary>
    /// <remarks>
    /// Testers file bugs and comment, so read-only-plus-one-power would be the wrong shape. It stops
    /// well short of administration: certifying work says nothing about renaming the project,
    /// reconfiguring its board, or deciding what its sprint commits to.
    /// </remarks>
    [Fact]
    public void TesterCertifiesAndContributesButDoesNotAdminister()
    {
        var tester = Snapshot(projects: [(Project, RoleType.Tester)]);

        Assert.True(CanVerify(tester));
        Assert.True(AccessEvaluator.GrantsAtProject(tester, Permissions.WorkItemWrite, Project, Location));
        Assert.True(AccessEvaluator.GrantsAtProject(tester, Permissions.WorkItemComment, Project, Location));

        Assert.False(AccessEvaluator.GrantsAtProject(tester, Permissions.ProjectAdmin, Project, Location));
        Assert.False(AccessEvaluator.GrantsAtProject(tester, Permissions.BoardConfigure, Project, Location));
        Assert.False(AccessEvaluator.GrantsAtProject(tester, Permissions.SprintScope, Project, Location));
        Assert.False(AccessEvaluator.GrantsAtProject(tester, Permissions.WorkItemDelete, Project, Location));
    }

    /// <summary>
    /// A team Tester reaches every project the team serves, and nothing outside it.
    /// </summary>
    [Fact]
    public void TeamTesterReachesTheTeamsProjectsOnly()
    {
        var snapshot = Snapshot(teams: [(Team, RoleType.Tester)]);

        Assert.True(CanVerify(snapshot));

        Assert.False(AccessEvaluator.GrantsAtProject(
            snapshot, Permissions.WorkItemVerify,
            Guid.NewGuid(), new ProjectLocation(Org, Guid.NewGuid())));
    }

    /// <summary>
    /// Certification is not something a team's own membership confers.
    /// </summary>
    /// <remarks>
    /// Team membership grants Contributor on the team's projects, which is what makes onboarding one
    /// step. If it also granted certification, every developer on the team could sign off their own
    /// work and the gate would be open by default.
    /// </remarks>
    [Fact]
    public void TeamMembershipAloneDoesNotConferCertification() =>
        Assert.False(CanVerify(Snapshot(teams: [(Team, RoleType.TeamMember)])));
}
