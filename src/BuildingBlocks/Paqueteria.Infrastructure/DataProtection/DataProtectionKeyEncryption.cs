using System.Security.Cryptography;
using Azure.Core.Cryptography;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Paqueteria.Infrastructure.DataProtection;

/// <summary>
/// The resolver the key ring uses to reach its key-encryption key. Production wraps the Key Vault
/// <c>KeyResolver</c> (managed identity); tests register their own instance before the platform
/// registration runs.
/// </summary>
public sealed class DataProtectionKeyEncryptionKeyResolver(IKeyEncryptionKeyResolver resolver)
{
    public IKeyEncryptionKeyResolver Resolver { get; } = resolver;
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
