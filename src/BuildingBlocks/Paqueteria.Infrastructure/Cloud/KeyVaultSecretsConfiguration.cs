using System.Net;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace Paqueteria.Infrastructure.Cloud;

/// <summary><c>KeyVaultSecrets</c>: the opt-in, allowlisted Key Vault configuration source.</summary>
public sealed class KeyVaultSecretsOptions
{
    public const string SectionName = "KeyVaultSecrets";

    /// <summary><c>https://{vault}.vault.azure.net/</c>. Empty means the source is off.</summary>
    public string VaultUri { get; set; } = string.Empty;

    public List<KeyVaultSecretMapping> Mappings { get; set; } = [];

    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class KeyVaultSecretMapping
{
    public string SecretName { get; set; } = string.Empty;

    public string ConfigurationKey { get; set; } = string.Empty;
}

/// <summary>Reads one secret's current value. Production: <see cref="AzureKeyVaultSecretReader"/>.</summary>
public interface IKeyVaultSecretReader
{
    Task<string> GetSecretValueAsync(string secretName, CancellationToken cancellationToken);
}

/// <summary>
/// The host could not start because a configured Key Vault secret is missing, disabled or
/// unreadable, or the configuration of the source is invalid. The message names the secret and the
/// configuration key, never a value.
/// </summary>
public sealed class KeyVaultSecretsStartupException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// PILOT-KEYVAULT-PRIVATE-APP-READ: API, Worker and the DatabaseMigrator read their own secrets
/// from a Key Vault behind its firewall (Container Apps platform Key Vault references do not work
/// with <c>defaultAction=Deny</c>). Only the secrets listed in <c>KeyVaultSecrets:Mappings</c> are
/// read, once, at startup, with the workload managed identity; each value is placed under its
/// configuration key and overrides earlier sources. Any failure stops the host before it starts.
/// The source is off unless <c>KeyVaultSecrets:VaultUri</c> is set.
/// </summary>
public static partial class KeyVaultSecretsConfiguration
{
    /// <summary>
    /// Adds the allowlisted Key Vault secrets to <paramref name="builder"/> when
    /// <c>KeyVaultSecrets:VaultUri</c> is configured by the sources already added.
    /// </summary>
    /// <param name="readerFactory">Test seam; production builds the managed-identity reader.</param>
    public static IConfigurationBuilder AddPaqueteriaKeyVaultSecrets(
        this IConfigurationBuilder builder,
        Func<Uri, TimeSpan, IKeyVaultSecretReader>? readerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var current = builder as IConfiguration ?? builder.Build();
        var options = new KeyVaultSecretsOptions();
        current.GetSection(KeyVaultSecretsOptions.SectionName).Bind(options);
        if (string.IsNullOrWhiteSpace(options.VaultUri))
        {
            return builder;
        }

        var vaultUri = Validate(options);
        var timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        var reader = readerFactory?.Invoke(vaultUri, timeout) ??
            new AzureKeyVaultSecretReader(
                vaultUri,
                AzureWorkloadCredential.Create(Environment.GetEnvironmentVariable(AzureWorkloadCredential.ClientIdVariable)),
                timeout);
        var values = LoadAsync(options.Mappings, reader, timeout).GetAwaiter().GetResult();
        builder.Add(new MemoryConfigurationSource { InitialData = values });
        return builder;
    }

    /// <summary>
    /// Only the mapped values (configuration key → secret value), read with the source configured in
    /// <paramref name="settings"/>; empty when the source is off. The DatabaseMigrator overlays them
    /// on its live environment variables, so an unmapped name always reads the current environment.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LoadMappedSecrets(
        IConfiguration settings,
        Func<Uri, TimeSpan, IKeyVaultSecretReader>? readerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = new KeyVaultSecretsOptions();
        settings.GetSection(KeyVaultSecretsOptions.SectionName).Bind(options);
        if (string.IsNullOrWhiteSpace(options.VaultUri))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var loaded = new ConfigurationBuilder()
            .AddConfiguration(settings)
            .AddPaqueteriaKeyVaultSecrets(readerFactory)
            .Build();
        return options.Mappings.ToDictionary(
            mapping => mapping.ConfigurationKey,
            mapping => loaded[mapping.ConfigurationKey]!,
            StringComparer.OrdinalIgnoreCase);
    }

