using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.Search.Services;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.Authorization;
using BoardSync.Api.Shared.Auth.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BoardSync.Api.Controllers;

/// <summary>
/// Global search across organizations, projects, members, and work items
/// scoped to the resources the calling user has access to.
/// </summary>
[ApiController]
[Route("api/search")]
[Authorize]
[Produces("application/json")]
public class SearchController : ControllerBase
{
    private readonly ISearchService _search;
    private readonly ICurrentUserContext _currentUser;

    public SearchController(ISearchService search, ICurrentUserContext currentUser)
    {
        _search = search;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Search organizations, projects, members, and work items by name/title.
    /// Results are scoped to the organizations the calling user belongs to.
    /// Requires at least 2 characters, or a bare work item number.
    /// </summary>
    /// <param name="q">The search term — 2 characters, or a work item number such as 7.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matched hits grouped by resource type, up to 10 results per category.</returns>
    [HttpGet]
    [NoPermissionRequired(
        "Search spans every scope, so there is no single one to gate on. Scoped in SearchService by " +
        "IRbacService.GetVisibleOrganizationIdsAsync and .GetProjectVisibilityAsync — each category " +
        "is filtered by the permission that category's own endpoint requires.")]
    [ProducesResponseType(typeof(ApiResponse<GlobalSearchResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] string q, CancellationToken ct)
    {
        var term = q?.Trim() ?? string.Empty;

        /*
         * A single digit is allowed; a single letter is not.
         *
         * The minimum exists to stop one character turning into a prefix scan over everything the
         * caller can see. A bare number is not that — it is an exact match on a work item's number,
         * which is indexed — and refusing it means somebody cannot search for BS-1 by typing what
         * they see on the card.
         */
        var isReferenceNumber =
            term.Length > 0 && term.All(char.IsAsciiDigit);

        if (term.Length == 0
            || (term.Length < ISearchService.MinimumTermLength && !isReferenceNumber))
        {
            return BadRequest(new ApiResponse(false,
                "Search query must be at least 2 characters, or a work item number."));
        }

        var response = await _search.SearchAsync(_currentUser.UserId, q, ct);

        return Ok(new ApiResponse<GlobalSearchResponse>(true, "Search completed.", response));
    }
}
