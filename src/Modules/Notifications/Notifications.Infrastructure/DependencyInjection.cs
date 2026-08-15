using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Audience;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Dispatching;

namespace Notifications.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNotificationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<NotificationsOptions>()
            .Bind(configuration.GetSection(NotificationsOptions.SectionName))
            .Validate(NotificationsOptionsValidator.IsValid,
                "Notifications contains an invalid bounded option.")
            .Validate(options => options.Provider != NotificationsDispatcherProviderKind.PostgreSql ||
                    !string.IsNullOrWhiteSpace(configuration.GetConnectionString("PaqueteriaWorker")),
                "Notifications:Provider=PostgreSql requires ConnectionStrings:PaqueteriaWorker.")
            .ValidateOnStart();
        services.AddSingleton(_ => new NotificationsWorkerConnectionFactory(
            configuration.GetConnectionString("PaqueteriaWorker") ?? string.Empty));
        services.AddSingleton<NotificationAudienceResolver>();
        services.AddSingleton<INotificationsStore, PostgreSqlNotificationsStore>();
        services.AddSingleton<ISyntheticInAppProvider, SyntheticInAppProvider>();
        services.AddSingleton<NotificationsOutboxProcessor>();
        services.AddHostedService<NotificationsOutboxDispatcher>();
        services.AddHealthChecks().AddCheck<NotificationsHealthCheck>(
            "notifications_dispatcher",
            tags: ["ready"]);
        return services;
    }
}
