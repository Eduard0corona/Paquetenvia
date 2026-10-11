using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Security;
using Paqueteria.Application.Voice;
using Paqueteria.Infrastructure.Security.Pii;

namespace Paqueteria.Infrastructure.Voice;

public static class VoiceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the VOICE-001 masked call bridge: <see cref="IVoiceBridgeProvider"/>, <see cref="IVoiceBridgeStatus"/>
    /// and <see cref="IVoiceWebhookVerifier"/>. <c>Disabled</c> by default; invalid configuration stops the host at
    /// start with a message that names keys, never values. Twilio is refused in Development, Testing and
    /// DEV_SYNTHETIC, and the synthetic fake is refused anywhere else. With Twilio the phones are protected with the
    /// ADP-001 Key Vault envelope, so <c>PiiProtection:AzureKeyVault</c> is validated too. The HTTP client drops the
    /// default request loggers: no URI, header or form field (all of which can carry a phone) reaches the logs.
    /// </summary>
    public static IServiceCollection AddPaqueteriaVoiceBridge(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<VoiceBridgeOptions>()
            .Bind(configuration.GetSection(VoiceBridgeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<VoiceBridgeOptions>>(
            new VoiceBridgeOptionsValidation(IsSyntheticAllowed(environment), IsLiveAllowed(environment)));
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient(TwilioVoiceBridgeProvider.HttpClientName, client =>
            {
                // The per-attempt timeout is enforced by the adapter; this is only a backstop.
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .RemoveAllLoggers();

        // The adapter holds the factory, not a client: CreateClient runs per call so pooled handlers rotate.
        services.AddSingleton(provider => new TwilioVoiceBridgeProvider(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<IOptions<VoiceBridgeOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<TwilioVoiceBridgeProvider>>()));
        services.AddSingleton<SyntheticVoiceBridgeProvider>();
        services.AddSingleton<IVoiceBridgeProvider, VoiceBridgeProviderRouter>();
        services.AddSingleton<IVoiceBridgeStatus, VoiceBridgeStatus>();
        services.AddSingleton<IVoiceWebhookVerifier, TwilioVoiceWebhookVerifier>();

        // ADP-001: nothing Azure-related is built unless the live provider is selected.
        services.AddAzureKeyVaultPiiProtection(
            configuration,
            provider => provider.GetRequiredService<IOptions<VoiceBridgeOptions>>().Value.Provider ==
                VoiceBridgeProviderKind.Twilio);
        return services;
    }

    private static bool IsSyntheticAllowed(IHostEnvironment environment) =>
        environment.IsDevelopment() ||
        environment.IsEnvironment("Testing") ||
        SyntheticEnvironmentPolicy.IsDevSynthetic(environment.EnvironmentName);

    /// <summary>
    /// Real calls never run where the data is synthetic: not in Development, Testing or the DevSynthetic environment,
    /// and never in a deployment classified DEV_SYNTHETIC, whatever its environment name.
    /// </summary>
    private static bool IsLiveAllowed(IHostEnvironment environment) =>
        !environment.IsDevelopment() &&
        !environment.IsEnvironment("Testing") &&
        !environment.IsEnvironment(SyntheticEnvironmentPolicy.EnvironmentName) &&
        !string.Equals(
            Environment.GetEnvironmentVariable(SyntheticEnvironmentPolicy.DeploymentClassVariable),
            SyntheticEnvironmentPolicy.DeploymentClass,
            StringComparison.Ordinal);

    private sealed class VoiceBridgeOptionsValidation(bool syntheticAllowed, bool liveAllowed)
        : IValidateOptions<VoiceBridgeOptions>
    {
        public ValidateOptionsResult Validate(string? name, VoiceBridgeOptions options)
        {
            var failures = VoiceBridgeOptionsValidator.Validate(options, syntheticAllowed, liveAllowed);
            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}
