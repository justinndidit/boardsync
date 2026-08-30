using System.Net;

namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That a proposal can be found again.
/// </summary>
/// <remarks>
/// <para>
/// Four routes existed and none of them listed anything, so a proposal was reachable only by its id
/// — which nothing recorded. Navigate away from the page that made it and it was gone.
/// </para>
/// <para>
/// That mattered beyond a missing screen. <c>docs/adr-002-proposals.md</c> keeps proposals after the
/// decision deliberately: "every accept and reject is a labelled example of what this team considers
/// a good breakdown". The record was being written and could not be read.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class ProposalListingTests(BoardSyncApiFactory factory)
{
    [Fact]
    public async Task ProposalsAreListedNewestFirst()
    {
        var workspace = await Workspace.CreateAsync(factory);

        // No model is configured in the test environment, so each of these fails fast with a
        // reason — which is exactly the case that most needs listing, because a failed proposal
        // has no draft to navigate back into.
        foreach (var n in new[] { "first document", "second document" })
        {
            await workspace.Owner.PostRaw(
                $"/api/projects/{workspace.ProjectId}/intelligence/decompose",
                new { content = new string('x', 200) + n, teamId = workspace.TeamId });
        }

        var listed = await workspace.Owner.Get<Paged<Summary>>(
            $"/api/projects/{workspace.ProjectId}/intelligence/proposals?page=1&pageSize=20");

        Assert.Equal(2, listed.TotalCount);

        Assert.True(
            listed.Items[0].CreatedAt >= listed.Items[1].CreatedAt,
            "Proposals should be listed newest first.");
    }

    [Fact]
    public async Task AListingCarriesEnoughToTellProposalsApart()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var body = new string('y', 200) + " billing rewrite";

        await workspace.Owner.PostRaw(
            $"/api/projects/{workspace.ProjectId}/intelligence/decompose",
            new { content = body, teamId = workspace.TeamId });

        var listed = await workspace.Owner.Get<Paged<Summary>>(
            $"/api/projects/{workspace.ProjectId}/intelligence/proposals?page=1&pageSize=20");

        var only = listed.Items.Single();

        // A status and a reason, so a failed proposal explains itself in the list rather than
        // needing to be opened.
        Assert.False(string.IsNullOrWhiteSpace(only.Status));

        // And a preview of the source, because two proposals from one project are otherwise a pair
        // of timestamps.
        Assert.False(string.IsNullOrWhiteSpace(only.Preview));
        Assert.StartsWith("yyy", only.Preview);
    }

    [Fact]
    public async Task ListingIsScopedToTheProject()
    {
        var one = await Workspace.CreateAsync(factory);
        var two = await Workspace.CreateAsync(factory);

        await one.Owner.PostRaw(
            $"/api/projects/{one.ProjectId}/intelligence/decompose",
            new { content = new string('z', 200), teamId = one.TeamId });

        var theirs = await two.Owner.Get<Paged<Summary>>(
            $"/api/projects/{two.ProjectId}/intelligence/proposals?page=1&pageSize=20");

        Assert.Empty(theirs.Items);
    }

    [Fact]
    public async Task ListingNeedsThePermissionThatCreatesWork()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var outsider = await workspace.AddOrganizationMemberAsync(factory);

        /*
         * `workitem:write`, the same permission decomposing needs. A decomposition spends the
         * organization's allowance, so reading what has been spent on belongs with the permission
         * to spend it — and an organization member with no standing in the project has neither.
         */
        var response = await outsider.GetRaw(
            $"/api/projects/{workspace.ProjectId}/intelligence/proposals?page=1&pageSize=20");

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"Expected the listing to be refused, got {response.StatusCode}.");
    }

    private sealed record Summary(
        Guid Id, string Status, string? Detail, int TokensSpent,
        int? NodeCount, string Preview, DateTime CreatedAt);

    private sealed record Paged<T>(List<T> Items, int TotalCount, int Page, int PageSize);
}
