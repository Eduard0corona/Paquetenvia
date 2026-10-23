extern alias WorkerHost;

using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Notifications.Application.RecipientNotices;
using Notifications.Infrastructure.Dispatching;
using Notifications.Infrastructure.RecipientNotices;
using Orders.Application.RecipientNotices;
using Paqueteria.Application.Messaging;
using Paqueteria.Application.RecipientNotices;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Security.Pii;
using Paqueteria.IntegrationTests.Hosting;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Notifications;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 and NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 (AI-08 NTF-005; AI-24
/// <c>recipient_notices.switches</c>): the real Worker composition of recipient notices. With synthetic providers it starts
/// enabled and resolves the PostgreSQL readers of Orders and Locations and the synthetic link key, without building the
/// Key Vault opener; a real provider is refused on start in Testing even with both gate decision ids. Every contact here
/// is a synthetic placeholder and no key or token is a literal of this file. Nothing opens a database connection.
/// </summary>
public sealed class RecipientNoticeWorkerCompositionTests
{
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=ntf005composition;Username=unused;Password=unused;Timeout=3;Pooling=false";

    [Fact]
    public async Task Worker_starts_enabled_with_synthetic_providers_and_composes_the_postgresql_readers()
    {
        await using var worker = new WorkerFactory(Synthetic());

        Assert.True(worker.Services.GetRequiredService<IOptions<RecipientNoticesOptions>>().Value.Enabled);
        Assert.Equal("RecipientNoticeProcessor", worker.Services.GetRequiredService<IRecipientNoticeLane>().GetType().Name);
        Assert.Equal("PostgreSqlRecipientNoticeOrderReader", worker.Services.GetRequiredService<IRecipientNoticeOrderReader>().GetType().Name);
        Assert.Equal("PostgreSqlRecipientNoticeContactReader", worker.Services.GetRequiredService<IRecipientNoticeContactReader>().GetType().Name);
        // AI-08 NTF-004 (NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23 N3 choice 7): the consent is read from orders.recipient_notice_consents.
        Assert.Equal("PostgreSqlRecipientNoticeConsentReader", worker.Services.GetRequiredService<IRecipientNoticeConsentReader>().GetType().Name);
        Assert.Equal("MessagingProviderRouter", worker.Services.GetRequiredService<IMessagingProvider>().GetType().Name);
        var links = worker.Services.GetRequiredService<IRecipientNoticeTrackingLinks>();
        Assert.Equal("RecipientNoticeTrackingLinks", links.GetType().Name);
        Assert.Equal(RecipientNoticeLinkReadiness.Synthetic, links.Readiness);
        Assert.True(worker.Services.GetRequiredService<IRecipientContactLookup>().IsAvailable);
        Assert.NotNull(worker.Services.GetRequiredService<NotificationsOutboxProcessor>());

        // Synthetic providers never decrypt: the unwrap-only Key Vault opener is not selected.
        Assert.All(
            worker.Services.GetServices<RecipientNoticeKeyOpenerSelection>(),
            selection => Assert.False(selection.IsSelected));
    }

    [Fact]
    public async Task Worker_starts_with_recipient_notices_off_by_default()
    {
        await using var worker = new WorkerFactory(new Dictionary<string, string?>());

        var options = worker.Services.GetRequiredService<IOptions<RecipientNoticesOptions>>().Value;
        Assert.False(options.Enabled);
        Assert.Equal(("08:00", "21:00", 12, 8, 500), (
            options.QuietHours.Start,
            options.QuietHours.End,
            options.MaximumNoticeDelayHours,
            options.MaximumPerOrderAndChannel,
            options.DailyLimitPerChannel));
    }

    [Theory]
    [InlineData("WhatsApp")]
    [InlineData("Email")]
    public void Worker_refuses_a_real_provider_in_testing_even_with_both_gate_decision_ids(string channel)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Notifications:RecipientNotices:Gate004DecisionId"] = "GATE-004-SYNTHETIC-COMPOSITION",
            ["Notifications:RecipientNotices:Gate007DecisionId"] = "GATE-007-SYNTHETIC-COMPOSITION",
        };
        if (channel == "WhatsApp")
        {
            settings["Messaging:WhatsApp:Provider"] = "MetaCloudApi";
            settings["Messaging:WhatsApp:MetaCloudApi:PhoneNumberId"] = "100000000000001";
            settings["Messaging:WhatsApp:MetaCloudApi:AccessToken"] = $"synthetic-{Guid.NewGuid():N}";
        }
        else
        {
            settings["Messaging:Email:Provider"] = "AzureCommunicationServices";
            settings["Messaging:Email:AzureCommunicationServices:Endpoint"] = "https://ntf005-synthetic.communication.azure.com";
            settings["Messaging:Email:AzureCommunicationServices:SenderAddress"] = RecipientNoticeTexts.EmailSender;
        }

        var exception = Assert.ThrowsAny<Exception>(() =>
        {
            using var worker = new WorkerFactory(settings);
            _ = worker.Services;
        });

        Assert.Contains(
            "A real messaging provider for recipient notices is refused in Development, Testing and DEV_SYNTHETIC.",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>Notices on, synthetic providers, the AI-24 templates, a runtime contact lookup key, a synthetic origin.</summary>
    private static Dictionary<string, string?> Synthetic()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Notifications:RecipientNotices:Enabled"] = "true",
            ["Messaging:WhatsApp:Provider"] = "Synthetic",
            ["Messaging:Email:Provider"] = "Synthetic",
            ["ContactLookup:CurrentKeyVersion"] = "1",
            ["ContactLookup:Keys:1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            // https: the e-mail one-click unsubscribe URL is https (RFC 8058) even in Testing.
            ["PublicTracking:PublicBaseUrl"] = "https://paquetenvia.test",
        };
        foreach (var kind in RecipientNoticeCatalog.All)
        {
            var key = RecipientNoticeCatalog.TemplateKey(kind);
            settings[$"Messaging:WhatsApp:Templates:{key}:Name"] =
                "ntf005_" + key.Replace("recipient.", string.Empty, StringComparison.Ordinal).Replace('-', '_');
            settings[$"Messaging:WhatsApp:Templates:{key}:LanguageCode"] = RecipientNoticeTexts.WhatsAppLanguage;
            settings[$"Messaging:WhatsApp:Templates:{key}:ParameterCount"] = RecipientNoticeTexts.WhatsAppParameterCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings[$"Messaging:Email:Templates:{key}:Subject"] = RecipientNoticeTexts.EmailSubject(kind);
            settings[$"Messaging:Email:Templates:{key}:PlainText"] = RecipientNoticeTexts.EmailPlainText(kind);
            settings[$"Messaging:Email:Templates:{key}:ParameterCount"] = RecipientNoticeTexts.EmailParameterCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return settings;
    }

    private sealed class WorkerFactory(IReadOnlyDictionary<string, string?> settings)
        : StartupFailureSurfacingWebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>(settings)
                {
                    ["ConnectionStrings:PaqueteriaWorker"] = UnreachableDatabase,
                }));
        }
    }
}
