using BoardSync.Api.Data;
using BoardSync.Api.Modules.Activity.Models;
using BoardSync.Api.Modules.Activity.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.Activity.Repositories.Implementations;

/// <inheritdoc />
public class ActivityRepository : IActivityRepository
{
    private readonly BoardSyncDbContext _context;

    public ActivityRepository(BoardSyncDbContext context)
    {
        _context = context;
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task AddAsync(ActivityLog entry, CancellationToken ct = default)
    {
        _context.ActivityLogs.Add(entry);
        await _context.SaveChangesAsync(ct);
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public Task<int> CountForOrganizationsAsync(
        IReadOnlyCollection<Guid> organizationIds,
        CancellationToken ct = default) =>
        ForOrganizations(organizationIds).CountAsync(ct);

    public async Task<IReadOnlyList<ActivityLog>> GetPageAsync(
        IReadOnlyCollection<Guid> organizationIds,
        int skip,
        int take,
        CancellationToken ct = default) =>
        await Ordered(ForOrganizations(organizationIds))
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ActivityLog>> GetPageAfterAsync(
        IReadOnlyCollection<Guid> organizationIds,
        DateTime occurredBefore,
        Guid idBefore,
        int take,
        CancellationToken ct = default) =>
        await Ordered(ForOrganizations(organizationIds))
            .Where(a => a.OccurredAt < occurredBefore
                        || (a.OccurredAt == occurredBefore && a.Id.CompareTo(idBefore) < 0))
            .Take(take)
            .ToListAsync(ct);

    // ── Name resolution ───────────────────────────────────────────────────────

    public async Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default) =>
        userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Users
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

    public async Task<IReadOnlyDictionary<Guid, string>> GetOrganizationNamesAsync(
        IReadOnlyCollection<Guid> organizationIds,
        CancellationToken ct = default) =>
        organizationIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Organizations
                .Where(o => organizationIds.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, o => o.Name, ct);

    public async Task<IReadOnlyDictionary<Guid, string>> GetProjectNamesAsync(
        IReadOnlyCollection<Guid> projectIds,
        CancellationToken ct = default) =>
        projectIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Projects
                .Where(p => projectIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

    public async Task<IReadOnlyDictionary<Guid, string>> GetTeamNamesAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default) =>
        teamIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Teams
                .Where(t => teamIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

    public async Task<string> GetUserNameAsync(Guid userId, CancellationToken ct = default) =>
        await _context.Users
            .Where(u => u.Id == userId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct) ?? "Unknown";

    public async Task<string> GetOrganizationNameAsync(Guid organizationId, CancellationToken ct = default) =>
        await _context.Organizations
            .Where(o => o.Id == organizationId)
            .Select(o => o.Name)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

    public async Task<string> GetTeamNameAsync(Guid teamId, CancellationToken ct = default) =>
        await _context.Teams
            .Where(t => t.Id == teamId)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

    // ── Subject lookups ───────────────────────────────────────────────────────

    public async Task<ProjectScope?> GetProjectScopeAsync(Guid projectId, CancellationToken ct = default)
    {
        var row = await _context.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new ProjectScope(p.OrganizationId, p.Name))
            .FirstOrDefaultAsync(ct);

        return row.Equals(default(ProjectScope)) ? null : row;
    }

    public async Task<string> GetWorkItemTitleAsync(Guid workItemId, CancellationToken ct = default) =>
        await _context.WorkItems
            .Where(w => w.Id == workItemId)
            .Select(w => w.Title)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

    public async Task<WorkItemSubject?> GetWorkItemSubjectAsync(Guid workItemId, CancellationToken ct = default)
    {
        var row = await _context.WorkItems
            .Where(w => w.Id == workItemId)
            .Select(w => new WorkItemSubject(w.Title, w.ProjectId))
            .FirstOrDefaultAsync(ct);

        return row.Equals(default(WorkItemSubject)) ? null : row;
    }

    public async Task<string?> GetCommentBodyAsync(Guid commentId, CancellationToken ct = default) =>
        await _context.WorkItemComments
            .Where(c => c.Id == commentId)
            .Select(c => c.Body)
            .FirstOrDefaultAsync(ct);

    // ── Shared query shape ────────────────────────────────────────────────────

    private IQueryable<ActivityLog> ForOrganizations(IReadOnlyCollection<Guid> organizationIds)
    {
        var ids = organizationIds as List<Guid> ?? organizationIds.ToList();
        return _context.ActivityLogs.Where(a => ids.Contains(a.OrganizationId));
    }

    /// <summary>
    /// Newest first, with Id as a tiebreaker. Id is not decoration: entries written in the same
    /// transaction share an OccurredAt to the microsecond, and without a total order the same row
    /// can appear on two pages while another is skipped. It is also what makes a cursor a stable
    /// position rather than an approximate one.
    /// </summary>
    private static IQueryable<ActivityLog> Ordered(IQueryable<ActivityLog> query) =>
        query.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id);
}
