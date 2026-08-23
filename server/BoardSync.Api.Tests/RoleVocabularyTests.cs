using BoardSync.Api.Modules.Rbac.Models;

namespace BoardSync.Api.Tests;

/// <summary>
/// The rule that each scope has its own role names, held to.
/// </summary>
/// <remarks>
/// <para>
/// The vocabulary is the whole point of the three-scope split: reading <c>Contributor</c> on an
/// assignment should tell you it is a grant on a project without your having to check which column
/// is populated. That property is easy to state and easy to erode — it erodes the moment someone
/// reuses a familiar name at a second scope because it nearly fits, which is exactly how
/// <c>Reader</c> came to mean "organization member" in one place and "read-only" in two others.
/// </para>
/// <para>
/// The expected sets below are written out literally rather than derived. Deriving them from the
/// same table they check would make the test vacuous; spelled out, changing the model means editing
/// this file, which is the point at which someone has to agree the change is intended. They must
/// also stay in step with the <c>CK_RoleAssignment_RoleMatchesScope</c> check constraint in
/// <c>Stage3_ScopedRoleNames</c>, which is the database's copy of the same rule.
/// </para>
/// </remarks>
public class RoleVocabularyTests
{
    [Fact]
    public void OrganizationScopeHasItsOwnRoles() =>
        Assert.Equal(
            [RoleType.OrgAdmin, RoleType.Member],
            RolePermissions.AssignableAt(RoleScope.Organization));

    [Fact]
    public void TeamScopeHasItsOwnRoles() =>
        Assert.Equal(
            [RoleType.TeamLead, RoleType.ScrumMaster, RoleType.ProductOwner,
             RoleType.TeamMember, RoleType.Tester, RoleType.Viewer],
            RolePermissions.AssignableAt(RoleScope.Team));

    /// <summary>
    /// Project scope, including the role no person may hold.
    /// </summary>
    /// <remarks>
    /// <c>AssignableAt</c> answers what the database check constraint permits, and <c>Integration</c>
    /// genuinely belongs there — a connected git installation holds it on the projects its repository
    /// feeds. It must never reach a person, which is a different question with a different answer
    /// below.
    /// </remarks>
    [Fact]
    public void ProjectScopeHasItsOwnRoles() =>
        Assert.Equal(
            [RoleType.ProjectAdmin, RoleType.Contributor, RoleType.Tester, RoleType.Viewer,
             RoleType.Integration],
            RolePermissions.AssignableAt(RoleScope.Project));

    /// <summary>
    /// What a role picker may offer, which excludes the roles held by non-human principals.
    /// </summary>
    /// <remarks>
    /// The distinction exists because adding <c>Integration</c> to the project table would otherwise
    /// have let a project administrator grant it to a colleague. That grants less than
    /// <c>Contributor</c> so it is not an escalation — but it is a role nobody could explain the
    /// presence of, and the endpoints that hand out roles validate against this list.
    /// </remarks>
    [Fact]
    public void RolesHeldOnlyByIntegrationsAreNotGrantableToPeople()
    {
        Assert.Equal(
            [RoleType.ProjectAdmin, RoleType.Contributor, RoleType.Tester, RoleType.Viewer],
            RolePermissions.GrantableToUsersAt(RoleScope.Project));

        // The other two scopes have no such role, so the two lists coincide there.
        foreach (var scope in new[] { RoleScope.Organization, RoleScope.Team })
            Assert.Equal(RolePermissions.AssignableAt(scope), RolePermissions.GrantableToUsersAt(scope));
    }

    /// <summary>
    /// Every role means something somewhere. An enum member valid at no scope is unassignable and
    /// grants nothing — which is what <c>User</c> was, and what made it worth deleting rather than
    /// leaving around to be picked out of a dropdown.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllRoles))]
    public void EveryRoleIsValidSomewhere(RoleType role) =>
        Assert.True(
            Scopes.Any(scope => RolePermissions.IsValidAt(role, scope)),
            $"{role} is valid at no scope, so nothing can ever hold it.");

    /// <summary>
    /// Roles held at two scopes, which is allowed only where the name means the same thing at both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Viewer</c> was the single exception: read-only on a team and read-only on a project are the
    /// same idea applied to different things. <c>Tester</c> is the second, on the same reasoning —
    /// testing a team's work and testing one project's work differ in what they reach, not in what
    /// they mean.
    /// </para>
    /// <para>
    /// The list is deliberately short and deliberately hand-written. Any *other* name appearing at
    /// two scopes is the drift this suite exists to catch, and the fix is a scope-specific name — as
    /// it was for <c>Reader</c>, which meant "organization member" at one scope and "read-only" at
    /// the other two.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllRoles))]
    public void OnlyDeliberatelySharedNamesAreValidAtTwoScopes(RoleType role)
    {
        var scopes = Scopes.Where(scope => RolePermissions.IsValidAt(role, scope)).ToList();

        if (role is RoleType.Viewer or RoleType.Tester)
        {
            Assert.Equal([RoleScope.Team, RoleScope.Project], scopes);
            return;
        }

        Assert.True(scopes.Count == 1,
            $"{role} is valid at {scopes.Count} scopes ({string.Join(", ", scopes)}). Only Viewer " +
            "and Tester may name the same thing at more than one scope; give this role a " +
            "scope-specific name.");
    }

    /// <summary>
    /// Positions are team appointments. One holding at another scope would be silently unappointable
    /// — the transfer and vacate operations only ever look at team scope.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPositions))]
    public void PositionsBelongToTeamsOnly(RoleType position)
    {
        Assert.True(RolePermissions.IsValidAt(position, RoleScope.Team));
        Assert.False(RolePermissions.IsValidAt(position, RoleScope.Organization));
        Assert.False(RolePermissions.IsValidAt(position, RoleScope.Project));
    }

    private static readonly RoleScope[] Scopes =
        [RoleScope.Organization, RoleScope.Team, RoleScope.Project];

    public static TheoryData<RoleType> AllRoles() => new(Enum.GetValues<RoleType>());

    public static TheoryData<RoleType> AllPositions() => new(TeamPositions.All);
}
