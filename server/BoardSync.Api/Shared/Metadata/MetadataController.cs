using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace BoardSync.Api.Shared.Metadata;

/// <summary>
/// The vocabularies the client renders: roles, permissions, work item types and states, priorities,
/// sprint statuses and link types.
/// </summary>
/// <remarks>
/// <para>
/// One request at boot replaces eight hardcoded lists. Enums reach the client as bare strings, so
/// without this there is no way to know that <c>Critical</c> sorts above <c>Low</c>, that
/// <c>Contributor</c> is a project role and <c>TeamMember</c> is not, or that a <c>Bug</c> may sit
/// under a <c>UserStory</c> but not under a <c>Task</c>.
/// </para>
/// <para>
/// <b>Authenticated, though the content is not tenant data.</b> The design note proposed anonymous;
/// this is a deliberate departure. No screen needs the vocabulary before sign-in, so requiring a
/// token costs nothing, and an open endpoint enumerating every permission in the system is free
/// reconnaissance for anyone deciding what to attack. <c>Cache-Control</c> is therefore
/// <c>private</c> — it is per-user only in the sense that it is behind a login; the body is
/// identical for everybody.
/// </para>
/// </remarks>
[ApiController]
[Route("api/metadata")]
[Authorize]
[Produces("application/json")]
public class MetadataController : ControllerBase
{
    /// <summary>How long a client may reuse the document without revalidating.</summary>
    /// <remarks>
    /// Five minutes, not longer. The content only changes on deploy, but a stale vocabulary shows
    /// stale role names in a picker, and the revalidation this permits is a 304 costing nothing.
    /// </remarks>
    private const int MaxAgeSeconds = 300;

    /// <summary>
    /// Every value the client would otherwise hardcode, with its label and sort order.
    /// </summary>
    /// <remarks>
    /// Send the <c>version</c> back as <c>If-None-Match</c> to get a 304 when nothing has changed.
    /// It is a hash of the content, so it changes exactly when the vocabulary does.
    /// </remarks>
    [HttpGet]
    [NoPermissionRequired(
        "Publishes the system's own vocabulary — role names, permission names, enum labels. It is " +
        "identical for every caller and contains no tenant data, so there is no scope to gate on.")]
    [ProducesResponseType(typeof(ApiResponse<MetadataDocument>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public IActionResult Get()
    {
        var document = MetadataCatalog.Document;
        var etag = $"\"{document.Version}\"";

        Response.Headers.CacheControl = $"private, max-age={MaxAgeSeconds}";
        Response.Headers.ETag = etag;

        // Weak comparison, and against every tag offered: a proxy may legitimately hand back W/"…"
        // or a list, and treating either as a miss would defeat the point of sending the ETag.
        if (Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var offered)
            && offered.Any(tag => tag is not null && Matches(tag, document.Version)))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(new ApiResponse<MetadataDocument>(true, "Metadata retrieved.", document));
    }

    private static bool Matches(string offered, string version) =>
        offered.Split(',')
            .Select(tag => tag.Trim().TrimStart('W', '/').Trim('"'))
            .Any(tag => tag == version || tag == "*");
}
