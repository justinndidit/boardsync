using BoardSync.Api.Data;

using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Intelligence.Services;

/// <summary>The organization a sprint's cost should be charged to.</summary>
/// <remarks>
/// A sprint belongs to a team and a team to an organization, which is the level the narration
/// allowance is held at.
/// </remarks>
public interface ISprintOrganizationLookup
{
    Task<Guid> ForSprintAsync(Guid sprintId, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class SprintOrganizationLookup : ISprintOrganizationLookup
{
    private readonly BoardSyncDbContext _context;

    public SprintOrganizationLookup(BoardSyncDbContext context)
    {
        _context = context;
    }

    public async Task<Guid> ForSprintAsync(Guid sprintId, CancellationToken ct = default) =>
        await _context.Sprints
            .Where(s => s.Id == sprintId)
            .Join(_context.Teams, s => s.TeamId, t => t.Id, (_, t) => t.OrganizationId)
            .FirstOrDefaultAsync(ct);
}
