using System.Text.Json;
using System.Text.Json.Nodes;
using Dispatch.Infrastructure;
using Drivers.Infrastructure;
using Locations.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Notifications.Infrastructure.RecipientNotices.Webhooks;
using Orders.Infrastructure.Orders;
using Paqueteria.Application.Security;
using Realtime.Application.Configuration;
using Routing.Infrastructure;

namespace Paqueteria.IntegrationTests.Hosting;

/// <summary>
/// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the API composed with exactly the settings the pilot gives it
/// (<c>deploy/azure/pilot/apps.bicep</c> <c>apiEnv</c> plus <c>apps.settings.json</c>) is checked against every
/// <c>ValidateOnStart</c> rule. Each Key Vault mapping of the API is replaced by a synthetic value under its mapped
/// configuration key, as the ADP-001 Key Vault secrets source would deliver it; nothing reaches Azure or a database
/// (the connections point to a closed local port). Since GATE-010-VEHICLE-ELIGIBILITY-PILOT-2026-10-20 the settings
/// carry the owner's driver eligibility maps, so the host starts with exactly the pilot's settings.
/// </summary>
/// <remarks>
/// Before this decision the pilot API ran the realtime outbox dispatchers with no
/// <c>ConnectionStrings:PaqueteriaWorker</c> and would have refused to start; dropping the realtime mapping keeps that
/// failure class visible.
/// </remarks>
public sealed class PilotApiStartupConfigurationTests
{
    /// <summary>
    /// The start validation failures the pilot API produces without its driver eligibility maps: <c>Drivers:Provider</c>
    /// and <c>Routing:Provider</c> are <c>PostgreSql</c> in the pilot, so both validate the per-vehicle-type required
    /// document types and capacities that <c>apps.settings.json</c> carries
    /// (GATE-010-VEHICLE-ELIGIBILITY-PILOT-2026-10-20).
    /// </summary>
    private static readonly string[] MissingEligibilityStartFailures =
    [
        "Drivers:Eligibility must define a valid document and capacity policy for every vehicle type.",
        "Drivers:Eligibility must define the DSP-001 policy used by PostgreSQL routing.",
    ];

    private const string EligibilitySection = "Drivers:Eligibility";

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

    /// <summary>
    /// NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 (AI-08 NTF-008; AI-24 <c>recipient_notices.pilot_wiring</c>): the API maps
    /// the contact lookup key and the webhook values (each secret with its previous slot), never the WhatsApp token; it
    /// names the recipient-notice key and sets no recipient-notice switch, no messaging setting and no
    /// <c>Locations:NoticeKeyProtector</c>, so no notice copy is written until the gate-closing change (W4).
    /// </summary>
    [Fact]
    public void Pilot_api_maps_the_recipient_notice_secrets_with_every_switch_off()
    {
        var settings = PilotApiSettings.Load();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ContactLookup:Keys:1"] = "paquetenvia-contact-lookup-key-1",
            ["Messaging:WhatsApp:Webhook:AppSecret"] = "whatsapp-app-secret",
            ["Messaging:WhatsApp:Webhook:PreviousAppSecret"] = "whatsapp-app-secret-previous",
            ["Messaging:WhatsApp:Webhook:VerifyToken"] = "whatsapp-webhook-verify-token",
            ["Messaging:WhatsApp:Webhook:PreviousVerifyToken"] = "whatsapp-webhook-verify-token-previous",
            ["Messaging:Email:Events:Secret"] = "acs-email-events-secret",
            ["Messaging:Email:Events:PreviousSecret"] = "acs-email-events-secret-previous",
            ["Messaging:WhatsApp:Webhook:PhoneNumberId"] = "whatsapp-phone-number-id",
            ["Messaging:Email:Events:SourceResourceId"] = "acs-communication-resource-id",
        };
        foreach (var (key, secret) in expected)
        {
            Assert.Equal(secret, settings.Mappings[key]);
        }

