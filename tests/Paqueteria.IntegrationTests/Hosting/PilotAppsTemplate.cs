using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.IntegrationTests.Hosting;

/// <summary>
/// The settings the pilot gives one Container App: the variables <c>deploy/azure/pilot/apps.bicep</c> concatenates into
/// <c>apiEnv</c> or <c>workerEnv</c>, then <c>apps.settings.json</c>, with every deployment output replaced by a synthetic
/// value of the same shape. Shared by <see cref="PilotApiStartupConfigurationTests"/> and
/// <see cref="PilotWorkerStartupConfigurationTests"/>; nothing here reaches Azure or a database.
/// </summary>
internal static partial class PilotAppsTemplate
{
    /// <summary>A connection string to a closed local port: a host that tries to use it fails fast and reaches nothing.</summary>
    public const string ClosedPort = "Host=127.0.0.1;Port=1;Database=paqueteria;Password=unused;Timeout=3;Pooling=false";

    /// <summary>A synthetic Communication Services resource id in the shape the API validates; no real subscription.</summary>
    public const string SyntheticAcsResourceId =
        "/subscriptions/00000000-0000-4000-8000-000000000000/resourceGroups/rg-pv-pilot/providers/Microsoft.Communication/communicationServices/acs-pv-synthetic";

    /// <summary>A synthetic WhatsApp Business phone number id (digits only, as Meta gives it).</summary>
    public const string SyntheticPhoneNumberId = "100000000000001";

