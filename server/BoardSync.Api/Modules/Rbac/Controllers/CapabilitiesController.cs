using System.ComponentModel.DataAnnotations;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Modules.Rbac.Services.Interfaces;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.Rbac.Controllers;

/// <summary>What the calling user may do, at scopes they name.</summary>
/// <remarks>
/// <para>
/// Without this a client has to reimplement <see cref="Services.AccessEvaluator"/> in TypeScript:
/// three inheritance routes, the team → project edge with its Scrum Master and Product Owner
/// exception, and OrgAdmin's reach into everything. That reimplementation drifts, and it drifts
/// <em>permissive</em> — a button that 403s gets reported, a button wrongly hidden does not.
/// </para>
/// <para>
/// It also cannot be learned by probing, because a denial is a 404 wherever the caller cannot see
/// the scope, so the client cannot tell "gone" from "forbidden".
/// </para>
/// </remarks>
[ApiController]
[Route("api/me/capabilities")]
[Authorize]
[Produces("application/json")]
public class CapabilitiesController : ControllerBase
{
    /// <summary>
    /// Most a batch may ask about at once.
    /// </summary>
    /// <remarks>
    /// Each scope past the first costs at most one primary-key lookup — the snapshot is resolved
    /// once and memoized — so the cap is about bounding a request someone builds by accident, not
    /// about the work being expensive.
    /// </remarks>
    public const int MaxBatchSize = 50;

    private readonly IRbacService _rbac;
    private readonly ICurrentUserContext _currentUser;

    public CapabilitiesController(IRbacService rbac, ICurrentUserContext currentUser)
    {
        _rbac = rbac;
        _currentUser = currentUser;
    }

    /// <summary>
    /// The permissions the caller holds at one scope.
    /// </summary>
    /// <param name="scope">
    /// <c>org:{guid}</c>, <c>team:{guid}</c> or <c>project:{guid}</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// A scope the caller cannot see and one that does not exist both return an empty list. Nothing
    /// here confirms that an id names something real.
    /// </remarks>
    [HttpGet]
    [NoPermissionRequired(
        "Reports the caller's own permissions and nobody else's; the answer is derived from their " +
        "access snapshot, and an unknown or invisible scope is empty rather than an error.")]
    [ProducesResponseType(typeof(ApiResponse<CapabilityResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Get([FromQuery] string scope, CancellationToken ct)
    {
        if (!ScopeRef.TryParse(scope, out var parsed))
            return BadRequest(new ApiResponse(false, InvalidScopeMessage(scope)));

        var permissions = await _rbac.GetPermissionsAtAsync(_currentUser.UserId, parsed, ct);

        return Ok(new ApiResponse<CapabilityResponse>(
            true, "Capabilities retrieved.", new CapabilityResponse(parsed.ToString(), permissions)));
    }

    /// <summary>
    /// The permissions the caller holds at each of several scopes.
    /// </summary>
    /// <remarks>
    /// A dashboard listing twenty projects needs twenty answers and should not make twenty requests.
    /// POST rather than a repeated query parameter because the list is unbounded in principle and
    /// URLs are not; nothing is created, so this is deliberately not idempotent-by-verb.
    /// </remarks>
    [HttpPost]
    [NoPermissionRequired("Batch form of the GET above; same reasoning.")]
    [ProducesResponseType(typeof(ApiResponse<Dictionary<string, IReadOnlyList<string>>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetMany([FromBody] CapabilityBatchRequest request, CancellationToken ct)
    {
        if (request.Scopes.Count > MaxBatchSize)
            return BadRequest(new ApiResponse(false,
                $"At most {MaxBatchSize} scopes may be requested at once; {request.Scopes.Count} were given."));

        var results = new Dictionary<string, IReadOnlyList<string>>(request.Scopes.Count);

        foreach (var raw in request.Scopes)
        {
            if (!ScopeRef.TryParse(raw, out var parsed))
                return BadRequest(new ApiResponse(false, InvalidScopeMessage(raw)));

            // Keyed on what the caller sent, so they can look results up without re-deriving the
            // canonical spelling. Duplicates collapse rather than erroring — asking twice is not a
            // mistake worth failing a dashboard over.
            results[raw] = await _rbac.GetPermissionsAtAsync(_currentUser.UserId, parsed, ct);
        }

        return Ok(new ApiResponse<Dictionary<string, IReadOnlyList<string>>>(
            true, "Capabilities retrieved.", results));
    }

    private static string InvalidScopeMessage(string? value) =>
        $"'{value}' is not a scope reference. Expected org:{{guid}}, team:{{guid}} or project:{{guid}}.";
}

/// <summary>The caller's permissions at one scope.</summary>
/// <param name="Scope">The scope asked about, in canonical form.</param>
/// <param name="Permissions">
/// What they may do there. Empty when they may do nothing, when the scope does not exist, and when
/// they cannot see it — those are deliberately indistinguishable.
/// </param>
public sealed record CapabilityResponse(string Scope, IReadOnlyList<string> Permissions);

/// <summary>Scopes to report on.</summary>
public sealed class CapabilityBatchRequest
{
    [Required]
    public IReadOnlyList<string> Scopes { get; init; } = [];
}
