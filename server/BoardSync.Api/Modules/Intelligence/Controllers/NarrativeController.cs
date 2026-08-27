using BoardSync.Api.Modules.Intelligence.DTOs;
using BoardSync.Api.Modules.Intelligence.Services;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.Intelligence.Controllers;

/// <summary>
/// A written account of a sprint, over figures the Reporting module computed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from the report, and separate on purpose.</b> A reader of
/// <c>GET /api/sprints/{id}/report</c> gets numbers that were calculated from recorded history and
/// nothing else. This endpoint returns prose about those numbers. Merging them would mean a single
/// response where a reader cannot tell which parts a model wrote — the thing
/// <c>build_context.md</c> §8.3 exists to prevent.
/// </para>
/// <para>
/// Gated on <c>sprint:read</c>, the same permission as the report it narrates. A narrative is a
/// restatement of figures the caller can already see, so gating it more tightly would be theatre
/// and gating it less would be a leak.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Produces("application/json")]
public class NarrativeController : ControllerBase
{
    private readonly INarrativeService _narratives;
    private readonly ISprintOrganizationLookup _organizations;

    public NarrativeController(
        INarrativeService narratives,
        ISprintOrganizationLookup organizations)
    {
        _narratives = narratives;
        _organizations = organizations;
    }

    /// <summary>
    /// Prose about a sprint's report. Requires <c>sprint:read</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **200 with no narrative is a normal answer.** No model configured, the organization's
    /// allowance spent, the provider unreachable, or an answer that cited a figure the report does
    /// not contain — each returns a reason rather than an error, because the sprint report itself
    /// is unaffected and a client should carry on showing it.
    /// </para>
    /// <para>
    /// <c>grounded: false</c> means the model stated something the report does not support. The
    /// prose is withheld and the offending sentences are returned instead: trimming them would
    /// leave a paragraph that reads fine and no longer says what was meant.
    /// </para>
    /// </remarks>
    [HttpGet("api/sprints/{sprintId:guid}/report/narrative")]
    [RequirePermission(Permissions.SprintRead, From = "sprintId")]
    [ProducesResponseType(typeof(ApiResponse<NarrativeResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetNarrative(Guid sprintId, CancellationToken ct)
    {
        var organizationId = await _organizations.ForSprintAsync(sprintId, ct);

        var result = await _narratives.ForSprintAsync(sprintId, organizationId, ct);

        return Ok(new ApiResponse<NarrativeResult>(
            true,
            result.Narrative is null
                ? result.Detail ?? "No narrative available."
                : "Narrative generated.",
            result));
    }
}
