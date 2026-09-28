using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Messaging;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Cloud;

namespace Paqueteria.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMessagingProvider"/> (GATE-004-CHANNELS). Every channel is <c>Disabled</c>
    /// by default; invalid provider configuration stops the host at start with a value-free message.
    /// HTTP clients drop the default request loggers, so no URI, header or body reaches the logs.
    /// </summary>
    public static IServiceCollection AddPaqueteriaMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<MessagingOptions>()
            .Bind(configuration.GetSection(MessagingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MessagingOptions>>(
            new MessagingOptionsValidation(IsSyntheticAllowed(environment)));
        services.TryAddSingleton(TimeProvider.System);
        services.AddAzureWorkloadCredential();

        services.AddHttpClient(MetaWhatsAppCloudApiProvider.HttpClientName, client =>
            {
                // The per-attempt timeout is enforced by the adapter; this is only a backstop.
                client.Timeout = TimeSpan.FromSeconds(90);
            })
            .RemoveAllLoggers();
        services.AddHttpClient(AzureCommunicationEmailProvider.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(90);
            })
            .RemoveAllLoggers();

        // The adapters hold the factory, not a client: CreateClient runs per send so the pooled
        // handlers still rotate (DNS refresh) for the Worker's lifetime.
        services.AddSingleton(provider => new MetaWhatsAppCloudApiProvider(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<IOptions<MessagingOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<MetaWhatsAppCloudApiProvider>>()));
        services.AddSingleton(provider => new AzureCommunicationEmailProvider(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<TokenCredential>(),
            provider.GetRequiredService<IOptions<MessagingOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<AzureCommunicationEmailProvider>>()));
        services.AddSingleton<SyntheticWhatsAppProvider>();
        services.AddSingleton<SyntheticEmailProvider>();
        services.AddSingleton<IMessagingProvider, MessagingProviderRouter>();
        return services;
    }

    private static bool IsSyntheticAllowed(IHostEnvironment environment) =>
        environment.IsDevelopment() ||
        environment.IsEnvironment("Testing") ||
        SyntheticEnvironmentPolicy.IsDevSynthetic(environment.EnvironmentName);

    private sealed class MessagingOptionsValidation(bool syntheticAllowed) : IValidateOptions<MessagingOptions>
    {
        public ValidateOptionsResult Validate(string? name, MessagingOptions options)
        {
            var failures = MessagingOptionsValidator.Validate(options, syntheticAllowed);
            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}
