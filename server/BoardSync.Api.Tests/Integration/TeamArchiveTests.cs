using System.Net;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// End-to-end coverage for the team archive/unarchive lifecycle: archiving a team, listing archived
/// teams, restoring a team, and the regression guarantee that the active-team endpoint keeps
/// returning only active teams.
/// </summary>
[Collection(ApiCollection.Name)]
public class TeamArchiveTests(BoardSyncApiFactory factory)
{
    // ── Archive ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ActiveTeamCanBeArchived()
    {
        var (owner, orgId, teamId) = await CreateTeamWithoutProjectAsync(factory);

        await owner.Delete($"/api/teams/{teamId}");

        var teams = await owner.Get<Paged<Team>>(
            $"/api/orgs/{orgId}/teams?page=1&pageSize=20");

        Assert.Empty(teams.Items);
    }

    [Fact]
    public async Task ArchivedTeamAppearsInArchivedList()
    {
        var (owner, orgId, teamId) = await CreateTeamWithoutProjectAsync(factory);

        await owner.Delete($"/api/teams/{teamId}");

        var archived = await owner.Get<Paged<Team>>(
            $"/api/orgs/{orgId}/teams/archived?page=1&pageSize=20");

        var team = Assert.Single(archived.Items);
        Assert.Equal(teamId, team.Id);
        Assert.False(team.IsActive);
    }

