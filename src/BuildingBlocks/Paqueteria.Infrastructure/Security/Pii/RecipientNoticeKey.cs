using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Keys;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Cloud;

namespace Paqueteria.Infrastructure.Security.Pii;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (E4-P2, "Llave propia para avisos (Recommended)"; AI-24
/// <c>recipient_notices.contact.notice_key</c>; AI-08 NTF-002): the Key Vault RSA key <c>recipient-notice-kek</c>
/// that seals the copies of the destination phone and e-mail the Worker may open to send recipient notices. It is
/// distinct from the PII key and from the Data Protection key. The section describes the key only; the Locations
/// module selects whether to seal with it (<c>Locations:NoticeKeyProtector</c>).
/// </summary>
public sealed class RecipientNoticeKeyOptions
{
    public const string SectionName = "PiiProtection:RecipientNoticeKey";

    /// <summary>Versionless key URI, <c>https://{vault}.vault.azure.net/keys/{name}</c>.</summary>
    public string KeyId { get; set; } = string.Empty;

    public int CurrentVersionRefreshSeconds { get; set; } = 300;

    public int HealthCacheSeconds { get; set; } = 300;
}

public static class RecipientNoticeKeyOptionsValidator
{
    /// <summary>
    /// A versionless https key URI, refresh and health intervals between 30 and 3600 seconds, and a key that is not
    /// the PII key: a notice copy must never be readable with the key that opens addresses, names and descriptions.
    /// </summary>
    public static bool IsValid(RecipientNoticeKeyOptions? options, string? piiKeyId) =>
        options is not null &&
        KeyVaultKeyIds.TryParseVersionless(options.KeyId, out var vault, out var name) &&
        options.CurrentVersionRefreshSeconds is >= 30 and <= 3_600 &&
        options.HealthCacheSeconds is >= 30 and <= 3_600 &&
        !(KeyVaultKeyIds.TryParseVersionless(piiKeyId, out var piiVault, out var piiName) &&
          string.Equals(vault.Host, piiVault.Host, StringComparison.OrdinalIgnoreCase) &&
          string.Equals(name, piiName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The API's half of the recipient-notice key: the current version and the wrap of a data key under it. There is no
/// unwrap operation: the API can seal a notice copy and can never open one (AI-24 <c>contact.roles</c>). Only the
/// Worker unwraps (AI-08 NTF-005).
/// </summary>
public interface IRecipientNoticeKeyWrapClient : IPiiKeyWrapper;

/// <summary>Seals values with the ADP-001 envelope format v1 under the recipient-notice key. It never opens them.</summary>
public interface IRecipientNoticeEnvelopeSealer
{
    Task<PiiProtectedBatch> SealAsync(
        PiiBinding binding,
        IReadOnlyList<PiiPlaintext> values,
        CancellationToken cancellationToken);
}

public sealed class RecipientNoticeEnvelopeSealer(IRecipientNoticeKeyWrapClient keyWrapClient) : IRecipientNoticeEnvelopeSealer
{
    public Task<PiiProtectedBatch> SealAsync(
        PiiBinding binding,
        IReadOnlyList<PiiPlaintext> values,
        CancellationToken cancellationToken) =>
        PiiEnvelopeFormat.SealAsync(keyWrapClient, binding, values, cancellationToken);
}

/// <summary>
/// The recipient-notice key in Azure Key Vault, reached with the workload managed identity. The current version and
/// its public RSA part are read from Key Vault (<c>GET /keys/{name}</c>, cached for
/// <c>CurrentVersionRefreshSeconds</c>) and every data key is wrapped in process with that public part, RSA-OAEP-256
/// (OAEP with SHA-256 and MGF1-SHA-256, the Key Vault <c>RSA-OAEP-256</c> algorithm the Worker unwraps with). The API
/// therefore needs only to read the key: no <c>wrapKey</c>, <c>unwrapKey</c> or <c>decrypt</c> call is ever made, and
/// this class has no code that could make one. The persisted version is <c>akv:{key name}/{Key Vault version}</c>.
/// </summary>
public sealed partial class AzureKeyVaultRecipientNoticeKeyWrapClient : IRecipientNoticeKeyWrapClient
{
    internal const string VersionPrefix = "akv:";

    /// <summary>RSA keys shorter than this are refused (3072 bits recommended, as the PII key).</summary>
    internal const int MinimumModulusBytes = 256;

    private readonly string _keyName;
    private readonly KeyClient _keyClient;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, RSAParameters> _publicKeys = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CurrentVersion? _current;

    public AzureKeyVaultRecipientNoticeKeyWrapClient(
        IOptions<RecipientNoticeKeyOptions> options,
        TokenCredential credential,
        TimeProvider timeProvider)
        : this(options, credential, timeProvider, transport: null)
    {
    }

    /// <summary>Test seam: routes the Key Vault SDK through <paramref name="transport"/>.</summary>
    internal AzureKeyVaultRecipientNoticeKeyWrapClient(
        IOptions<RecipientNoticeKeyOptions> options,
        TokenCredential credential,
        TimeProvider timeProvider,
        HttpPipelineTransport? transport)
    {
        var settings = options.Value;
        if (!KeyVaultKeyIds.TryParseVersionless(settings.KeyId, out var vaultUri, out var keyName))
        {
            throw new InvalidOperationException(
                "PiiProtection:RecipientNoticeKey:KeyId must be a versionless Key Vault key URI.");
        }

        _keyName = keyName;
        _timeProvider = timeProvider;
        _refreshInterval = TimeSpan.FromSeconds(settings.CurrentVersionRefreshSeconds);
        var clientOptions = new KeyClientOptions();
        if (transport is not null)
        {
            clientOptions.Transport = transport;
        }

        clientOptions.Retry.MaxRetries = 2;
        clientOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        clientOptions.Diagnostics.IsLoggingContentEnabled = false;
        _keyClient = new KeyClient(vaultUri, credential, clientOptions.WithoutDistributedTracing());
    }

    public async Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_current is { } cached && now - cached.ReadAt < _refreshInterval)
        {
            return cached.Value;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_current is { } fresh && now - fresh.ReadAt < _refreshInterval)
            {
                return fresh.Value;
            }

            KeyVaultKey key = await _keyClient.GetKeyAsync(_keyName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var parameters = ReadPublicKey(key, now);
            // ReadPublicKey accepted the version as a Key Vault version identifier.
            var version = key.Properties.Version!;
            _publicKeys[version] = parameters;
            var value = VersionPrefix + _keyName + "/" + version;
            _current = new CurrentVersion(value, now);
            return value;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<byte[]> WrapKeyAsync(string keyVersion, byte[] dataKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataKey);
        if (!TryParseKeyVersion(keyVersion, out var keyName, out var version) ||
            !string.Equals(keyName, _keyName, StringComparison.Ordinal))
        {
            throw new PiiProtectionUnavailableException();
        }

        if (!_publicKeys.TryGetValue(version, out var parameters))
        {
            KeyVaultKey key = await _keyClient.GetKeyAsync(_keyName, version, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(key.Properties.Version, version, StringComparison.Ordinal))
            {
                throw new PiiProtectionUnavailableException();
            }

            parameters = ReadPublicKey(key, _timeProvider.GetUtcNow());
            _publicKeys[version] = parameters;
        }

        using var rsa = RSA.Create(parameters);
        return rsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256);
    }

    internal static bool TryParseKeyVersion(string? keyVersion, out string keyName, out string version)
    {
        keyName = string.Empty;
        version = string.Empty;
        if (keyVersion is null || !keyVersion.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = keyVersion.LastIndexOf('/');
        if (separator <= VersionPrefix.Length)
        {
            return false;
        }

        keyName = keyVersion[VersionPrefix.Length..separator];
        version = keyVersion[(separator + 1)..];
        return KeyVaultKeyIds.IsKeyName(keyName) && IsKeyVaultVersion(version);
    }

    /// <summary>
    /// Only the public part is read (modulus and exponent). The key must be enabled, current, RSA, allowed to wrap
    /// (when Key Vault lists permitted operations) and at least 2048 bits.
    /// </summary>
    private static RSAParameters ReadPublicKey(KeyVaultKey key, DateTimeOffset now)
    {
        var properties = key.Properties;
        if (properties.Enabled != true ||
            properties.ExpiresOn is { } expires && expires <= now ||
            properties.NotBefore is { } notBefore && notBefore > now ||
            (key.KeyType != KeyType.Rsa && key.KeyType != KeyType.RsaHsm) ||
            key.KeyOperations.Count > 0 && !key.KeyOperations.Contains(KeyOperation.WrapKey) ||
            !IsKeyVaultVersion(properties.Version) ||
            key.Key is not { } jwk)
        {
            throw new PiiProtectionUnavailableException();
        }

        if (jwk.N is not { Length: >= MinimumModulusBytes } modulus || jwk.E is not { Length: > 0 } exponent)
        {
            throw new PiiProtectionUnavailableException();
        }

        return new RSAParameters { Modulus = modulus, Exponent = exponent };
    }

    private static bool IsKeyVaultVersion(string? value) => value is not null && KeyVaultVersionPattern().IsMatch(value);

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyVaultVersionPattern();

    private sealed record CurrentVersion(string Value, DateTimeOffset ReadAt);
}

public static class RecipientNoticeKeyServiceCollectionExtensions
{
    public const string HealthCheckName = "recipient_notice_key";

    /// <summary>
    /// Registers the recipient-notice sealer once. <paramref name="isSelected"/> is evaluated against the caller's own
    /// bound options at runtime. When selected, <c>PiiProtection:RecipientNoticeKey</c> is validated on start and the
    /// <c>ready</c> check reads the key and seals a random probe (it cannot open it: nothing here unwraps); otherwise
    /// nothing Azure-related is ever constructed.
    /// </summary>
    public static IServiceCollection AddAzureKeyVaultRecipientNoticeKey(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<IServiceProvider, bool> isSelected)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(isSelected);
        services.AddSingleton(serviceProvider => new RecipientNoticeKeySelection(() => isSelected(serviceProvider)));
        if (services.Any(descriptor => descriptor.ServiceType == typeof(RecipientNoticeKeyRegistrationMarker)))
        {
            return services;
        }

        services.AddSingleton<RecipientNoticeKeyRegistrationMarker>();
        services.AddOptions<RecipientNoticeKeyOptions>()
            .Bind(configuration.GetSection(RecipientNoticeKeyOptions.SectionName))
            .Validate<IEnumerable<RecipientNoticeKeySelection>>(
                (options, selections) => !selections.Any(selection => selection.IsSelected) ||
                    RecipientNoticeKeyOptionsValidator.IsValid(
                        options,
                        configuration[$"{PiiProtectionOptions.SectionName}:AzureKeyVault:KeyId"]),
                "PiiProtection:RecipientNoticeKey requires a versionless https Key Vault key URI in KeyId that is not " +
                "the PII key, and CurrentVersionRefreshSeconds/HealthCacheSeconds between 30 and 3600.")
            .ValidateOnStart();
        services.AddAzureWorkloadCredential();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRecipientNoticeKeyWrapClient, AzureKeyVaultRecipientNoticeKeyWrapClient>();
        services.TryAddSingleton<IRecipientNoticeEnvelopeSealer, RecipientNoticeEnvelopeSealer>();
        services.AddSingleton<RecipientNoticeKeyHealthCheck>();
        services.AddHealthChecks().AddCheck<RecipientNoticeKeyHealthCheck>(HealthCheckName, tags: ["ready"]);
        return services;
    }

    private sealed class RecipientNoticeKeyRegistrationMarker;
}

/// <summary>Whether a module selected the recipient-notice key, read from its bound options.</summary>
public sealed class RecipientNoticeKeySelection(Func<bool> isSelected)
{
    /// <summary>A module whose own options are invalid reports not selected: its own validation stops the host.</summary>
    public bool IsSelected
    {
        get
        {
            try
            {
                return isSelected();
            }
            catch (OptionsValidationException)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// Readiness for the recipient-notice key: the current version resolves and a random probe key is wrapped with its
/// public part. The result is cached; any failure reports unhealthy (fail closed) and is retried on the next probe.
/// </summary>
public sealed class RecipientNoticeKeyHealthCheck(
    IServiceProvider services,
    IEnumerable<RecipientNoticeKeySelection> selections,
    IOptions<RecipientNoticeKeyOptions> options,
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
            return HealthCheckResult.Healthy("recipient_notice_key_not_selected");
        }

        var cacheFor = TimeSpan.FromSeconds(options.Value.HealthCacheSeconds);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (_last is { Healthy: true } last && now - last.CheckedAt < cacheFor)
            {
                return HealthCheckResult.Healthy("recipient_notice_key_available");
            }

            var healthy = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            _last = (now, healthy);
            return healthy
                ? HealthCheckResult.Healthy("recipient_notice_key_available")
                : HealthCheckResult.Unhealthy("recipient_notice_key_unavailable");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        var probe = RandomNumberGenerator.GetBytes(32);
        try
        {
            var client = services.GetRequiredService<IRecipientNoticeKeyWrapClient>();
            var version = await client.GetCurrentKeyVersionAsync(cancellationToken).ConfigureAwait(false);
            var wrapped = await client.WrapKeyAsync(version, probe, cancellationToken).ConfigureAwait(false);
            return wrapped is { Length: > 0 };
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
        }
    }
}
