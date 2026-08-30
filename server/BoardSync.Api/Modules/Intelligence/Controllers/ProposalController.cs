using BoardSync.Api.Shared.Kernel;
using BoardSync.Api.Modules.Intelligence.DTOs;
using BoardSync.Api.Modules.Intelligence.Services;
using BoardSync.Api.Modules.Rbac.Models;
using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Modules.Intelligence.Controllers;

/// <summary>
/// Decomposing a requirements document into proposed work items.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here writes to the board except <see cref="Accept"/>.</b> Requesting a decomposition
/// produces a proposal — a draft with no authority that a human reads and decides on. That is the
/// boundary build_context.md §8.1 draws, and the reason it exists is that a board which silently
/// gains eleven invented tasks is worse than no assistance at all.
/// </para>
/// <para>
/// Every endpoint is gated on <c>workitem:write</c> in the target project. Producing a draft is
/// gated as tightly as accepting one deliberately: a decomposition costs the organization real
/// money, so the permission to spend it belongs with the permission to create the work.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Produces("application/json")]
public class ProposalController : ControllerBase
{
    private readonly IProposalService _proposals;
    private readonly ICurrentUserContext _currentUser;

    public ProposalController(IProposalService proposals, ICurrentUserContext currentUser)
    {
        _proposals = proposals;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Asks for a document to be decomposed. Requires <c>workitem:write</c>.
    /// </summary>
    /// <remarks>
    /// <b>202, not 200.</b> The model call takes tens of seconds and runs as a background job, so
    /// this returns an id to poll rather than holding the connection open for it — build_context.md
    /// §8.4. Poll <c>GET /api/intelligence/proposals/{id}</c> until it leaves <c>Pending</c>.
    /// </remarks>
    [HttpPost("api/projects/{projectId:guid}/intelligence/decompose")]
    [RequirePermission(Permissions.WorkItemWrite, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Decompose(
        Guid projectId,
        [FromBody] DecomposeRequest request,
        CancellationToken ct)
    {
        var proposalId = await _proposals.RequestAsync(
            projectId, request, _currentUser.UserId, ct);

        return Accepted(new ApiResponse<object>(
            true,
            "Decomposition queued.",
            new { proposalId }));
    }

    /// <summary>
    /// Every proposal this project has produced, newest first. Requires <c>workitem:write</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this a proposal was reachable only by its id, and nothing recorded the id — so once
    /// you navigated away it was gone. That mattered beyond convenience: proposals are kept after
    /// the decision on purpose, because every accept and reject is a labelled example of what this
    /// team considers a good breakdown, and the record could not be read back.
    /// </para>
    /// <para>
    /// Summaries, not drafts. A page of thirty proposals is not a page of thirty hierarchies —
    /// <c>GET /intelligence/proposals/{id}</c> is what returns one.
    /// </para>
    /// </remarks>
    [HttpGet("api/projects/{projectId:guid}/intelligence/proposals")]
    [RequirePermission(Permissions.WorkItemWrite, From = "projectId")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ProposalSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(
        Guid projectId,
        [FromQuery] PaginationQuery pagination,
        CancellationToken ct)
    {
        var proposals = await _proposals.ListAsync(projectId, pagination, ct);

        return Ok(new ApiResponse<PagedResult<ProposalSummary>>(
            true, "Proposals retrieved.", proposals));
    }

    /// <summary>
    /// A proposal and its draft. Requires <c>workitem:write</c> in the owning project.
    /// </summary>
    /// <remarks>
    /// <c>Failed</c> is a normal answer with a reason, not an error: no model configured, the
    /// allowance spent, or a tree the board's hierarchy would not accept. The client shows the
    /// reason and offers to try again.
    /// </remarks>
    [HttpGet("api/intelligence/proposals/{proposalId:guid}")]
    [RequirePermission(Permissions.WorkItemWrite, From = "proposalId")]
    [ProducesResponseType(typeof(ApiResponse<ProposalView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid proposalId, CancellationToken ct)
    {
        var view = await _proposals.GetAsync(proposalId, ct);

        return Ok(new ApiResponse<ProposalView>(true, "Proposal retrieved.", view));
    }

    /// <summary>
    /// Creates work items from a proposal. Requires <c>workitem:write</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the only call in the module that changes the board</b>, and it goes through the
    /// same service a person creating a work item by hand uses.
    /// </para>
    /// <para>
    /// Selecting a node selects its ancestors too — a story cannot be created under a feature that
    /// was not. An empty selection means the whole draft. Accepting twice is refused rather than
    /// duplicated.
    /// </para>
    /// </remarks>
    [HttpPost("api/intelligence/proposals/{proposalId:guid}/accept")]
    [RequirePermission(Permissions.WorkItemWrite, From = "proposalId")]
    [ProducesResponseType(typeof(ApiResponse<AcceptanceResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Accept(
        Guid proposalId,
        [FromBody] AcceptProposalRequest request,
        CancellationToken ct)
    {
        var result = await _proposals.AcceptAsync(
            proposalId, request ?? new AcceptProposalRequest(), _currentUser.UserId, ct);

        return Ok(new ApiResponse<AcceptanceResult>(
            true, $"{result.Created} work items created.", result));
    }

    /// <summary>Declines a proposal. Requires <c>workitem:write</c>.</summary>
    /// <remarks>
    /// Kept rather than deleted. A rejection records what this team did not consider a good
    /// breakdown, which is exactly the signal §8.1 says autonomous writes would throw away.
    /// </remarks>
    [HttpPost("api/intelligence/proposals/{proposalId:guid}/reject")]
    [RequirePermission(Permissions.WorkItemWrite, From = "proposalId")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reject(Guid proposalId, CancellationToken ct)
    {
        await _proposals.RejectAsync(proposalId, _currentUser.UserId, ct);

        return Ok(new ApiResponse(true, "Proposal rejected."));
    }
}
