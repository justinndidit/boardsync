namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// The shape an organization's project list actually sends.
/// </summary>
/// <remarks>
/// <para>
/// The projects page renders cards from this, and a card shows the owning team and the creation
/// date. The endpoint used to send a three-field summary — id, slug, name — so the team rendered
/// blank and <c>new Date(undefined).toLocaleDateString()</c> put "Invalid Date" on screen.
/// </para>
/// <para>
/// Nothing failed to compile, in either language: the client's type had always declared the full
/// shape, and TypeScript believes a declaration about data it never sees. Only a test that reads
/// the payload can catch this, which is why it asserts on fields rather than on a deserialized
/// object — a record with a missing property simply comes back null.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class ProjectListingTests(BoardSyncApiFactory factory)
{
    [Fact]
    public async Task AListedProjectCarriesEverythingACardRenders()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var page = await workspace.Owner.Get<Paged<Listed>>(
            $"/api/orgs/{workspace.OrganizationId}/projects?page=1&pageSize=20");

        var project = Assert.Single(
            page.Items, p => p.Id == workspace.ProjectId);

        Assert.False(string.IsNullOrWhiteSpace(project.Name));
        Assert.False(string.IsNullOrWhiteSpace(project.Slug));

        // The three the card reads that the summary did not carry.
        Assert.False(string.IsNullOrWhiteSpace(project.Key));
        Assert.False(string.IsNullOrWhiteSpace(project.AssignedTeamName));
        Assert.NotEqual(default, project.CreatedAt);

        Assert.Equal(workspace.TeamId, project.AssignedTeamId);
        Assert.Equal(workspace.OrganizationId, project.OrganizationId);
    }

    /// <summary>The paging envelope carries what the pager reads.</summary>
    [Fact]
    public async Task TheListingIsPaged()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var page = await workspace.Owner.Get<Paged<Listed>>(
            $"/api/orgs/{workspace.OrganizationId}/projects?page=1&pageSize=20");

        Assert.Equal(1, page.Page);
        Assert.True(page.TotalCount >= 1);
        Assert.True(page.TotalPages >= 1);
        Assert.False(page.HasPreviousPage);
    }

    private sealed record Listed(
        Guid Id,
        Guid OrganizationId,
        string Slug,
        string Key,
        string Name,
        string Description,
        bool IsActive,
        Guid AssignedTeamId,
        string AssignedTeamName,
        bool AllowSelfCertification,
        DateTime CreatedAt);

    private sealed record Paged<T>(
        List<T> Items,
        int TotalCount,
        int Page,
        int PageSize,
        int TotalPages,
        bool HasNextPage,
        bool HasPreviousPage);
}
