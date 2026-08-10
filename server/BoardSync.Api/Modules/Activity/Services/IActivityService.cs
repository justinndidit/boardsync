using BoardSync.Api.Modules.Activity.DTOs;
using BoardSync.Api.Modules.Activity.Models;
using BoardSync.Api.Shared.Kernel;

namespace BoardSync.Api.Modules.Activity.Services;

/// <summary>
/// Append-only writer for the activity log. Called from the Activity module's event handlers;
/// the modules that raise the events never touch this.
/// </summary>
public interface IActivityRecorder
{
    Task RecordAsync(ActivityLog entry, CancellationToken ct = default);
}

/// <summary>
/// Read side of the activity log, shared by both feeds.
/// </summary>
public interface IActivityQueryService
{
    /// <summary>
    /// Activity across the given organizations, newest first. Pass one id for an organization
    /// feed, or every organization the caller belongs to for the workspace feed.
    /// </summary>
    Task<PagedResult<ActivityResponse>> GetForOrganizationsAsync(
        IReadOnlyCollection<Guid> organizationIds,
        PaginationQuery pagination,
        CancellationToken ct = default);
}
