using System.Security.Cryptography;
using Azure.Core;
using Azure.Core.Cryptography;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Paqueteria.Infrastructure.Cloud;

namespace Paqueteria.Infrastructure.DataProtection;

/// <summary>
/// The resolver the key ring uses to reach its key-encryption key. Production wraps the Key Vault
/// <c>KeyResolver</c> (managed identity); tests register their own instance before the platform
/// registration runs.
/// </summary>
public sealed class DataProtectionKeyEncryptionKeyResolver(IKeyEncryptionKeyResolver resolver)
{
    public IKeyEncryptionKeyResolver Resolver { get; } = resolver;

    /// <summary>
    /// The production resolver: the Key Vault <c>KeyResolver</c> with <paramref name="credential"/> (the workload's
    /// managed identity) and <see cref="CreateClientOptions"/>. <paramref name="transport"/> is a test seam.
    /// </summary>
    internal static DataProtectionKeyEncryptionKeyResolver CreateKeyVault(
        TokenCredential credential,
        HttpPipelineTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new DataProtectionKeyEncryptionKeyResolver(new KeyResolver(credential, CreateClientOptions(transport)));
    }

    /// <summary>
    /// Options of the Key Vault cryptography clients the production resolver builds: no trace context on any
    /// request (OBS-003, <see cref="AzureClientTracing"/>). <paramref name="transport"/> is a test seam.
    /// </summary>
    public static CryptographyClientOptions CreateClientOptions(HttpPipelineTransport? transport = null)
    {
        var options = new CryptographyClientOptions();
        if (transport is not null)
        {
            options.Transport = transport;
        }

        return options.WithoutDistributedTracing();
    }
}

public sealed record DataProtectionKeyEncryptionKeyId(Uri Value);

/// <summary>
/// Readiness of the key-encryption key: a replica that cannot wrap and unwrap with it must not
/// serve, because it could neither read the shared ring nor add keys to it. Cached for five minutes
/// while healthy so readiness polling does not multiply Key Vault operations.
/// </summary>
internal sealed class DataProtectionKeyEncryptionHealthCheck(
    DataProtectionKeyEncryptionKeyResolver resolver,
    DataProtectionKeyEncryptionKeyId keyId,
    TimeProvider? timeProvider = null) : IHealthCheck
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTimeOffset? _healthyAt;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_healthyAt is { } healthyAt && now - healthyAt < CacheFor)
            {
                return HealthCheckResult.Healthy("data_protection_key_encryption_available");
            }

            var probe = RandomNumberGenerator.GetBytes(32);
            try
            {
                var key = await resolver.Resolver.ResolveAsync(keyId.Value.ToString(), cancellationToken)
                    .ConfigureAwait(false);
                var wrapped = await key.WrapKeyAsync("RSA-OAEP", probe, cancellationToken).ConfigureAwait(false);
                var unwrapped = await key.UnwrapKeyAsync("RSA-OAEP", wrapped, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(probe, unwrapped))
                {
                    _healthyAt = null;
                    return HealthCheckResult.Unhealthy("data_protection_key_encryption_unavailable");
                }

                _healthyAt = now;
                return HealthCheckResult.Healthy("data_protection_key_encryption_available");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                _healthyAt = null;
                return HealthCheckResult.Unhealthy("data_protection_key_encryption_unavailable");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(probe);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
