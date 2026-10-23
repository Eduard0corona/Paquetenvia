using System.Collections.Concurrent;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Cloud;

namespace Paqueteria.Infrastructure.Security.Pii;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (E4-P2, "Llave propia para avisos (Recommended)"; AI-24
/// <c>recipient_notices.contact.roles</c>; AI-08 NTF-005; NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 L4 choice 5): the Worker's
/// half of the recipient-notice key <c>recipient-notice-kek</c>. It only unwraps: there is no wrap, no current-version
/// read and no access to the PII key, so the Worker opens the notice copies of a destination and nothing else.
/// </summary>
public interface IRecipientNoticeKeyUnwrapClient
{
    Task<byte[]> UnwrapKeyAsync(string keyVersion, byte[] wrappedKey, CancellationToken cancellationToken);
}

/// <summary>
/// Opens one notice copy (ADP-001 envelope format v1 under the recipient-notice key, bound to the owner organization, the
/// location row and the purpose) in memory. A copy that is malformed, altered, bound elsewhere or under a key version the
/// Worker cannot open throws <see cref="PiiCiphertextRejectedException"/> (permanent); an unreachable Key Vault throws
/// <see cref="PiiProtectionUnavailableException"/> (transient). No exception message carries a value.
/// </summary>
public interface IRecipientNoticeEnvelopeOpener
{
    Task<string> OpenAsync(
        PiiBinding binding,
        string purpose,
        byte[] ciphertext,
        string keyVersion,
        CancellationToken cancellationToken);
}

public sealed class RecipientNoticeEnvelopeOpener(IRecipientNoticeKeyUnwrapClient keyUnwrapClient)
    : IRecipientNoticeEnvelopeOpener
{
    public async Task<string> OpenAsync(
        PiiBinding binding,
        string purpose,
        byte[] ciphertext,
        string keyVersion,
        CancellationToken cancellationToken)
    {
        PiiEnvelopeFormat.ValidatePurpose(purpose);
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!binding.IsValid ||
            !PiiEnvelopeFormat.IsValidKeyVersion(keyVersion) ||
            !PiiEnvelopeFormat.TryParse(ciphertext, out var envelope))
        {
            throw new PiiCiphertextRejectedException();
        }

        var dataKey = await keyUnwrapClient.UnwrapKeyAsync(keyVersion, envelope.WrappedKey, cancellationToken)
            .ConfigureAwait(false);
        return PiiEnvelopeFormat.Open(binding, purpose, keyVersion, envelope, dataKey);
    }
}

/// <summary>
/// The recipient-notice key in Azure Key Vault, reached with the workload managed identity: one <c>unwrapKey</c>
/// (RSA-OAEP-256) call on the exact version a copy names (<c>akv:{key name}/{version}</c>, the configured key only). A
/// version that is not of the configured key, or that Key Vault refuses or does not know (400, 403, 404), is a rejected
/// copy; a throttled, failing or unreachable Key Vault is unavailable and the send is retried.
/// </summary>
public sealed class AzureKeyVaultRecipientNoticeKeyUnwrapClient : IRecipientNoticeKeyUnwrapClient
{
    private readonly Uri _vaultUri;
    private readonly string _keyName;
    private readonly TokenCredential _credential;
    private readonly CryptographyClientOptions _options;
    private readonly ConcurrentDictionary<string, CryptographyClient> _clients = new(StringComparer.Ordinal);

    public AzureKeyVaultRecipientNoticeKeyUnwrapClient(
        IOptions<RecipientNoticeKeyOptions> options,
        TokenCredential credential)
        : this(options, credential, transport: null)
    {
    }

