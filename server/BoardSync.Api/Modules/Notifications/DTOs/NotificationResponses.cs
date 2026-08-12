namespace BoardSync.Api.Modules.Notifications.DTOs;

/// <summary>
/// One entry in the notification bell.
/// </summary>
/// <remarks>
/// Field-for-field identical to the shape this endpoint returned when it lived on
/// <c>WorkspaceController</c>, so moving it here changed nothing on the wire.
/// </remarks>
public record NotificationResponse(
    Guid Id,
    string Type,
    string Title,
    string Organization,
    DateTime CreatedAt
);

/// <summary>
/// A work item change as it comes out of the database, before it is worded for display.
/// </summary>
public readonly record struct NotificationSource(
    Guid Id,
    string FieldName,
    string? NewValue,
    string WorkItemTitle,
    string? OrganizationName,
    DateTime CreatedAt
);
