using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Modules.Search.Services;
using BoardSync.Api.Shared.Auth;
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
    /// Requires a query string of at least 2 characters.
    /// </summary>
    /// <param name="q">The search term (minimum 2 characters).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Matched hits grouped by resource type, up to 10 results per category.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<GlobalSearchResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Search([FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < ISearchService.MinimumTermLength)
            return BadRequest(new ApiResponse(false, "Search query must be at least 2 characters."));

        var response = await _search.SearchAsync(_currentUser.UserId, q, ct);

        return Ok(new ApiResponse<GlobalSearchResponse>(true, "Search completed.", response));
    }
}