    [Fact]
    public async Task TeamWithActiveProjectsCannotBeArchived()
    {
        var workspace = await Workspace.CreateAsync(factory);

        // The workspace has a project assigned to the team, so archiving must fail.
        var deleteResponse = await workspace.Owner.SendRaw(
            new HttpRequestMessage(HttpMethod.Delete, $"/api/teams/{workspace.TeamId}"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task AlreadyArchivedTeamCannotBeArchivedAgain()
    {
        var (owner, orgId, teamId) = await CreateTeamWithoutProjectAsync(factory);

        await owner.Delete($"/api/teams/{teamId}");

        // Second archive attempt should 404 because the team is no longer active.
        var response = await owner.SendRaw(
            new HttpRequestMessage(HttpMethod.Delete, $"/api/teams/{teamId}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnauthorizedUserCannotArchiveTeam()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var outsider = await TestApi.RegisterAsync(factory);

        var response = await outsider.SendRaw(
            new HttpRequestMessage(HttpMethod.Delete, $"/api/teams/{workspace.TeamId}"));

        // Returns 404 (not 403) to avoid leaking resource existence.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Get Archived ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ArchivedTeamsAreReturned()
    {
        var (owner, orgId, teamId) = await CreateTeamWithoutProjectAsync(factory);
        await owner.Delete($"/api/teams/{teamId}");

        var archived = await owner.Get<Paged<Team>>(
            $"/api/orgs/{orgId}/teams/archived?page=1&pageSize=20");

        Assert.Single(archived.Items);
        Assert.All(archived.Items, t => Assert.False(t.IsActive));
    }

    [Fact]
    public async Task ActiveTeamsAreNotInArchivedList()
    {
        var (owner, orgId, teamId) = await CreateTeamWithoutProjectAsync(factory);

        var archived = await owner.Get<Paged<Team>>(
            $"/api/orgs/{orgId}/teams/archived?page=1&pageSize=20");

        Assert.Empty(archived.Items);
    }

    [Fact]
    public async Task TeamsFromAnotherOrgAreNotInArchivedList()
    {
        var (owner1, orgId1, teamId1) = await CreateTeamWithoutProjectAsync(factory);
        var (owner2, orgId2, teamId2) = await CreateTeamWithoutProjectAsync(factory);

        // Archive team from org1
        await owner1.Delete($"/api/teams/{teamId1}");

        // Check org2's archived list — should not contain org1's team
        var archived = await owner2.Get<Paged<Team>>(
            $"/api/orgs/{orgId2}/teams/archived?page=1&pageSize=20");

        Assert.Empty(archived.Items);
    }

    [Fact]
    public async Task ArchivedTeamsArePaged()
    {
        var (owner, orgId, teamId) = await CreateTeamWithoutProjectAsync(factory);
        await owner.Delete($"/api/teams/{teamId}");

        var archived = await owner.Get<Paged<Team>>(
            $"/api/orgs/{orgId}/teams/archived?page=1&pageSize=10");

        Assert.Equal(1, archived.Page);
        Assert.True(archived.TotalCount >= 1);
        Assert.True(archived.TotalPages >= 1);
        Assert.False(archived.HasPreviousPage);
    }

    [Fact]
    public async Task UnauthorizedUserCannotGetArchivedTeams()
    {
        var (owner, orgId, teamId) = await CreateTeamWithoutProjectAsync(factory);
        var outsider = await TestApi.RegisterAsync(factory);

        var response = await outsider.GetRaw(
            $"/api/orgs/{orgId}/teams/archived?page=1&pageSize=20");

        // Returns 404 (not 403) to avoid leaking resource existence.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Unarchive ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ArchivedTeamCanBeActivated()
    {
        var (owner, orgId, teamId) = await CreateTeamWithoutProjectAsync(factory);
        await owner.Delete($"/api/teams/{teamId}");

        await owner.Post($"/api/teams/{teamId}/activate", new {});

        var teams = await owner.Get<Paged<Team>>(
            $"/api/orgs/{orgId}/teams?page=1&pageSize=20");

        var team = Assert.Single(teams.Items);
        Assert.Equal(teamId, team.Id);
        Assert.True(team.IsActive);
    }

    [Fact]
    public async Task ActivatedTeamNoLongerInArchivedList()
    {
        var (owner, orgId, teamId) = await CreateTeamWithoutProjectAsync(factory);
        await owner.Delete($"/api/teams/{teamId}");
        await owner.Post($"/api/teams/{teamId}/activate", new {});

        var archived = await owner.Get<Paged<Team>>(
            $"/api/orgs/{orgId}/teams/archived?page=1&pageSize=20");

        Assert.Empty(archived.Items);
    }

    [Fact]
    public async Task NonexistentTeamActivationReturnsNotFound()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var fakeId = Guid.NewGuid();

        var response = await workspace.Owner.SendRaw(
            new HttpRequestMessage(HttpMethod.Post, $"/api/teams/{fakeId}/activate"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnauthorizedUserCannotActivateTeam()
    {
        var (owner, orgId, teamId) = await CreateTeamWithoutProjectAsync(factory);
        await owner.Delete($"/api/teams/{teamId}");

        var outsider = await TestApi.RegisterAsync(factory);
        var response = await outsider.SendRaw(
            new HttpRequestMessage(HttpMethod.Post, $"/api/teams/{teamId}/activate"));

        // Returns 404 (not 403) to avoid leaking resource existence.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ActivatingAlreadyActiveTeamIsNoOp()
    {
        var (owner, orgId, teamId) = await CreateTeamWithoutProjectAsync(factory);

        // Activating an already-active team should succeed without error.
        await owner.Post($"/api/teams/{teamId}/activate", new {});

        var teams = await owner.Get<Paged<Team>>(
            $"/api/orgs/{orgId}/teams?page=1&pageSize=20");

        var team = Assert.Single(teams.Items);
        Assert.Equal(teamId, team.Id);
        Assert.True(team.IsActive);
    }

    // ── Full lifecycle ───────────────────────────────────────────────────────

    [Fact]
    public async Task FullArchiveUnarchiveLifecycle()
    {
        var (owner, orgId, teamId) = await CreateTeamWithoutProjectAsync(factory);

        // Start active
        var activeTeams = await owner.Get<Paged<Team>>(
            $"/api/orgs/{orgId}/teams?page=1&pageSize=20");
        Assert.Single(activeTeams.Items);
        Assert.True(activeTeams.Items[0].IsActive);

        // Archive
        await owner.Delete($"/api/teams/{teamId}");

        // Now in archived list
        activeTeams = await owner.Get<Paged<Team>>(
            $"/api/orgs/{orgId}/teams?page=1&pageSize=20");
        Assert.Empty(activeTeams.Items);

        var archivedTeams = await owner.Get<Paged<Team>>(
            $"/api/orgs/{orgId}/teams/archived?page=1&pageSize=20");
        Assert.Single(archivedTeams.Items);
        Assert.False(archivedTeams.Items[0].IsActive);

        // Unarchive
        await owner.Post($"/api/teams/{teamId}/activate", new {});

        // Back in active list
        activeTeams = await owner.Get<Paged<Team>>(
            $"/api/orgs/{orgId}/teams?page=1&pageSize=20");
        Assert.Single(activeTeams.Items);
        Assert.True(activeTeams.Items[0].IsActive);

        archivedTeams = await owner.Get<Paged<Team>>(
            $"/api/orgs/{orgId}/teams/archived?page=1&pageSize=20");
        Assert.Empty(archivedTeams.Items);
    }

    // ── Active teams regression ─────────────────────────────────────────────

    [Fact]
    public async Task ActiveTeamsEndpointStillReturnsOnlyActiveTeams()
    {
        var (owner, orgId, teamId) = await CreateTeamWithoutProjectAsync(factory);
        await owner.Delete($"/api/teams/{teamId}");

        var teams = await owner.Get<Paged<Team>>(
            $"/api/orgs/{orgId}/teams?page=1&pageSize=20");

        Assert.All(teams.Items, t => Assert.True(t.IsActive));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Creates an org + team (no project) so the team can be archived.</summary>
    private static async Task<(TestApi Owner, Guid OrganizationId, Guid TeamId)> CreateTeamWithoutProjectAsync(BoardSyncApiFactory factory)
    {
        var owner = await TestApi.RegisterAsync(factory);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var org = await owner.Post<Created>("/api/orgs", new { name = $"Org {suffix}" });
        var team = await owner.Post<Created>($"/api/orgs/{org.Id}/teams", new { name = $"Team {suffix}" });

        return (owner, org.Id, team.Id);
    }

    private sealed record Team(
        Guid Id,
        Guid OrganizationId,
        string Name,
        string Description,
        bool IsActive,
        int MemberCount,
        DateTime CreatedAt);

    private sealed record Paged<T>(
        List<T> Items,
        int TotalCount,
        int Page,
        int PageSize,
        int TotalPages,
        bool HasNextPage,
        bool HasPreviousPage);

    private sealed record Created(Guid Id);
}
