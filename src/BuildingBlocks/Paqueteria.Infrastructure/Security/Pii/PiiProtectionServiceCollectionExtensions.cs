using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Cloud;

namespace Paqueteria.Infrastructure.Security.Pii;

public static class PiiProtectionServiceCollectionExtensions
{
    public const string HealthCheckName = "pii_key_vault";

    /// <summary>
    /// Registers the Key Vault envelope protector once, however many modules select it. Options are
    /// validated on start and a <c>ready</c> check fails closed while the key cannot wrap and unwrap.
    /// </summary>
    public static IServiceCollection AddAzureKeyVaultPiiProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(PiiProtectionRegistrationMarker)))
        {
            return services;
        }

        services.AddSingleton<PiiProtectionRegistrationMarker>();
        services.AddOptions<PiiProtectionOptions>()
            .Bind(configuration.GetSection(PiiProtectionOptions.SectionName))
            .Validate(
                PiiProtectionOptionsValidator.IsValid,
                "PiiProtection:AzureKeyVault requires a versionless https Key Vault key URI in KeyId and " +
                "CurrentVersionRefreshSeconds/HealthCacheSeconds between 30 and 3600.")
            .ValidateOnStart();
        services.AddAzureWorkloadCredential();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPiiKeyWrapClient, AzureKeyVaultPiiKeyWrapClient>();
        services.TryAddSingleton<IPiiEnvelopeProtector, PiiEnvelopeProtector>();
        services.AddSingleton<PiiKeyWrapHealthCheck>();
        services.AddHealthChecks().AddCheck<PiiKeyWrapHealthCheck>(HealthCheckName, tags: ["ready"]);
        return services;
    }

    private sealed class PiiProtectionRegistrationMarker;
}

/// <summary>
/// Readiness for the PII key: the current version resolves and a random probe key survives a
/// wrap/unwrap round trip. The result is cached so readiness polling does not multiply Key Vault
/// operations; any failure reports unhealthy (fail closed) and is retried on the next probe.
/// </summary>
public sealed class PiiKeyWrapHealthCheck(
    IPiiKeyWrapClient client,
    IOptions<PiiProtectionOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (DateTimeOffset CheckedAt, bool Healthy)? _last;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var cacheFor = TimeSpan.FromSeconds(options.Value.AzureKeyVault.HealthCacheSeconds);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (_last is { Healthy: true } last && now - last.CheckedAt < cacheFor)
            {
                return HealthCheckResult.Healthy("pii_key_available");
            }

            var healthy = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            _last = (now, healthy);
            return healthy
                ? HealthCheckResult.Healthy("pii_key_available")
                : HealthCheckResult.Unhealthy("pii_key_unavailable");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        var probe = RandomNumberGenerator.GetBytes(32);
        byte[]? unwrapped = null;
        try
        {
            var version = await client.GetCurrentKeyVersionAsync(cancellationToken).ConfigureAwait(false);
            var wrapped = await client.WrapKeyAsync(version, probe, cancellationToken).ConfigureAwait(false);
            unwrapped = await client.UnwrapKeyAsync(version, wrapped, cancellationToken).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(probe, unwrapped);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(probe);
            if (unwrapped is not null)
            {
                CryptographicOperations.ZeroMemory(unwrapped);
            }
        }
    }
}
