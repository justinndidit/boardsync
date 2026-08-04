using BoardSync.Api.Data;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using BoardSync.Api.Shared.Auth;
using BoardSync.Api.Shared.Auth.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    private readonly BoardSyncDbContext _context;
    private readonly ICurrentUserContext _currentUser;

    public SearchController(BoardSyncDbContext context, ICurrentUserContext currentUser)
    {
        _context = context;
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
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return BadRequest(new ApiResponse(false, "Search query must be at least 2 characters."));

        var term = q.Trim().ToLowerInvariant();
        var userId = _currentUser.UserId;

        // Scope everything to orgs the user belongs to
        var orgIds = await _context.OrganizationMemberships
            .Where(m => m.UserId == userId)
            .Select(m => m.OrganizationId)
            .ToListAsync(ct);

        var projectIds = await _context.Projects
            .Where(p => orgIds.Contains(p.OrganizationId) && p.IsActive)
            .Select(p => p.Id)
            .ToListAsync(ct);

        // These four run sequentially by design. They share the one DbContext scoped to this
        // request, which is not thread-safe: starting them concurrently and awaiting with
        // Task.WhenAll throws "a second operation was started on this context instance".
        var organizations = await _context.Organizations
            .Where(o => orgIds.Contains(o.Id) && o.IsActive
                        && (o.Name.ToLower().Contains(term) || o.Slug.ToLower().Contains(term)))
            .OrderBy(o => o.Name)
            .Take(10)
            .Select(o => new SearchHit(o.Id, o.Name, o.Slug))
            .ToListAsync(ct);

        var projects = await _context.Projects
            .Where(p => projectIds.Contains(p.Id)
                        && (p.Name.ToLower().Contains(term) || p.Slug.ToLower().Contains(term)))
            .OrderBy(p => p.Name)
            .Take(10)
            .Select(p => new SearchHit(p.Id, p.Name, p.Slug))
            .ToListAsync(ct);

        // Order on the joined shape, not on a constructed SearchHit: EF cannot translate an
        // OrderBy that reads a property off a projected record and fails the whole request.
        var members = await _context.OrganizationMemberships
            .Where(m => orgIds.Contains(m.OrganizationId))
            .Select(m => m.UserId)
            .Distinct()
            .Join(
                _context.Users.Where(u => u.IsActive
                    && (u.DisplayName.ToLower().Contains(term)
                        || u.Email.ToLower().Contains(term))),
                uid => uid,
                u => u.Id,
                (uid, u) => new { u.Id, u.DisplayName, u.Email })
            .OrderBy(x => x.DisplayName)
            .Take(10)
            .Select(x => new SearchHit(x.Id, x.DisplayName, x.Email))
            .ToListAsync(ct);

        var workItems = await _context.WorkItems
            .Where(w => projectIds.Contains(w.ProjectId)
                        && w.IsActive
                        && w.Title.ToLower().Contains(term))
            .OrderByDescending(w => w.CreatedAt)
            .Take(10)
            .Select(w => new SearchHit(w.Id, w.Title, null))
            .ToListAsync(ct);

        var response = new GlobalSearchResponse(
            Organizations: organizations,
            Projects: projects,
            Members: members,
            WorkItems: workItems
        );

        return Ok(new ApiResponse<GlobalSearchResponse>(true, "Search completed.", response));
    }
}
