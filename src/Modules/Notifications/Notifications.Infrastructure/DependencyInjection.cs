using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Paqueteria.Application.RecipientNotices;
using Notifications.Application.Audience;
using Notifications.Application.RecipientNotices;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Dispatching;
using Notifications.Infrastructure.RecipientNotices;
using Orders.Application.RecipientNotices;
using Orders.Application.Tracking;
using Paqueteria.Application.Messaging;
using Paqueteria.Infrastructure.Database.Pooling;
using Paqueteria.Infrastructure.Messaging;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.Infrastructure.Security;
using Paqueteria.Infrastructure.Security.Pii;

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
        services.AddRuntimeDataSource(configuration, RuntimeDatabaseRole.Worker);
        services.TryAddSingleton<NotificationsWorkerConnectionFactory>();
        services.AddSingleton<NotificationAudienceResolver>();
        services.AddSingleton<PostgreSqlNotificationsStore>();
        services.AddSingleton<INotificationsStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgreSqlNotificationsStore>());
        services.AddSingleton<IRecipientNoticeStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgreSqlNotificationsStore>());
        services.AddSingleton<ISyntheticInAppProvider, SyntheticInAppProvider>();
        services.AddOutboxLaneMonitor();
        AddRecipientNotices(services, configuration);
        services.AddSingleton<NotificationsOutboxProcessor>();
        services.AddHostedService<NotificationsOutboxDispatcher>();
        services.AddHealthChecks().AddCheck<NotificationsHealthCheck>(
            "notifications_dispatcher",
            tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-005; AI-24 <c>recipient_notices</c>): the consumer and sender of
    /// recipient notices, off by default (<c>Notifications:RecipientNotices:Enabled</c>). The Orders and Locations readers
    /// are composed by the Worker; a host without them keeps the fallbacks, and recipient notices then refuse to start
    /// enabled. The unwrap-only notice key is built only when a real provider sends notices; nothing here reaches the PII
    /// key.
    /// </summary>
    private static void AddRecipientNotices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RecipientNoticesOptions>()
            .Bind(configuration.GetSection(RecipientNoticesOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RecipientNoticesOptions>, RecipientNoticesOptionsValidation>();
        services.AddContactLookup(configuration);
        services.AddRecipientNoticeKeyOpener(configuration, serviceProvider =>
        {
            var messaging = serviceProvider.GetRequiredService<IOptions<MessagingOptions>>().Value;
            return serviceProvider.GetRequiredService<IOptions<RecipientNoticesOptions>>().Value.Enabled &&
                (RecipientNoticeProviders.IsReal(messaging, MessagingChannel.WhatsApp) ||
                 RecipientNoticeProviders.IsReal(messaging, MessagingChannel.Email));
        });
        services.TryAddSingleton<UnavailableRecipientNoticeReaders>();
        services.TryAddSingleton<IRecipientNoticeOrderReader>(serviceProvider =>
            serviceProvider.GetRequiredService<UnavailableRecipientNoticeReaders>());
        services.TryAddSingleton<IRecipientNoticeContactReader>(serviceProvider =>
            serviceProvider.GetRequiredService<UnavailableRecipientNoticeReaders>());
        services.TryAddSingleton<IRecipientNoticeTrackingLinks>(serviceProvider =>
            serviceProvider.GetRequiredService<UnavailableRecipientNoticeReaders>());
        services.TryAddSingleton<IRecipientNoticeConsentReader, UnrecordedRecipientNoticeConsentReader>();
        services.TryAddSingleton<IMessagingProvider, DisabledRecipientNoticeMessagingProvider>();
        services.TryAddSingleton<PublicOrderStatusPolicy>();
        services.AddOptions<PublicTrackingOptions>();
        services.AddSingleton<RecipientNoticeContactResolver>();
        services.AddSingleton<IRecipientNoticeLane, RecipientNoticeProcessor>();

        // NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-08 NTF-006; O7, O11 choice 10): the automatic-reply store loop. It
        // runs on the Notifications lane's provider switch and drops every request while notices or WhatsApp are off.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IRecipientAutoReplyStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgreSqlNotificationsStore>());
        services.AddSingleton<RecipientAutoReplyNumberResolver>();
        services.AddSingleton<RecipientAutoReplyProcessor>();
        services.AddHostedService<RecipientAutoReplyDispatcher>();
    }
}
