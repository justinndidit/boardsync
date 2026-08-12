using BoardSync.Api.Modules.Notifications.Repositories.Implementations;
using BoardSync.Api.Modules.Notifications.Repositories.Interfaces;
using BoardSync.Api.Modules.Notifications.Services;

namespace BoardSync.Api.Modules.Notifications;

public static class NotificationsModuleExtensions
{
    /// <summary>
    /// Registers the notification bell's reader and its data access.
    /// </summary>
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services)
    {
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<INotificationService, NotificationService>();

        return services;
    }
}
