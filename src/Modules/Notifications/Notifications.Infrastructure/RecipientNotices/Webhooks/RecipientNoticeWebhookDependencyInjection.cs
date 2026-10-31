using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Notifications.Application.RecipientNotices;
using Paqueteria.Infrastructure.Database.Pooling;
using Paqueteria.Infrastructure.Security;
using Paqueteria.Infrastructure.Security.Pii;

namespace Notifications.Infrastructure.RecipientNotices.Webhooks;

public static class RecipientNoticeWebhookDependencyInjection
{
    /// <summary>
    /// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-08 NTF-006) API composition: the provider webhooks and the e-mail
    /// unsubscribe, on the API's one <c>paqueteria_app</c> pool. Both webhook switches are off by default (uniform 404);
    /// the unsubscribe has no switch (O4) and answers the uniform 404 for every token while no contact lookup key is
    /// configured. Only the wrap side of the recipient-notice key is registered, and only when the WhatsApp webhook seals
    /// sender numbers with Key Vault; nothing here opens a notice copy, reaches the Notifications lane or calls a provider.
    /// </summary>
    public static IServiceCollection AddRecipientNoticeReceipts(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        services.AddContactLookup(configuration);
        services.AddOptions<WhatsAppWebhookOptions>()
            .Bind(configuration.GetSection(WhatsAppWebhookOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<EmailEventsOptions>()
            .Bind(configuration.GetSection(EmailEventsOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton<RecipientNoticeWebhookOptionsValidation>();
        services.AddSingleton<IValidateOptions<WhatsAppWebhookOptions>>(serviceProvider =>
            serviceProvider.GetRequiredService<RecipientNoticeWebhookOptionsValidation>());
        services.AddSingleton<IValidateOptions<EmailEventsOptions>>(serviceProvider =>
            serviceProvider.GetRequiredService<RecipientNoticeWebhookOptionsValidation>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddRuntimeDataSource(configuration, RuntimeDatabaseRole.Application);
        services.AddSingleton<IRecipientNoticeWebhookGuard, RecipientNoticeWebhookGuard>();
        services.AddSingleton<IRecipientNoticeReceiptStore, PostgreSqlRecipientNoticeReceiptStore>();

        // O11 choice 10: the sender number is sealed with the API's wrap-only half of the recipient-notice key.
        services.AddAzureKeyVaultRecipientNoticeKey(
            configuration,
            serviceProvider => serviceProvider.GetRequiredService<IOptions<WhatsAppWebhookOptions>>().Value is
                { Enabled: true, NumberSealer: RecipientAutoReplyNumberSealerKind.AzureKeyVault });
        services.AddSingleton<DisabledAutoReplyNumberSealer>();
        services.AddSingleton<SyntheticAutoReplyNumberSealer>();
        services.AddSingleton(serviceProvider =>
            new AzureKeyVaultAutoReplyNumberSealer(serviceProvider.GetRequiredService<IRecipientNoticeEnvelopeSealer>()));
        services.AddSingleton<IRecipientAutoReplyNumberSealer>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<WhatsAppWebhookOptions>>().Value.NumberSealer switch
            {
                RecipientAutoReplyNumberSealerKind.Mock => serviceProvider.GetRequiredService<SyntheticAutoReplyNumberSealer>(),
                RecipientAutoReplyNumberSealerKind.AzureKeyVault =>
                    serviceProvider.GetRequiredService<AzureKeyVaultAutoReplyNumberSealer>(),
                _ => serviceProvider.GetRequiredService<DisabledAutoReplyNumberSealer>(),
            });

        services.AddScoped<IRecipientNoticeWebhookService, RecipientNoticeWebhookService>();
        services.AddScoped<IRecipientNoticeUnsubscribeService, RecipientNoticeUnsubscribeService>();
        return services;
    }
}