        Assert.DoesNotContain("whatsapp-cloud-api-token", settings.Mappings.Values);
        Assert.DoesNotContain("Messaging:WhatsApp:MetaCloudApi:AccessToken", settings.Mappings.Keys);
        Assert.Equal("1", settings.Configuration["ContactLookup:CurrentKeyVersion"]);
        Assert.Equal(
            "https://kv-pvp-synthetic.vault.azure.net/keys/recipient-notice-kek",
            settings.Configuration["PiiProtection:RecipientNoticeKey:KeyId"]);
        Assert.NotEqual(
            settings.Configuration["PiiProtection:AzureKeyVault:KeyId"],
            settings.Configuration["PiiProtection:RecipientNoticeKey:KeyId"]);
        Assert.DoesNotContain(settings.Configuration.Keys, key =>
            key.StartsWith("Messaging:", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Notifications:RecipientNotices:", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Locations:NoticeKeyProtector", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_pilot_api_starts_with_the_contact_lookup_key_no_notice_copy_and_every_webhook_off()
    {
        await using var api = new PilotApiFactory(PilotApiSettings.Load().Resolved());

        Assert.Equal(
            LocationNoticeKeyProtectorKind.Disabled,
            api.Services.GetRequiredService<IOptions<LocationsOptions>>().Value.NoticeKeyProtector);
        Assert.False(api.Services.GetRequiredService<IOptions<WhatsAppWebhookOptions>>().Value.Enabled);
        Assert.False(api.Services.GetRequiredService<IOptions<EmailEventsOptions>>().Value.Enabled);
        Assert.True(api.Services.GetRequiredService<IRecipientContactLookup>().IsAvailable);
    }

    [Fact]
    public async Task The_pilot_api_starts_with_its_own_settings_and_its_realtime_dispatchers()
    {
        // GATE-010-VEHICLE-ELIGIBILITY-PILOT-2026-10-20: no stand-in any more; every value is the pilot's own.
        await using var api = new PilotApiFactory(PilotApiSettings.Load().Resolved());
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
    public async Task Every_module_of_the_pilot_api_binds_exactly_the_reviewed_driver_eligibility_maps()
    {
        // Drivers (DSP-001), Routing (RTE-001), Dispatch (DSP-002) and Orders (ORD-002) each bind Drivers:Eligibility on
        // their own; all four must hold exactly the entries of apps.settings.json, so they never decide differently.
        var settings = PilotApiSettings.Load();
        var reviewed = settings.Configuration
            .Where(pair => pair.Key.StartsWith(EligibilitySection + ":", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key + "=" + pair.Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(EligibilitySection + ":RequiredDocumentTypesByVehicleType:WALKER:0=IDENTITY", reviewed);
        Assert.DoesNotContain(reviewed, entry => entry.Contains(":NonExpiringDocumentTypes:", StringComparison.Ordinal));

        await using var api = new PilotApiFactory(settings.Resolved());
        var bound = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Drivers"] = Flatten(api.Services.GetRequiredService<IOptions<DriversOptions>>().Value.Eligibility),
            ["Routing"] = Flatten(api.Services.GetRequiredService<IOptions<RoutingDriverEligibilityOptions>>().Value),
            ["Dispatch"] = Flatten(api.Services.GetRequiredService<IOptions<DispatchDriverEligibilityOptions>>().Value),
            ["Orders"] = Flatten(
                api.Services.GetRequiredService<IOptions<OrderTransitionDriverEligibilityOptions>>().Value),
        };

        foreach (var (module, entries) in bound)
        {
            Assert.True(
                reviewed.SetEquals(entries),
                $"{module} binds Drivers:Eligibility differently from apps.settings.json: " +
                string.Join(", ", entries.Except(reviewed).Concat(reviewed.Except(entries)).Order(StringComparer.Ordinal)));
        }
    }

    [Fact]
    public async Task Without_its_driver_eligibility_maps_the_pilot_api_refuses_to_start()
    {
        var settings = PilotApiSettings.Load().Resolved();
        foreach (var key in settings.Keys
                     .Where(key => key.StartsWith(EligibilitySection + ":", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            settings.Remove(key);
        }

        await using var api = new PilotApiFactory(settings);
        var failures = StartFailures(Record.Exception(() => api.Services));

        Assert.Equal(
            MissingEligibilityStartFailures.Order(StringComparer.Ordinal),
            failures.Order(StringComparer.Ordinal));
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

    private static string Connection(string login) => PilotAppsTemplate.Connection(login);

    /// <summary>
    /// A bound eligibility options object as <c>Drivers:Eligibility:...=value</c> entries, the way configuration spells
    /// them (lists by index, booleans in lowercase); empty lists and maps give no entry.
    /// </summary>
    private static HashSet<string> Flatten<TOptions>(TOptions options)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);
        Visit(JsonSerializer.SerializeToNode(options), EligibilitySection);
        return entries;

        void Visit(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject members:
                    foreach (var (name, member) in members)
                    {
                        Visit(member, path + ":" + name);
                    }

                    break;
                case JsonArray items:
                    for (var index = 0; index < items.Count; index++)
                    {
                        Visit(items[index], path + ":" + index);
                    }

                    break;
                case JsonValue value:
                    entries.Add(path + "=" + (value.GetValueKind() == JsonValueKind.String
                        ? value.GetValue<string>()
                        : value.ToJsonString()));
                    break;
                default:
                    throw new Xunit.Sdk.XunitException($"{path} is null after binding.");
            }
        }
    }

    /// <summary>The API settings the pilot deploys, read from the reviewed templates.</summary>
    private sealed record PilotApiSettings(
        Dictionary<string, string?> Configuration,
        Dictionary<string, string> Mappings)
    {
        public static PilotApiSettings Load()
        {
            var (configuration, mappings) = PilotAppsTemplate.Load("apiEnv");
            return new PilotApiSettings(configuration, mappings);
        }

        /// <summary>
        /// The settings with the Key Vault source off and every mapped configuration key holding a synthetic value of
        /// the shape the owner or the workflow stores (README section 4).
        /// </summary>
        public Dictionary<string, string?> Resolved() =>
            PilotAppsTemplate.Resolve(Configuration, Mappings, key => PilotAppsTemplate.SharedSecret(key) ?? key switch
            {
                "ConnectionStrings:Paqueteria" => Connection("pv_pilot_api"),
                "ConnectionStrings:PaqueteriaRealtime" => Connection("pv_pilot_realtime"),
                "AuthCenter:ClientSecret" => PilotAppsTemplate.Derived("authcenter client", hex: true),
                "EmailLookup:Keys:1" => PilotAppsTemplate.Derived("email lookup key", hex: false),
                "Locations:GoogleMaps:ApiKey" => PilotAppsTemplate.Derived("geocoding key", hex: true),
                // NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31: the webhook values. The Meta app secret is written by the
                // owner; the verify token, the Event Grid secret and the previous slots are 64 hex characters from the
                // workflow; the phone number id and the resource id have the shapes Meta and Azure give.
                "Messaging:WhatsApp:Webhook:AppSecret" => PilotAppsTemplate.Derived("meta app secret", hex: true),
                "Messaging:WhatsApp:Webhook:PreviousAppSecret" => PilotAppsTemplate.Derived("meta app secret previous", hex: true),
                "Messaging:WhatsApp:Webhook:VerifyToken" => PilotAppsTemplate.Derived("meta verify token", hex: true),
                "Messaging:WhatsApp:Webhook:PreviousVerifyToken" => PilotAppsTemplate.Derived("meta verify token previous", hex: true),
                "Messaging:Email:Events:Secret" => PilotAppsTemplate.Derived("event grid secret", hex: true),
                "Messaging:Email:Events:PreviousSecret" => PilotAppsTemplate.Derived("event grid secret previous", hex: true),
                "Messaging:WhatsApp:Webhook:PhoneNumberId" => PilotAppsTemplate.SyntheticPhoneNumberId,
                "Messaging:Email:Events:SourceResourceId" => PilotAppsTemplate.SyntheticAcsResourceId,
                _ => null,
            });
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
