using BoardSync.Api.Modules.Search.Domain;

namespace BoardSync.Api.Tests;

/// <summary>
/// Whether a search term is a work item reference.
/// </summary>
/// <remarks>
/// Getting this wrong does not fail — it quietly returns an unrelated card — so the interesting
/// cases are the ones that should <i>not</i> parse.
/// </remarks>
public class SearchTermTests
{
    [Theory]
    [InlineData("142", 142)]
    [InlineData("BS-142", 142)]
    [InlineData("bs 142", 142)]
    [InlineData("  PAY-7  ", 7)]
    [InlineData("release-2024", 2024)]
    public void AReferenceYieldsItsNumber(string term, int expected) =>
        Assert.Equal(expected, SearchTerm.ReferenceNumber(term));

    /// <summary>
    /// A key run together with its number keeps the whole number.
    /// </summary>
    /// <remarks>
    /// A greedy prefix took <c>S14</c> and left <c>2</c>, so <c>BS142</c> found work item 2 — a
    /// wrong answer that looks like a right one, which is the worst kind for a search box.
    /// </remarks>
    [Fact]
    public void AKeyRunTogetherWithItsNumberKeepsTheNumber() =>
        Assert.Equal(142, SearchTerm.ReferenceNumber("BS142"));

    [Theory]
    [InlineData("ordinary work")]
    [InlineData("billing")]
    [InlineData("")]
    [InlineData("BS-")]
    [InlineData("1234567890123")]
    public void ProseIsNotAReference(string term) =>
        Assert.Null(SearchTerm.ReferenceNumber(term));

    /// <summary>
    /// The bug this was extracted for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An unbounded prefix read any long alphanumeric string ending in a digit as a reference. A
    /// hex string ending in <c>1</c> parsed as "work item 1", so a search for a term that matches
    /// nothing returned the first work item in every project the caller could read.
    /// </para>
    /// <para>
    /// It surfaced as <c>SearchTests.AnUnmatchedTermReturnsNothing</c> failing about six runs in a
    /// hundred — the rate at which a random GUID ends in the digit <c>1</c>. A flake that is a real
    /// defect, and the reason this case is pinned with the literal that produced it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("zzz4f2a91b0c3d4e5f6a7b8c9d0e1f2a3b1")]
    [InlineData("zzz00000000000000000000000000000001")]
    [InlineData("abcdefghijklmnopqrstuvwxyz9")]
    public void ALongAlphanumericTermIsNotAReference(string term) =>
        Assert.Null(SearchTerm.ReferenceNumber(term));

    /// <summary>Every shape the failing test could generate, not just the one that failed.</summary>
    [Fact]
    public void NoGuidShapedTermParsesAsAReference()
    {
        for (var i = 0; i < 2_000; i++)
        {
            var term = $"zzz{Guid.NewGuid():N}";

            Assert.Null(SearchTerm.ReferenceNumber(term));
        }
    }
}