    private static Uri Validate(KeyVaultSecretsOptions options)
    {
        if (!Uri.TryCreate(options.VaultUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new KeyVaultSecretsStartupException(
                "KeyVaultSecrets:VaultUri must be an https vault URI without path, query or port.");
        }

        if (options.TimeoutSeconds is < 5 or > 120)
        {
            throw new KeyVaultSecretsStartupException("KeyVaultSecrets:TimeoutSeconds must be between 5 and 120.");
        }

        if (options.Mappings is not { Count: > 0 })
        {
            throw new KeyVaultSecretsStartupException(
                "KeyVaultSecrets:VaultUri is set but KeyVaultSecrets:Mappings lists no secret; nothing is loaded implicitly.");
        }

        var configurationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in options.Mappings)
        {
            if (mapping is not { SecretName: { } secretName, ConfigurationKey: { } configurationKey } ||
                !SecretNamePattern().IsMatch(secretName) ||
                !ConfigurationKeyPattern().IsMatch(configurationKey) ||
                configurationKey.StartsWith(KeyVaultSecretsOptions.SectionName, StringComparison.OrdinalIgnoreCase))
            {
                throw new KeyVaultSecretsStartupException(
                    "Each KeyVaultSecrets:Mappings entry needs an exact SecretName ([0-9A-Za-z-], 1-127) and a ConfigurationKey outside KeyVaultSecrets.");
            }

            // One secret may feed several configuration keys (for example the Worker connection under
            // ConnectionStrings:PaqueteriaWorker and ConnectionStrings:Paqueteria), each listed
            // explicitly; a configuration key may be fed by one secret only.
            if (!configurationKeys.Add(configurationKey))
            {
                throw new KeyVaultSecretsStartupException(
                    $"KeyVaultSecrets:Mappings repeats the configuration key '{configurationKey}'.");
            }
        }

        return new Uri($"https://{uri.Host}/");
    }

    private static async Task<Dictionary<string, string?>> LoadAsync(
        IReadOnlyList<KeyVaultSecretMapping> mappings,
        IKeyVaultSecretReader reader,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var read = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings)
        {
            string value;
            try
            {
                // Each distinct secret is read once, however many configuration keys it feeds.
                if (!read.TryGetValue(mapping.SecretName, out var cached))
                {
                    cached = await reader.GetSecretValueAsync(mapping.SecretName, cancellation.Token).ConfigureAwait(false);
                    read[mapping.SecretName] = cached;
                }

                value = cached;
            }
            catch (Exception exception)
            {
                throw new KeyVaultSecretsStartupException(
                    $"The Key Vault secret '{mapping.SecretName}' for '{mapping.ConfigurationKey}' could not be read ({Describe(exception)}); the host does not start.",
                    exception is RequestFailedException ? null : exception);
            }

            if (string.IsNullOrEmpty(value))
            {
                throw new KeyVaultSecretsStartupException(
                    $"The Key Vault secret '{mapping.SecretName}' for '{mapping.ConfigurationKey}' is empty; the host does not start.");
            }

            values[mapping.ConfigurationKey] = value;
        }

        return values;
    }

    /// <summary>A value-free description: the status and error code only, never the response body.</summary>
    private static string Describe(Exception exception) => exception switch
    {
        RequestFailedException failed => $"status {failed.Status}, {failed.ErrorCode ?? "no error code"}",
        OperationCanceledException => "timed out",
        _ => exception.GetType().Name,
    };

    [GeneratedRegex("^[0-9A-Za-z-]{1,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretNamePattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]+(:[A-Za-z0-9_.-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigurationKeyPattern();
}

/// <summary>Reads the latest enabled version of one secret with the workload managed identity.</summary>
public sealed class AzureKeyVaultSecretReader : IKeyVaultSecretReader
{
    private readonly SecretClient _client;

    public AzureKeyVaultSecretReader(Uri vaultUri, TokenCredential credential, TimeSpan timeout)
        : this(vaultUri, credential, timeout, transport: null)
    {
    }

    /// <summary>Test seam: routes the SDK through <paramref name="transport"/>.</summary>
    internal AzureKeyVaultSecretReader(
        Uri vaultUri,
        TokenCredential credential,
        TimeSpan timeout,
        HttpPipelineTransport? transport)
    {
        var options = new SecretClientOptions();
        options.Retry.MaxRetries = 2;
        options.Retry.NetworkTimeout = timeout;
        options.Diagnostics.IsLoggingContentEnabled = false;
        if (transport is not null)
        {
            options.Transport = transport;
        }

        _client = new SecretClient(vaultUri, credential, options);
    }

    public async Task<string> GetSecretValueAsync(string secretName, CancellationToken cancellationToken)
    {
        KeyVaultSecret secret = await _client.GetSecretAsync(secretName, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (secret.Properties.Enabled != true)
        {
            throw new RequestFailedException((int)HttpStatusCode.Forbidden, "The secret is disabled.", "SecretDisabled", null);
        }

        return secret.Value;
    }
}
