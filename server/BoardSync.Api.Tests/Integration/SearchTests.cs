namespace BoardSync.Api.Tests.Integration;

/// <summary>
/// That search finds what somebody meant, and ranks it.
/// </summary>
/// <remarks>
/// <para>
/// It matched <c>LOWER(title) LIKE '%term%'</c>, which no index can serve, ordered by creation date,
/// and did not look at the reference at all — so <c>BS-142</c>, the single most likely thing anybody
/// types into a search box here, returned nothing.
/// </para>
/// <para>
/// Driven through the endpoint rather than the repository: the ranking is Postgres', and asserting
/// it against anything other than the real database would be asserting a guess.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public class SearchTests(BoardSyncApiFactory factory)
{
    private sealed record Hit(Guid Id, string Name, string? Slug);

    private sealed record Results(
        List<Hit> Organizations, List<Hit> Projects, List<Hit> Members, List<Hit> WorkItems);

    private sealed record WorkItemView(Guid Id, string Reference, string Title);

    private static Task<Results> SearchAsync(TestApi api, string term) =>
        api.Get<Results>($"/api/search?q={Uri.EscapeDataString(term)}");

    /// <summary>
    /// A work item is findable by the reference people actually say out loud.
    /// </summary>
    /// <remarks>
    /// The gap that mattered most: every other surface now shows <c>BS-142</c> — the card, the
    /// list, the backlog, the branch hint — and the one box you would paste it into ignored it.
    /// </remarks>
    [Fact]
    public async Task AWorkItemIsFoundByItsReference()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var itemId = await workspace.AddWorkItemAsync("something with no matching words");

        var reference = (await workspace.Owner.Get<WorkItemView>(
            $"/api/workitems/{itemId}")).Reference;

        var byFullReference = await SearchAsync(workspace.Owner, reference);
        Assert.Contains(byFullReference.WorkItems, w => w.Id == itemId);

        // And by the number alone, which is what people type when the key is obvious.
        var number = reference.Split('-')[^1];

        Assert.Contains(
            (await SearchAsync(workspace.Owner, number)).WorkItems,
            w => w.Id == itemId);
    }

    /// <summary>
    /// An exact reference outranks a word match, because somebody who typed one knows what they
    /// want and a relevance score cannot outrank knowing.
    /// </summary>
    [Fact]
    public async Task AnExactReferenceOutranksAWordMatch()
    {
        var workspace = await Workspace.CreateAsync(factory);

        var wanted = await workspace.AddWorkItemAsync("unrelated subject entirely");

        var reference = (await workspace.Owner.Get<WorkItemView>(
            $"/api/workitems/{wanted}")).Reference;

        var number = reference.Split('-')[^1];

        // A decoy whose *title* contains the number as a word.
        await workspace.AddWorkItemAsync($"decoy mentioning {number} in its title");

        var results = await SearchAsync(workspace.Owner, reference);

        Assert.Equal(wanted, results.WorkItems[0].Id);
    }

    /// <summary>
    /// Full text, not substring: the index is over words, and stemming means the word somebody
    /// remembers finds the word they wrote.
    /// </summary>
    [Fact]
    public async Task SearchStemsWords()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var itemId = await workspace.AddWorkItemAsync("Migrating the payment gateway");

        Assert.Contains(
            (await SearchAsync(workspace.Owner, "migrate")).WorkItems,
            w => w.Id == itemId);
    }

    /// <summary>
    /// Results appear while somebody is still typing — the last word is a prefix.
    /// </summary>
    [Fact]
    public async Task ThePartialLastWordStillMatches()
    {
        var workspace = await Workspace.CreateAsync(factory);
        var itemId = await workspace.AddWorkItemAsync("Deduplicate webhook deliveries");

        Assert.Contains(
            (await SearchAsync(workspace.Owner, "webho")).WorkItems,
            w => w.Id == itemId);
    }

    /// <summary>
    /// A term matching nothing returns nothing rather than failing.
    /// </summary>
    /// <remarks>
    /// Worth asserting because the query is now built from user text: a term that produces an empty
    /// or malformed tsquery must not reach Postgres as a syntax error.
    /// </remarks>
    [Fact]
    public async Task AnUnmatchedTermReturnsNothing()
    {
        var workspace = await Workspace.CreateAsync(factory);
        await workspace.AddWorkItemAsync("ordinary work");

        var results = await SearchAsync(
            workspace.Owner, $"zzz{Guid.NewGuid():N}");

        Assert.Empty(results.WorkItems);
    }
}
