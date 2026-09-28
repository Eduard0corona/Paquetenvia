using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Cloud;

namespace Paqueteria.Infrastructure.Security.Pii;

/// <summary>
/// ADP-001-PII-KEYVAULT-ENVELOPE: the RSA key-encryption key lives in Azure Key Vault and is
/// reached with the workload managed identity. The current version is read from Key Vault (never
/// from configuration or the client) and cached for <c>CurrentVersionRefreshSeconds</c>; wrap and
/// unwrap always address one exact version, so rows protected before a rotation stay readable as
/// long as their version stays enabled.
/// </summary>
/// <remarks>
/// The persisted key version is <c>akv:{key name}/{Key Vault version}</c>. Unwrap accepts only the
/// configured key name and a Key Vault version identifier, so a tampered row cannot steer the
/// workload towards another key.
/// </remarks>
public sealed partial class AzureKeyVaultPiiKeyWrapClient : IPiiKeyWrapClient
{
    internal const string VersionPrefix = "akv:";

    private readonly Uri _vaultUri;
    private readonly string _keyName;
    private readonly TokenCredential _credential;
    private readonly KeyClient _keyClient;
    private readonly CryptographyClientOptions _cryptographyOptions;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CryptographyClient> _cryptographyClients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CurrentVersion? _current;

    public AzureKeyVaultPiiKeyWrapClient(
        IOptions<PiiProtectionOptions> options,
        TokenCredential credential,
        TimeProvider timeProvider)
        : this(options, credential, timeProvider, transport: null)
    {
    }

    /// <summary>Test seam: routes the Key Vault SDK through <paramref name="transport"/>.</summary>
    internal AzureKeyVaultPiiKeyWrapClient(
        IOptions<PiiProtectionOptions> options,
        TokenCredential credential,
        TimeProvider timeProvider,
        HttpPipelineTransport? transport)
    {
        var settings = options.Value.AzureKeyVault;
        if (!KeyVaultKeyIds.TryParseVersionless(settings.KeyId, out var vaultUri, out var keyName))
        {
            throw new InvalidOperationException("PiiProtection:AzureKeyVault:KeyId must be a versionless Key Vault key URI.");
        }

        _vaultUri = vaultUri;
        _keyName = keyName;
        _credential = credential;
        _timeProvider = timeProvider;
        _refreshInterval = TimeSpan.FromSeconds(settings.CurrentVersionRefreshSeconds);
        _keyClient = new KeyClient(vaultUri, credential, Configure(new KeyClientOptions(), transport));
        _cryptographyOptions = Configure(new CryptographyClientOptions(), transport);
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
            var properties = key.Properties;
            if (properties.Enabled != true ||
                properties.ExpiresOn is { } expires && expires <= now ||
                properties.NotBefore is { } notBefore && notBefore > now ||
                (key.KeyType != KeyType.Rsa && key.KeyType != KeyType.RsaHsm) ||
                key.KeyOperations.Count > 0 &&
                (!key.KeyOperations.Contains(KeyOperation.WrapKey) || !key.KeyOperations.Contains(KeyOperation.UnwrapKey)) ||
                !IsKeyVaultVersion(properties.Version))
            {
                throw new PiiProtectionUnavailableException();
            }

            var value = VersionPrefix + _keyName + "/" + properties.Version;
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
        var client = ClientFor(keyVersion);
        var result = await client.WrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, dataKey, cancellationToken)
            .ConfigureAwait(false);
        return result.EncryptedKey;
    }

    public async Task<byte[]> UnwrapKeyAsync(string keyVersion, byte[] wrappedKey, CancellationToken cancellationToken)
    {
        var client = ClientFor(keyVersion);
        var result = await client.UnwrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, wrappedKey, cancellationToken)
            .ConfigureAwait(false);
        return result.Key;
    }

    private CryptographyClient ClientFor(string keyVersion)
    {
        if (!TryParseKeyVersion(keyVersion, out var keyName, out var version) ||
            !string.Equals(keyName, _keyName, StringComparison.Ordinal))
        {
            throw new PiiProtectionUnavailableException();
        }

        return _cryptographyClients.GetOrAdd(version, value => new CryptographyClient(
            new Uri(_vaultUri, $"keys/{Uri.EscapeDataString(_keyName)}/{value}"),
            _credential,
            _cryptographyOptions));
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

    private static bool IsKeyVaultVersion(string? value) => value is not null && KeyVaultVersionPattern().IsMatch(value);

    private static T Configure<T>(T options, HttpPipelineTransport? transport)
        where T : ClientOptions
    {
        if (transport is not null)
        {
            options.Transport = transport;
        }

        options.Retry.MaxRetries = 2;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        options.Diagnostics.IsLoggingContentEnabled = false;
        return options;
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyVaultVersionPattern();

    private sealed record CurrentVersion(string Value, DateTimeOffset ReadAt);
}
