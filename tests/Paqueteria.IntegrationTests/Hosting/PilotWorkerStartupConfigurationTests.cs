extern alias WorkerHost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Notifications.Infrastructure.RecipientNotices;
using Orders.Application.RecipientNotices;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Messaging;
using Paqueteria.Infrastructure.Security.Pii;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Hosting;

/// <summary>
/// NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 (AI-08 NTF-008; W11 item 12): the Worker twin of
/// <see cref="PilotApiStartupConfigurationTests"/>. The Worker composed with exactly the settings the pilot gives it
/// (<c>deploy/azure/pilot/apps.bicep</c> <c>workerEnv</c> plus <c>apps.settings.json</c>) is checked against every
/// <c>ValidateOnStart</c> rule. Each Key Vault mapping is replaced by a synthetic value under its mapped configuration
/// key, as the ADP-001 Key Vault secrets source would deliver it; nothing reaches Azure, Meta or a database (the
/// connections point to a closed local port). Every recipient-notice switch stays off and every provider Disabled.
/// </summary>
public sealed class PilotWorkerStartupConfigurationTests
{
    [Fact]
    public void Pilot_worker_maps_the_link_key_the_contact_lookup_key_and_the_whatsapp_values_and_no_webhook_secret()
    {
        var (configuration, mappings) = PilotAppsTemplate.Load("workerEnv");

        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:PaqueteriaWorker"] = "pg-worker-runtime-connection",
                ["ConnectionStrings:Paqueteria"] = "pg-worker-runtime-connection",
                ["TelemetryPseudonym:Keys:1"] = "paquetenvia-telemetry-pseudonym-key-1",
                ["PublicTracking:LinkKeys:1"] = "public-tracking-link-key",
                ["ContactLookup:Keys:1"] = "paquetenvia-contact-lookup-key-1",
                ["Messaging:WhatsApp:MetaCloudApi:AccessToken"] = "whatsapp-cloud-api-token",
                ["Messaging:WhatsApp:MetaCloudApi:PhoneNumberId"] = "whatsapp-phone-number-id",
            },
            mappings);
        Assert.Equal("https://paquetenvia.com", configuration["PublicTracking:PublicBaseUrl"]);
        Assert.Equal("1", configuration["PublicTracking:CurrentLinkKeyVersion"]);
        Assert.Equal("1", configuration["ContactLookup:CurrentKeyVersion"]);
        Assert.Equal(
            "https://kv-pvp-synthetic.vault.azure.net/keys/recipient-notice-kek",
            configuration["PiiProtection:RecipientNoticeKey:KeyId"]);
        // ADP-001: the Worker never reaches the PII key; it opens notice copies with the recipient-notice key only.
        Assert.DoesNotContain("PiiProtection:AzureKeyVault:KeyId", configuration.Keys);
        Assert.DoesNotContain(configuration.Keys, key =>
            key.StartsWith("Messaging:", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Notifications:RecipientNotices:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_pilot_worker_starts_with_its_own_settings_and_every_recipient_notice_switch_off()
    {
        await using var worker = new PilotWorkerFactory(Resolved());

        Assert.False(worker.Services.GetRequiredService<IOptions<RecipientNoticesOptions>>().Value.Enabled);
        var messaging = worker.Services.GetRequiredService<IOptions<MessagingOptions>>().Value;
        Assert.Equal(WhatsAppProviderKind.Disabled, messaging.WhatsApp.Provider);
        Assert.Equal(EmailProviderKind.Disabled, messaging.Email.Provider);
        Assert.Empty(messaging.WhatsApp.Templates);
        Assert.Empty(messaging.Email.Templates);
        // The link key ring and the contact lookup key are ready for the gate-closing change; the unwrap-only opener of
        // the recipient-notice key is not selected while no real provider sends, so nothing reaches Key Vault yet.
        Assert.Equal(
            RecipientNoticeLinkReadiness.Configured,
            worker.Services.GetRequiredService<IRecipientNoticeTrackingLinks>().Readiness);
        Assert.True(worker.Services.GetRequiredService<IRecipientContactLookup>().IsAvailable);
        Assert.All(
            worker.Services.GetServices<RecipientNoticeKeyOpenerSelection>(),
            selection => Assert.False(selection.IsSelected));
    }

    /// <summary>The Worker settings with every Key Vault mapping replaced by a synthetic value of its stored shape.</summary>
    private static Dictionary<string, string?> Resolved()
    {
        var (configuration, mappings) = PilotAppsTemplate.Load("workerEnv");
        return PilotAppsTemplate.Resolve(configuration, mappings, key => PilotAppsTemplate.SharedSecret(key) ?? key switch
        {
            "ConnectionStrings:PaqueteriaWorker" or "ConnectionStrings:Paqueteria" => PilotAppsTemplate.Connection("pv_pilot_worker"),
            // NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31: the owner writes both Meta values (W7).
            "Messaging:WhatsApp:MetaCloudApi:AccessToken" => PilotAppsTemplate.Derived("meta system user token", hex: true),
            "Messaging:WhatsApp:MetaCloudApi:PhoneNumberId" => PilotAppsTemplate.SyntheticPhoneNumberId,
            _ => null,
        });
    }

    /// <summary>The Worker host in the pilot's environment (Production), composed and started with the given settings.</summary>
    private sealed class PilotWorkerFactory(Dictionary<string, string?> settings)
        : StartupFailureSurfacingWebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(settings["ASPNETCORE_ENVIRONMENT"] ?? throw new InvalidOperationException("no environment"));
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
        }
    }
}
