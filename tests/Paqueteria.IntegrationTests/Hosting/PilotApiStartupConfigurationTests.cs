using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.IntegrationTests.Orders;
using Realtime.Application.Configuration;

namespace Paqueteria.IntegrationTests.Hosting;

/// <summary>
/// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the API composed with exactly the settings the pilot gives it
/// (<c>deploy/azure/pilot/apps.bicep</c> <c>apiEnv</c> plus <c>apps.settings.json</c>) is checked against every
/// <c>ValidateOnStart</c> rule. Each Key Vault mapping of the API is replaced by a synthetic value under its mapped
/// configuration key, as the ADP-001 Key Vault secrets source would deliver it; nothing reaches Azure or a database
/// (the connections point to a closed local port). The only failures left are the owner-pending settings listed in
/// <see cref="OwnerPendingStartFailures"/>; with stand-ins for those, the host starts.
/// </summary>
/// <remarks>
/// Before this decision the pilot API ran the realtime outbox dispatchers with no
/// <c>ConnectionStrings:PaqueteriaWorker</c> and would have refused to start; dropping the realtime mapping keeps that
/// failure class visible.
/// </remarks>
public sealed partial class PilotApiStartupConfigurationTests
{
    private const string ClosedPort = "Host=127.0.0.1;Port=1;Database=paqueteria;Password=unused;Timeout=3;Pooling=false";

    /// <summary>
    /// The start validation failures the pilot API settings still produce, each awaiting an owner decision outside
    /// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12. <c>Drivers:Provider</c> and <c>Routing:Provider</c> are
    /// <c>PostgreSql</c> in the pilot, so the API needs the per-vehicle-type driver eligibility maps (required document
    /// types and capacities); <c>apps.settings.json</c> carries none yet. When the owner supplies them this list
    /// becomes empty and the pilot settings must start as they are.
    /// </summary>
    private static readonly string[] OwnerPendingStartFailures =
    [
        "Drivers:Eligibility must define a valid document and capacity policy for every vehicle type.",
        "Drivers:Eligibility must define the DSP-001 policy used by PostgreSQL routing.",
    ];

    /// <summary>
    /// OBS-003: the pilot keeps the export off with a valid <c>TelemetryExport</c> section, and maps the organization
    /// pseudonym key from Key Vault under the version it declares. The hosts read the section while they compose, so it
    /// is checked here directly.
    /// </summary>
    [Fact]
    public void Pilot_api_keeps_the_telemetry_export_off_and_maps_the_pseudonym_key()
    {
        var settings = PilotApiSettings.Load();
        var resolved = settings.Resolved();
        var export = Paqueteria.Infrastructure.Observability.TelemetryExportSettings.Read(
            new ConfigurationBuilder().AddInMemoryCollection(resolved).Build());

        Assert.False(export.Enabled);
        Assert.Null(export.ConnectionString);

        // The reviewed PR that flips the literal turns it on with the component's connection string as it is.
        var flipped = new Dictionary<string, string?>(resolved) { ["TelemetryExport:Enabled"] = "true" };
        var on = Paqueteria.Infrastructure.Observability.TelemetryExportSettings.Read(
            new ConfigurationBuilder().AddInMemoryCollection(flipped).Build());
        Assert.True(on.Enabled);
        Assert.Equal(resolved["TelemetryExport:ConnectionString"], on.ConnectionString);
        Assert.Equal("1", settings.Configuration["TelemetryPseudonym:CurrentKeyVersion"]);
        Assert.Equal("paquetenvia-telemetry-pseudonym-key-1", settings.Mappings["TelemetryPseudonym:Keys:1"]);
    }

