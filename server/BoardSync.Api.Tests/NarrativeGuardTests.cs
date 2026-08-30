using BoardSync.Api.Modules.Intelligence.Domain;

namespace BoardSync.Api.Tests;

/// <summary>
/// That a narrative cannot state a figure the report does not contain.
/// </summary>
/// <remarks>
/// <para>
/// The whole argument of <c>build_context.md</c> §8.3 is that a model asked to both compute and
/// narrate returns plausible numbers nobody downstream can distinguish from computed ones.
/// Splitting the modules answers half of it. This is the other half, and it is the half that can be
/// tested — whether the prose reads well is a judgement, whether it invented a number is a fact.
/// </para>
/// <para>
/// Pure, so these run without an API key. That is deliberate: the guard has to be trustworthy in
/// exactly the environments where the model is not reachable.
/// </para>
/// </remarks>
public class NarrativeGuardTests
{
    private static readonly double[] Report = [40, 34, 2.5, 91, 0];

    [Fact]
    public void ANarrativeCitingOnlyRealFiguresIsGrounded() =>
        Assert.True(NarrativeGuard.IsGrounded(
            "The team completed 34 of 40 points. Median wait for QA was 2.5 hours.",
            Report));

    /// <summary>
    /// The failure this exists to catch: a number that sounds like the others and is not one.
    /// </summary>
    [Fact]
    public void AnInventedFigureIsRejected()
    {
        var findings = NarrativeGuard.UnsupportedClaims(
            "The team completed 34 of 40 points, up from 28 last sprint.",
            Report);

        var finding = Assert.Single(findings);

        Assert.Equal("28", finding.Figure);
        Assert.Contains("28", finding.Sentence);
    }

    /// <summary>
    /// The sentence comes back with the figure, because "a number is wrong" is not actionable and
    /// "this sentence is wrong" is.
    /// </summary>
    [Fact]
    public void TheOffendingSentenceIsReported()
    {
        var findings = NarrativeGuard.UnsupportedClaims(
            "Everything is fine. Velocity reached 77 points.",
            Report);

        Assert.Equal(
            "Velocity reached 77 points.",
            Assert.Single(findings).Sentence);
    }

    /// <summary>
    /// Rounding is allowed, within a tolerance tight enough that a different number cannot pass as
    /// one. Rejecting "about 3 hours" for a report holding 2.5 would push the prose into reciting
    /// decimals at people, which is not what a narrative is for.
    /// </summary>
    [Theory]
    [InlineData("Work waited about 2.5 hours.", true)]
    [InlineData("Work waited about 2.55 hours.", true)]
    [InlineData("Work waited about 3 hours.", false)]
    public void RoundingIsToleratedButSubstitutionIsNot(
        string narrative, bool grounded) =>
        Assert.Equal(grounded, NarrativeGuard.IsGrounded(narrative, Report));

    /// <summary>
    /// A ratio in the report read as a percentage in the prose is the same figure.
    /// </summary>
    [Fact]
    public void APercentageMatchesTheRatioBehindIt() =>
        Assert.True(NarrativeGuard.IsGrounded(
            "Completion was 85%.",
            [0.85]));

    /// <summary>
    /// Thousands separators are how people write large numbers; the guard reads them the same way.
    /// </summary>
    [Fact]
    public void FormattedNumbersAreUnderstood() =>
        Assert.True(NarrativeGuard.IsGrounded(
            "The backlog holds 1,200 points.",
            [1200]));

    /// <summary>
    /// Zero is a figure like any other, and the one most likely to be asserted casually — "nothing
    /// was closed" is a claim about the data.
    /// </summary>
    [Fact]
    public void ZeroIsCheckedRatherThanAssumed()
    {
        Assert.True(NarrativeGuard.IsGrounded(
            "0 items were closed.", Report));

        Assert.False(NarrativeGuard.IsGrounded(
            "0 items were closed.", [40]));
    }

    /// <summary>
    /// Prose with no figures passes. It is also the loophole worth naming: a narrative can be
    /// wrong without citing anything, and this catches none of that.
    /// </summary>
    [Fact]
    public void ProseWithoutFiguresIsGrounded() =>
        Assert.True(NarrativeGuard.IsGrounded(
            "The team is doing well and morale seems high.",
            Report));

    [Fact]
    public void AnEmptyNarrativeIsGrounded() =>
        Assert.True(NarrativeGuard.IsGrounded("", Report));

    /// <summary>
    /// Every unsupported figure is reported, not just the first — a caller deciding whether to ship
    /// the narrative needs the whole picture in one pass.
    /// </summary>
    [Fact]
    public void EveryUnsupportedFigureIsReported()
    {
        var findings = NarrativeGuard.UnsupportedClaims(
            "Velocity hit 77. Cycle time fell to 9 hours.",
            Report);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Figure == "77");
        Assert.Contains(findings, f => f.Figure == "9");
    }

    /// <summary>
    /// A work item number is an identifier, not a quantity.
    /// </summary>
    /// <remarks>
    /// Reading the 11 in PAY-11 as a claimed figure would flag every correctly-cited item as an
    /// invention, which is the failure mode that makes a grounding check get switched off.
    /// </remarks>
    [Fact]
    public void AReferenceIsNotReadAsAFigure()
    {
        var findings = NarrativeGuard.UnsupportedClaims(
            "PAY-11 and PAY-4207 shipped.", [40, 34]);

        Assert.Empty(findings);
    }

    /// <summary>Masking the identifier does not mask a real figure beside it.</summary>
    [Fact]
    public void AFigureBesideAReferenceIsStillChecked()
    {
        var findings = NarrativeGuard.UnsupportedClaims(
            "PAY-11 shipped, taking velocity to 77.", [40, 34]);

        Assert.Equal("77", Assert.Single(findings).Figure);
    }

    /// <summary>An item nobody handed over is unsupported, however plausible it reads.</summary>
    [Fact]
    public void AnInventedReferenceIsFound()
    {
        var findings = NarrativeGuard.UnsupportedReferences(
            "PAY-11 and PAY-91 shipped.", ["PAY-11", "PAY-12"]);

        Assert.Equal("PAY-91", Assert.Single(findings).Figure);
    }

    /// <summary>Case is not identity — a key typed in lower case is the same work item.</summary>
    [Fact]
    public void ReferencesMatchRegardlessOfCase()
    {
        Assert.Empty(NarrativeGuard.UnsupportedReferences(
            "pay-11 shipped.", ["PAY-11"]));
    }

    /// <summary>
    /// Prose with no references passes, rather than everything matching nothing.
    /// </summary>
    [Fact]
    public void ProseWithoutReferencesIsSupported()
    {
        Assert.Empty(NarrativeGuard.UnsupportedReferences(
            "The sprint met its goal.", []));
    }
}