    /// <summary>
    /// The configuration (env names with <c>:</c> separators) of <paramref name="variable"/> and its Key Vault mappings
    /// (configuration key to secret name).
    /// </summary>
    public static (Dictionary<string, string?> Configuration, Dictionary<string, string> Mappings) Load(string variable)
    {
        var root = RepositoryRootLocator.Find(AppContext.BaseDirectory);
        var bicep = File.ReadAllText(Path.Combine(root, "deploy", "azure", "pilot", "apps.bicep"));
        var parameters = ParameterDefaults().Matches(bicep)
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);
        var expressions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["publicOrigin"] = "https://" + parameters["publicHost"],
            ["authCenterAuthority"] = parameters["authCenterAuthority"],
            ["authCenterIssuer"] = parameters["authCenterIssuer"],
            ["authCenterClientId"] = parameters["authCenterClientId"],
            ["trustedProxyNetwork"] = parameters["trustedProxyNetwork"],
            // Deployment outputs: synthetic, well-formed values of the same shape.
            ["blobServiceUri"] = "https://pvpilotsynthetic.blob.core.windows.net",
            ["piiKeyUri"] = "https://kv-pvp-synthetic.vault.azure.net/keys/paquetenvia-pii",
            ["dataProtectionKeyUri"] = "https://kv-pvp-synthetic.vault.azure.net/keys/paquetenvia-data-protection",
            // NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31: the security.bicep output of recipient-notice-kek.
            ["recipientNoticeKeyUri"] = "https://kv-pvp-synthetic.vault.azure.net/keys/recipient-notice-kek",
            ["apiIdentity.properties.clientId"] = "00000000-0000-4000-8000-0000000000a1",
            ["workerIdentity.properties.clientId"] = "00000000-0000-4000-8000-0000000000a2",
            ["vault.properties.vaultUri"] = "https://kv-pvp-synthetic.vault.azure.net/",
            // OBS-003: the connection string of the pilot Application Insights (the export stays off in the pilot).
            ["appInsights.properties.ConnectionString"] =
                "InstrumentationKey=00000000-0000-4000-8000-0000000000c1;IngestionEndpoint=https://mexicocentral-0.in.applicationinsights.azure.com/",
        };

        var environmentMatch = new Regex(
                @"^var " + Regex.Escape(variable) + @" = concat\(([\w\s,]+), \[\n(.*?)\n\], businessSettings\)",
                RegexOptions.Multiline | RegexOptions.Singleline)
            .Match(bicep);
        Assert.True(
            environmentMatch.Success,
            $"apps.bicep no longer declares {variable} as concat(<variables>, [ ... ], businessSettings).");
        var environment = new List<(string Name, string Value)>();
        foreach (var name in environmentMatch.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var block = new Regex(@"^var " + Regex.Escape(name) + @" = \[\n(.*?)\n\]", RegexOptions.Multiline | RegexOptions.Singleline)
                .Match(bicep);
            Assert.True(block.Success, $"apps.bicep variable {name} not found.");
            environment.AddRange(Entries(block.Groups[1].Value, expressions));
        }

        environment.AddRange(Entries(environmentMatch.Groups[2].Value, expressions));
        using (var businessSettings = JsonDocument.Parse(
                   File.ReadAllText(Path.Combine(root, "deploy", "azure", "pilot", "apps.settings.json"))))
        {
            environment.AddRange(businessSettings.RootElement.EnumerateArray()
                .Select(entry => (entry.GetProperty("name").GetString()!, entry.GetProperty("value").GetString()!)));
        }

        var configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in environment)
        {
            Assert.False(configuration.ContainsKey(name.Replace("__", ":", StringComparison.Ordinal)), $"{name} is set twice.");
            configuration[name.Replace("__", ":", StringComparison.Ordinal)] = value;
        }

        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; configuration.ContainsKey($"KeyVaultSecrets:Mappings:{index}:SecretName"); index++)
        {
            mappings[configuration[$"KeyVaultSecrets:Mappings:{index}:ConfigurationKey"]!] =
                configuration[$"KeyVaultSecrets:Mappings:{index}:SecretName"]!;
        }

        Assert.NotEmpty(mappings);
        return (configuration, mappings);
    }

    /// <summary>
    /// The configuration with the Key Vault source off and every mapped key holding <paramref name="synthetic"/>'s value of
    /// the shape the owner or the workflow stores (README section 4, guia-owner-avisos.md).
    /// </summary>
    public static Dictionary<string, string?> Resolve(
        IReadOnlyDictionary<string, string?> configuration,
        IReadOnlyDictionary<string, string> mappings,
        Func<string, string?> synthetic)
    {
        var resolved = configuration
            .Where(pair => !pair.Key.StartsWith("KeyVaultSecrets:", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var key in mappings.Keys)
        {
            resolved[key] = synthetic(key) ?? throw new Xunit.Sdk.XunitException(
                $"The pilot maps {key} from Key Vault; give it a synthetic value here.");
        }

        return resolved;
    }

    /// <summary>The synthetic value of a secret both hosts map the same way, or null for a host-specific one.</summary>
    public static string? SharedSecret(string configurationKey) => configurationKey switch
    {
        "PublicTracking:LinkKeys:1" => Derived("tracking link key", hex: false),
        "TelemetryPseudonym:Keys:1" => Derived("telemetry pseudonym key", hex: false),
        // NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31: 32 random bytes in base64 (generated by the workflow).
        "ContactLookup:Keys:1" => Derived("contact lookup key", hex: false),
        _ => null,
    };

    public static string Connection(string login) => ClosedPort + ";Username=" + login;

    /// <summary>A deterministic, non-secret stand-in: SHA-256 of a purpose, as hex (64 characters) or base64 (32 bytes).</summary>
    public static string Derived(string purpose, bool hex)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("paquetenvia rtm pilot startup " + purpose));
        return hex ? Convert.ToHexStringLower(bytes) : Convert.ToBase64String(bytes);
    }

    private static IEnumerable<(string Name, string Value)> Entries(string block, IReadOnlyDictionary<string, string> expressions)
    {
        foreach (Match entry in EnvironmentEntry().Matches(block))
        {
            var raw = entry.Groups[2].Value.Trim();
            if (raw.StartsWith('\'') && raw.EndsWith('\''))
            {
                yield return (entry.Groups[1].Value, raw[1..^1]);
                continue;
            }

            Assert.True(
                expressions.TryGetValue(raw, out var value),
                $"apps.bicep sets {entry.Groups[1].Value} from {raw}; give it a synthetic value here.");
            yield return (entry.Groups[1].Value, value!);
        }
    }

    [GeneratedRegex(@"^param (\w+) string = '([^']*)'", RegexOptions.Multiline)]
    private static partial Regex ParameterDefaults();

    [GeneratedRegex(@"name: '([^']+)'\s*\n\s*value: ([^\n]+)")]
    private static partial Regex EnvironmentEntry();
}
