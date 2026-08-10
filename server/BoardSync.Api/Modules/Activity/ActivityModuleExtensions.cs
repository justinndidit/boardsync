using BoardSync.Api.Modules.Activity.Handlers;
using BoardSync.Api.Modules.Activity.Services;
using BoardSync.Api.Shared.Kernel.Events;

namespace BoardSync.Api.Modules.Activity;

public static class ActivityModuleExtensions
{
    /// <summary>
    /// Registers the activity log's reader, writer and event subscribers.
    /// </summary>
    public static IServiceCollection AddActivityModule(this IServiceCollection services)
    {
        services.AddScoped<IActivityRecorder, ActivityRecorder>();
        services.AddScoped<IActivityQueryService, ActivityQueryService>();

        // The bus resolves subscribers by closed interface (IEventHandler<WorkItemCreated> and so
        // on), so every one of them needs its own registration. Enumerating them off the class
        // rather than listing them by hand means adding a subscriber is a one-line change to
        // ActivityEventHandlers and cannot be silently forgotten here.
        services.AddScoped<ActivityEventHandlers>();

        var handlerInterfaces = typeof(ActivityEventHandlers)
            .GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventHandler<>));

        foreach (var handlerInterface in handlerInterfaces)
            services.AddScoped(handlerInterface, sp => sp.GetRequiredService<ActivityEventHandlers>());

        return services;
    }
}