    [Fact]
    public void Pilot_api_maps_its_own_realtime_connection_and_never_the_workers()
    {
        var settings = PilotApiSettings.Load();

        Assert.Equal("PostgreSql", settings.Configuration["Realtime:OutboxDispatcher:Provider"]);
        Assert.Equal("pg-realtime-runtime-connection", settings.Mappings["ConnectionStrings:PaqueteriaRealtime"]);
        Assert.Equal("pg-api-runtime-connection", settings.Mappings["ConnectionStrings:Paqueteria"]);
        Assert.DoesNotContain("ConnectionStrings:PaqueteriaWorker", settings.Mappings.Keys);
        Assert.DoesNotContain(settings.Mappings.Values, secret => secret.StartsWith("pg-worker-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pilot_api_settings_fail_start_validation_only_on_owner_pending_settings()
    {
        await using var api = new PilotApiFactory(PilotApiSettings.Load().Resolved());

        var failures = StartFailures(Record.Exception(() => api.Services));

        // Nothing about the realtime dispatcher or any connection string is missing any more.
        Assert.Equal(OwnerPendingStartFailures.Order(StringComparer.Ordinal), failures.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task With_the_owner_pending_settings_the_pilot_api_starts_with_its_realtime_dispatchers()
    {
        // Synthetic stand-ins for the owner-pending driver eligibility maps only; every other value is the pilot's own.
        var settings = PilotApiSettings.Load().Resolved();
        foreach (var pair in OrderTransitionGuardsApiFactory.DriverEligibilitySettings)
        {
            settings[pair.Key] = pair.Value;
        }

        await using var api = new PilotApiFactory(settings);
        var dispatcher = api.Services.GetRequiredService<IOptions<OutboxDispatcherOptions>>().Value;

        Assert.Equal(OutboxDispatcherProviderKind.PostgreSql, dispatcher.Provider);
        // SCL-007-ADAPTIVE-OUTBOX-WAIT-2026-10-13: the pilot waits 250 ms (business) or 500 ms (location) after an empty
        // claim, doubling up to 1 s on both lanes.
        Assert.Equal(250, dispatcher.Business.PollIntervalMilliseconds);
        Assert.Equal(1_000, dispatcher.Business.MaximumIdleWaitMilliseconds);
        Assert.Equal(500, dispatcher.Location.PollIntervalMilliseconds);
        Assert.Equal(1_000, dispatcher.Location.MaximumIdleWaitMilliseconds);
    }

    [Fact]
    public async Task Without_its_realtime_mapping_the_pilot_api_refuses_to_start()
    {
        var settings = PilotApiSettings.Load().Resolved();
        settings.Remove("ConnectionStrings:PaqueteriaRealtime");

        await using var api = new PilotApiFactory(settings);
        var failures = StartFailures(Record.Exception(() => api.Services));

        Assert.Contains(
            "Realtime:OutboxDispatcher:Provider=PostgreSql requires ConnectionStrings:PaqueteriaRealtime.",
            failures);
    }

    [Theory]
    [InlineData("ConnectionStrings:Paqueteria")]
    [InlineData("ConnectionStrings:PaqueteriaWorker")]
    public async Task The_pilot_api_refuses_a_realtime_connection_that_reuses_another_login(string otherKey)
    {
        var settings = PilotApiSettings.Load().Resolved();
        settings[otherKey] = Connection("pv_pilot_realtime");

        await using var api = new PilotApiFactory(settings);
        var failures = StartFailures(Record.Exception(() => api.Services));

        Assert.Contains(
            "ConnectionStrings:PaqueteriaRealtime must be a valid connection string whose login differs from " +
            "ConnectionStrings:Paqueteria and ConnectionStrings:PaqueteriaWorker.",
            failures);
    }

    /// <summary>Every option validation failure the host reported when it refused to start.</summary>
    private static string[] StartFailures(Exception? exception)
    {
        Assert.NotNull(exception);
        var failures = new List<string>();
        var pending = new Stack<Exception>([exception]);
        while (pending.TryPop(out var current))
        {
            if (current is OptionsValidationException validation)
            {
                failures.AddRange(validation.Failures);
            }
            else if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        Assert.True(failures.Count != 0, $"The host failed without an options validation failure: {exception}");
        return failures.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string Connection(string login) => ClosedPort + ";Username=" + login;

    /// <summary>The API settings the pilot deploys, read from the reviewed templates.</summary>
    private sealed partial record PilotApiSettings(
        Dictionary<string, string?> Configuration,
        Dictionary<string, string> Mappings)
    {
        public static PilotApiSettings Load()
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
                ["apiIdentity.properties.clientId"] = "00000000-0000-4000-8000-0000000000a1",
                ["vault.properties.vaultUri"] = "https://kv-pvp-synthetic.vault.azure.net/",
                // OBS-003: the connection string of the pilot Application Insights (the export stays off in the pilot).
                ["appInsights.properties.ConnectionString"] =
                    "InstrumentationKey=00000000-0000-4000-8000-0000000000c1;IngestionEndpoint=https://mexicocentral-0.in.applicationinsights.azure.com/",
            };

            var apiEnv = ApiEnvironment().Match(bicep);
            Assert.True(apiEnv.Success, "apps.bicep no longer declares apiEnv as concat(<variables>, [ ... ], businessSettings).");
            var environment = new List<(string Name, string Value)>();
            foreach (var variable in apiEnv.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var block = new Regex(@"^var " + Regex.Escape(variable) + @" = \[\n(.*?)\n\]", RegexOptions.Multiline | RegexOptions.Singleline)
                    .Match(bicep);
                Assert.True(block.Success, $"apps.bicep variable {variable} not found.");
                environment.AddRange(Entries(block.Groups[1].Value, expressions));
            }

            environment.AddRange(Entries(apiEnv.Groups[2].Value, expressions));
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
            return new PilotApiSettings(configuration, mappings);
        }

        /// <summary>
        /// The settings with the Key Vault source off and every mapped configuration key holding a synthetic value of
        /// the shape the owner or the workflow stores (README section 4).
        /// </summary>
        public Dictionary<string, string?> Resolved()
        {
            var resolved = Configuration
                .Where(pair => !pair.Key.StartsWith("KeyVaultSecrets:", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var key in Mappings.Keys)
            {
                resolved[key] = key switch
                {
                    "ConnectionStrings:Paqueteria" => Connection("pv_pilot_api"),
                    "ConnectionStrings:PaqueteriaRealtime" => Connection("pv_pilot_realtime"),
                    "AuthCenter:ClientSecret" => Derived("authcenter client", hex: true),
                    "EmailLookup:Keys:1" => Derived("email lookup key", hex: false),
                    "Locations:GoogleMaps:ApiKey" => Derived("geocoding key", hex: true),
                    "PublicTracking:LinkKeys:1" => Derived("tracking link key", hex: false),
                    "TelemetryPseudonym:Keys:1" => Derived("telemetry pseudonym key", hex: false),
                    _ => throw new Xunit.Sdk.XunitException(
                        $"The pilot API maps {key} from Key Vault; give it a synthetic value here."),
                };
            }

            return resolved;
        }

        private static string Derived(string purpose, bool hex)
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

        [GeneratedRegex(@"^var apiEnv = concat\(([\w\s,]+), \[\n(.*?)\n\], businessSettings\)", RegexOptions.Multiline | RegexOptions.Singleline)]
        private static partial Regex ApiEnvironment();

        [GeneratedRegex(@"name: '([^']+)'\s*\n\s*value: ([^\n]+)")]
        private static partial Regex EnvironmentEntry();
    }

    /// <summary>The API host in the pilot's environment (Production), composed and started with the given settings.</summary>
    private sealed class PilotApiFactory(Dictionary<string, string?> settings)
        : StartupFailureSurfacingWebApplicationFactory<Program>
    {
        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(settings["ASPNETCORE_ENVIRONMENT"] ?? throw new InvalidOperationException("no environment"));
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
        }
    }
}
