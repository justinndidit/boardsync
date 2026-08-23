using BoardSync.Api.Modules.GitSync.Ingest;
using BoardSync.Api.Modules.GitSync.Models;
using BoardSync.Api.Shared.Auth.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.GitSync.Controllers;

/// <summary>
/// Where git hosts deliver events.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anonymous, and that is not a gap.</b> A webhook has no user and can carry no bearer token;
/// what it can do is prove it came from the provider, which is what the signature check does. That
/// check happens before anything is read from the payload or written to the database.
/// </para>
/// <para>
/// The installation is identified by a high-entropy token in the URL rather than by anything in the
/// body, so an attacker who has never seen a real webhook URL cannot even address an installation —
/// and for Azure DevOps, which cannot sign payloads at all, that token is most of the security.
/// </para>
/// </remarks>
[ApiController]
[Route("api/git")]
[AllowAnonymous]
[Produces("application/json")]
public class GitWebhookController : ControllerBase
{
    /// <summary>
    /// Largest payload accepted.
    /// </summary>
    /// <remarks>
    /// A push carrying hundreds of commits is legitimately large, and an unbounded read from an
    /// unauthenticated endpoint is a denial-of-service invitation. GitHub caps its own deliveries at
    /// 25 MB; this is comfortably above anything real and comfortably below anything ruinous.
    /// </remarks>
    private const int MaxPayloadBytes = 8 * 1024 * 1024;

    private readonly IWebhookIngestService _ingest;
    private readonly ILogger<GitWebhookController> _logger;

    public GitWebhookController(IWebhookIngestService ingest, ILogger<GitWebhookController> logger)
    {
        _ingest = ingest;
        _logger = logger;
    }

    /// <summary>Receives a webhook delivery from a git host.</summary>
    /// <param name="provider">Which host — <c>github</c>, <c>gitlab</c>, <c>azuredevops</c>, <c>bitbucket</c>.</param>
    /// <param name="endpointToken">The installation's own high-entropy path segment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// Answers <b>202</b> once the delivery is durably recorded, before it is processed. Anything a
    /// provider can retry into — a slow board update, a long backfill — must not be on this path.
    /// </para>
    /// <para>
    /// <b>2xx for anything durably accepted or deliberately ignored</b>, including a duplicate. A
    /// non-2xx teaches the provider to retry, and on some hosts to disable the hook after enough
    /// failures, so it is reserved for "we did not take this": a bad signature, or an unknown
    /// installation.
    /// </para>
    /// </remarks>
    [HttpPost("{provider}/webhook/{endpointToken}")]
    [NoPermissionRequired(
        "A webhook has no user to authorize. Authenticity is established by the provider's " +
        "signature over the raw body, checked in WebhookIngestService before anything is read or " +
        "written; the installation is identified by the high-entropy token in the route.")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Receive(string provider, string endpointToken, CancellationToken ct)
    {
        if (!Enum.TryParse<GitProvider>(provider, ignoreCase: true, out var parsed))
            return BadRequest();

        var rawBody = await ReadBodyAsync(ct);

        // Null means the body exceeded the cap. Deliberately not "read what fits": a truncated
        // payload would fail its signature check anyway, and reporting the real reason is more use
        // to whoever is debugging it.
        if (rawBody is null)
        {
            _logger.LogWarning("Rejected oversized webhook payload for {Provider}.", parsed);
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var outcome = await _ingest.AcceptAsync(parsed, endpointToken, rawBody, Request.Headers, ct);

        return outcome switch
        {
            IngestOutcome.Accepted => Accepted(),

            // Already have it. A success, and saying so is what stops the provider retrying.
            IngestOutcome.Duplicate => Ok(),

            // One answer for both, so a caller cannot use the difference to discover which endpoint
            // tokens are real.
            IngestOutcome.Unverified or IngestOutcome.UnknownInstallation => Unauthorized(),

            _ => BadRequest()
        };
    }

    /// <summary>
    /// Buffers the request body, or returns null if it is too large.
    /// </summary>
    /// <remarks>
    /// Read once, as raw bytes, and passed through untouched. Signatures are computed over the exact
    /// bytes the provider sent, so anything that deserializes and re-serializes — model binding
    /// included — produces a body that will not verify even when it is authentic. This is why the
    /// action takes no <c>[FromBody]</c> parameter.
    /// </remarks>
    private async Task<byte[]?> ReadBodyAsync(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;

        while ((read = await Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxPayloadBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
