using BoardSync.Api.Modules.Notifications.Handlers;
using BoardSync.Api.Modules.Notifications.Repositories.Implementations;
using BoardSync.Api.Modules.Notifications.Repositories.Interfaces;
using BoardSync.Api.Modules.Notifications.Services;
using BoardSync.Api.Modules.WorkItems.Events;
using BoardSync.Api.Shared.Kernel.Events;

namespace BoardSync.Api.Modules.Notifications;

public static class NotificationsModuleExtensions
{
    /// <summary>
    /// Registers the notification bell — its reader, its writer, and the event handlers that decide
    /// who needs to know about what.
    /// </summary>
    /// <remarks>
    /// The handlers are registered against the closed interface, which is how
    /// <c>EventDispatcher</c> finds them. One class implements three, so the same instance is
    /// registered three times rather than three instances doing a third of the job each.
    /// </remarks>
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services)
    {
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<INotificationWriter, NotificationWriter>();
        services.AddScoped<INotificationService, NotificationService>();

        services.AddScoped<NotificationEventHandlers>();
        services.AddScoped<IEventHandler<WorkItemCreated>>(
            sp => sp.GetRequiredService<NotificationEventHandlers>());
        services.AddScoped<IEventHandler<WorkItemAssigned>>(
            sp => sp.GetRequiredService<NotificationEventHandlers>());
        services.AddScoped<IEventHandler<WorkItemStateChanged>>(
            sp => sp.GetRequiredService<NotificationEventHandlers>());
        services.AddScoped<IEventHandler<WorkItemCommentAdded>>(
            sp => sp.GetRequiredService<NotificationEventHandlers>());

        return services;
    }
}