    /// <summary>Test seam: routes the Key Vault SDK through <paramref name="transport"/>.</summary>
    internal AzureKeyVaultRecipientNoticeKeyUnwrapClient(
        IOptions<RecipientNoticeKeyOptions> options,
        TokenCredential credential,
        HttpPipelineTransport? transport)
    {
        if (!KeyVaultKeyIds.TryParseVersionless(options.Value.KeyId, out var vaultUri, out var keyName))
        {
            throw new InvalidOperationException(
                "PiiProtection:RecipientNoticeKey:KeyId must be a versionless Key Vault key URI.");
        }

        _vaultUri = vaultUri;
        _keyName = keyName;
        _credential = credential;
        _options = Configure(new CryptographyClientOptions(), transport);
    }

    public async Task<byte[]> UnwrapKeyAsync(string keyVersion, byte[] wrappedKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wrappedKey);
        if (!AzureKeyVaultRecipientNoticeKeyWrapClient.TryParseKeyVersion(keyVersion, out var keyName, out var version) ||
            !string.Equals(keyName, _keyName, StringComparison.Ordinal))
        {
            throw new PiiCiphertextRejectedException();
        }

        var client = _clients.GetOrAdd(version, value => new CryptographyClient(
            new Uri(_vaultUri, $"keys/{Uri.EscapeDataString(_keyName)}/{value}"),
            _credential,
            _options));
        try
        {
            var result = await client.UnwrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, wrappedKey, cancellationToken)
                .ConfigureAwait(false);
            return result.Key;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RequestFailedException exception) when (exception.Status is 400 or 403 or 404)
        {
            throw new PiiCiphertextRejectedException();
        }
        catch (Exception exception)
        {
            throw new PiiProtectionUnavailableException(exception);
        }
    }

    private static CryptographyClientOptions Configure(CryptographyClientOptions options, HttpPipelineTransport? transport)
    {
        if (transport is not null)
        {
            options.Transport = transport;
        }

        options.Retry.MaxRetries = 2;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        options.Diagnostics.IsLoggingContentEnabled = false;
        return options.WithoutDistributedTracing();
    }
}

public static class RecipientNoticeKeyOpenerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Worker's unwrap-only opener of the recipient-notice key. <paramref name="isSelected"/> is evaluated
    /// against the caller's own bound options at runtime: when selected (a real messaging provider sends recipient
    /// notices), <c>PiiProtection:RecipientNoticeKey:KeyId</c> is validated on start; otherwise nothing Azure-related is
    /// ever constructed. Nothing here wraps, and nothing here reaches the PII key.
    /// </summary>
    public static IServiceCollection AddRecipientNoticeKeyOpener(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<IServiceProvider, bool> isSelected)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(isSelected);
        services.AddSingleton(serviceProvider => new RecipientNoticeKeyOpenerSelection(() => isSelected(serviceProvider)));
        services.AddOptions<RecipientNoticeKeyOptions>()
            .Bind(configuration.GetSection(RecipientNoticeKeyOptions.SectionName))
            .Validate<IEnumerable<RecipientNoticeKeyOpenerSelection>>(
                (options, selections) => !selections.Any(selection => selection.IsSelected) ||
                    RecipientNoticeKeyOptionsValidator.IsValid(
                        options,
                        configuration[$"{PiiProtectionOptions.SectionName}:AzureKeyVault:KeyId"]),
                "PiiProtection:RecipientNoticeKey requires a versionless https Key Vault key URI in KeyId that is not " +
                "the PII key, and CurrentVersionRefreshSeconds/HealthCacheSeconds between 30 and 3600.")
            .ValidateOnStart();
        services.AddAzureWorkloadCredential();
        services.TryAddSingleton<IRecipientNoticeKeyUnwrapClient, AzureKeyVaultRecipientNoticeKeyUnwrapClient>();
        services.TryAddSingleton<IRecipientNoticeEnvelopeOpener, RecipientNoticeEnvelopeOpener>();
        return services;
    }
}

/// <summary>Whether recipient notices need the Key Vault opener, read from the caller's bound options.</summary>
public sealed class RecipientNoticeKeyOpenerSelection(Func<bool> isSelected)
{
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
