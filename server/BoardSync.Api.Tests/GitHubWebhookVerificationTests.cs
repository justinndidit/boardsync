using System.Security.Cryptography;
using System.Text;
using BoardSync.Api.Modules.GitSync.Models;
using BoardSync.Api.Modules.GitSync.Providers;
using Microsoft.AspNetCore.Http;

namespace BoardSync.Api.Tests;

/// <summary>
/// That a webhook delivery is accepted only when it really came from GitHub.
/// </summary>
/// <remarks>
/// <para>
/// The webhook endpoint is anonymous — a delivery carries no user and can carry no bearer token — so
/// this signature check is the <em>entire</em> authentication story for it. Everything the git
/// integration is trusted to do downstream rests on it, which makes these the highest-consequence
/// tests in the suite despite being some of the smallest.
/// </para>
/// <para>
/// Written as unit tests against the provider rather than through the API because the interesting
/// cases are malformed inputs that a well-behaved HTTP client makes awkward to send.
/// </para>
/// </remarks>
public class GitHubWebhookVerificationTests
{
    private const string Secret = "a-webhook-secret-for-this-installation";

    private static readonly GitHubProvider Provider = new();

    private static byte[] Body(string json = """{"zen":"Design for failure."}""") =>
        Encoding.UTF8.GetBytes(json);

    /// <summary>Signs a body the way GitHub does, so the happy path is exercised for real.</summary>
    private static string Sign(byte[] body, string secret = Secret) =>
        "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    private static IHeaderDictionary Headers(string? signature) =>
        signature is null
            ? new HeaderDictionary()
            : new HeaderDictionary { ["X-Hub-Signature-256"] = signature };

    // ── The one case that must pass ───────────────────────────────────────────

    [Fact]
    public void AGenuinelySignedDeliveryIsAccepted()
    {
        var body = Body();
        Assert.True(Provider.Verify(body, Headers(Sign(body)), Secret));
    }

    // ── Everything that must not ──────────────────────────────────────────────

    /// <summary>
    /// A tampered body fails even though the signature is otherwise well-formed.
    /// </summary>
    /// <remarks>
    /// The property that separates HMAC from a shared secret, and the reason GitHub deliveries can be
    /// trusted more than Azure DevOps ones: the signature covers the payload, so altering a commit
    /// message or a repository id in flight invalidates it.
    /// </remarks>
    [Fact]
    public void ATamperedBodyIsRejected()
    {
        var signature = Sign(Body("""{"repository":{"id":1}}"""));

        Assert.False(Provider.Verify(Body("""{"repository":{"id":2}}"""), Headers(signature), Secret));
    }

    /// <summary>A signature from a different installation's secret is rejected.</summary>
    /// <remarks>
    /// Why secrets are per installation: one customer's leaked secret must not let them forge another
    /// customer's deliveries.
    /// </remarks>
    [Fact]
    public void ASignatureFromAnotherSecretIsRejected()
    {
        var body = Body();
        Assert.False(Provider.Verify(body, Headers(Sign(body, "someone-elses-secret")), Secret));
    }

    [Fact]
    public void AMissingSignatureIsRejected() =>
        Assert.False(Provider.Verify(Body(), Headers(null), Secret));

    /// <summary>
    /// Malformed signatures are rejected rather than throwing.
    /// </summary>
    /// <remarks>
    /// This endpoint is reachable by anyone who learns the URL, so a parse that threw would be a
    /// denial-of-service lever — and an unhandled exception is a 500, which tells the sender rather
    /// more than a flat rejection does.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("sha256=")]
    [InlineData("sha256=nothex!!")]
    [InlineData("sha1=aabbcc")]                                   // right shape, wrong algorithm
    [InlineData("sha256=aabb")]                                   // valid hex, too short
    [InlineData("sha256=" + "ab")]                                // ditto
    [InlineData("SHA256=0000000000000000000000000000000000000000000000000000000000000000")]
    public void MalformedSignaturesAreRejectedWithoutThrowing(string signature) =>
        Assert.False(Provider.Verify(Body(), Headers(signature), Secret));

    /// <summary>A correctly-shaped signature of the right length but wrong value is rejected.</summary>
    /// <remarks>
    /// The case that would pass if the comparison were ever loosened to a length or prefix check.
    /// </remarks>
    [Fact]
    public void AWellFormedButWrongSignatureIsRejected() =>
        Assert.False(Provider.Verify(
            Body(),
            Headers("sha256=" + new string('0', 64)),
            Secret));

    /// <summary>An empty body still has to be signed.</summary>
    [Fact]
    public void AnEmptyBodyStillRequiresAValidSignature()
    {
        Assert.False(Provider.Verify([], Headers("sha256=" + new string('0', 64)), Secret));
        Assert.True(Provider.Verify([], Headers(Sign([])), Secret));
    }

    // ── Header reading ────────────────────────────────────────────────────────

    [Fact]
    public void EventNameAndDeliveryIdAreReadFromTheirHeaders()
    {
        var headers = new HeaderDictionary
        {
            ["X-GitHub-Event"] = "push",
            ["X-GitHub-Delivery"] = "72d3162e-cc78-11e3-81ab-4c9367dc0958"
        };

        Assert.Equal("push", Provider.EventNameOf(headers));
        Assert.Equal("72d3162e-cc78-11e3-81ab-4c9367dc0958", Provider.DeliveryIdOf(headers));
    }

    [Fact]
    public void MissingHeadersReadAsNull()
    {
        Assert.Null(Provider.EventNameOf(new HeaderDictionary()));
        Assert.Null(Provider.DeliveryIdOf(new HeaderDictionary()));
    }

    [Fact]
    public void GitHubDeliveriesAreRecordedAsHmacVerified() =>
        Assert.Equal(WebhookVerification.HmacSha256, Provider.Verification);
}
