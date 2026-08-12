using BoardSync.Api.Modules.Notifications.DTOs;
using BoardSync.Api.Modules.Notifications.Repositories.Interfaces;

namespace BoardSync.Api.Modules.Notifications.Services;

/// <inheritdoc />
public class NotificationService : INotificationService
{
    private readonly INotificationRepository _repository;

    public NotificationService(INotificationRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<NotificationResponse>> GetForUserAsync(
        Guid userId,
        int limit = NotificationDefaults.DefaultLimit,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, NotificationDefaults.MaxLimit);

        var orgIds = await _repository.GetOrganizationIdsForUserAsync(userId, ct);

        if (orgIds.Count == 0) return [];

        var sources = await _repository.GetRecentForOrganizationsAsync(orgIds, take, ct);

        return sources.Select(Describe).ToList();
    }

    /// <summary>
    /// Words a raw history row for display.
    /// </summary>
    /// <remarks>
    /// The <c>type</c> vocabulary is preserved exactly as it was before this module existed —
    /// <c>WorkItem{NewState}</c> for a state change, <c>WorkItemUpdated</c> for everything else —
    /// because clients already switch on those strings.
    /// </remarks>
    private static NotificationResponse Describe(NotificationSource source)
    {
        var type = source.FieldName == "State"
            ? $"WorkItem{source.NewValue}"
            : "WorkItemUpdated";

        var title = $"{source.WorkItemTitle} — {source.FieldName} changed to {source.NewValue}";

        return new NotificationResponse(
            source.Id,
            type,
            title,
            source.OrganizationName ?? string.Empty,
            source.CreatedAt);
    }
}
