using BoardSync.Api.Data;
using BoardSync.Api.Modules.OrgProject.Domain.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BoardSync.Api.Modules.OrgProject.Domain.Helpers;

/// <summary>
/// Builds the work item activity feed shared by <c>/api/workspace/activity</c> and
/// <c>/api/orgs/{orgId}/activity</c>.
///
/// The two endpoints differ only in how they pick the project set — everything downstream of that
/// must stay identical, because both return <see cref="WorkspaceActivityResponse"/> and the same
/// client component renders either feed. Building the projection in one place is what stops the
/// two shapes drifting apart.
/// </summary>
public static class ActivityFeed
{
    /// <summary>
    /// The <paramref name="take"/> most recent work item history entries across
    /// <paramref name="projectIds"/>, newest first.
    /// </summary>
    public static async Task<IReadOnlyList<WorkspaceActivityResponse>> BuildAsync(
        BoardSyncDbContext context,
        IReadOnlyCollection<Guid> projectIds,
        int take,
        CancellationToken ct)
    {
        if (projectIds.Count == 0)
            return [];

        var scopedProjectIds = projectIds as List<Guid> ?? projectIds.ToList();

        var history = await context.WorkItemHistory
            .Where(h => scopedProjectIds.Contains(h.WorkItem.ProjectId))
            .OrderByDescending(h => h.CreatedAt)
            .Take(take)
            .Select(h => new
            {
                h.Id,
                h.FieldName,
                h.OldValue,
                h.NewValue,
                h.ChangedBy,
                h.CreatedAt,
                WorkItemTitle = h.WorkItem.Title,
                WorkItemProjectId = h.WorkItem.ProjectId
            })
            .ToListAsync(ct);

        if (history.Count == 0)
            return [];

        // Actor and project names are resolved as follow-up lookups rather than joins. A join to
        // Users would be applied *after* the Take above, so an entry whose author row is missing
        // would be dropped from the result and silently shrink the page, instead of falling back
        // to "Unknown" the way it does here.
        var actorIds = history.Select(h => h.ChangedBy).Distinct().ToList();
        var actorMap = await context.Users
            .Where(u => actorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var historyProjectIds = history.Select(h => h.WorkItemProjectId).Distinct().ToList();
        var projectMap = await context.Projects
            .Where(p => historyProjectIds.Contains(p.Id))
            .Select(p => new { p.Id, ProjectName = p.Name, OrgName = p.Organization.Name })
            .ToDictionaryAsync(p => p.Id, ct);

        return history.Select(h =>
        {
            actorMap.TryGetValue(h.ChangedBy, out var actor);
            projectMap.TryGetValue(h.WorkItemProjectId, out var project);

            return new WorkspaceActivityResponse(
                h.Id,
                TypeFor(h.FieldName, h.NewValue),
                h.WorkItemTitle,
                DetailFor(h.FieldName, h.OldValue, h.NewValue),
                actor ?? "Unknown",
                project?.OrgName ?? string.Empty,
                project?.ProjectName ?? string.Empty,
                h.CreatedAt);
        }).ToList();
    }

    /// <summary>
    /// Discriminator the client switches on to pick an icon. State transitions get their own type
    /// per target state (e.g. "WorkItemActive"); every other field change is a plain update.
    /// </summary>
    public static string TypeFor(string fieldName, string? newValue) =>
        fieldName == "State" ? $"WorkItem{newValue}" : "WorkItemUpdated";

    /// <summary>Human-readable description of a single field change.</summary>
    public static string DetailFor(string fieldName, string? oldValue, string? newValue) =>
        oldValue != null
            ? $"{fieldName}: {oldValue} → {newValue}"
            : $"{fieldName} set to {newValue}";
}
