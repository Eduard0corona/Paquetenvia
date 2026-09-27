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
    /// Registers the Key Vault envelope protector once, however many modules call it. Each module
    /// passes <paramref name="isSelected"/>, evaluated against its own bound options at runtime (not
    /// at registration time, so every configuration source is honoured). When any module selects
    /// the protector, <c>PiiProtection</c> is validated on start and the <c>ready</c> check probes
    /// wrap/unwrap and fails closed; otherwise nothing Azure-related is ever constructed.
    /// </summary>
    public static IServiceCollection AddAzureKeyVaultPiiProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<IServiceProvider, bool> isSelected)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(isSelected);
        services.AddSingleton(serviceProvider => new PiiKeyVaultSelection(() => isSelected(serviceProvider)));
        if (services.Any(descriptor => descriptor.ServiceType == typeof(PiiProtectionRegistrationMarker)))
        {
            return services;
        }

        services.AddSingleton<PiiProtectionRegistrationMarker>();
        services.AddOptions<PiiProtectionOptions>()
            .Bind(configuration.GetSection(PiiProtectionOptions.SectionName))
            .Validate<IEnumerable<PiiKeyVaultSelection>>(
                (options, selections) => !selections.Any(selection => selection.IsSelected) ||
                    PiiProtectionOptionsValidator.IsValid(options),
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

/// <summary>Whether one module selected the Key Vault protector, read from its bound options.</summary>
public sealed class PiiKeyVaultSelection(Func<bool> isSelected)
{
    public bool IsSelected => isSelected();
}

/// <summary>
/// Readiness for the PII key: the current version resolves and a random probe key survives a
/// wrap/unwrap round trip. The result is cached so readiness polling does not multiply Key Vault
/// operations; any failure reports unhealthy (fail closed) and is retried on the next probe.
/// </summary>
public sealed class PiiKeyWrapHealthCheck(
    IServiceProvider services,
    IEnumerable<PiiKeyVaultSelection> selections,
    IOptions<PiiProtectionOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (DateTimeOffset CheckedAt, bool Healthy)? _last;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!selections.Any(selection => selection.IsSelected))
        {
            return HealthCheckResult.Healthy("pii_key_vault_not_selected");
        }

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
            var client = services.GetRequiredService<IPiiKeyWrapClient>();
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
