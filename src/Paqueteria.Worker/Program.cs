using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Custody.Infrastructure;
using Dispatch.Infrastructure.Lifecycle;
using Identity.Infrastructure.Notifications;
using Locations.Infrastructure.RecipientNotices;
using Notifications.Infrastructure;
using Orders.Infrastructure;
using Orders.Infrastructure.RecipientNotices;
using Organizations.Infrastructure.Notifications;
using Paqueteria.Infrastructure.Database.Outbox.Retention;
using Paqueteria.Infrastructure.Cloud;
using Paqueteria.Infrastructure.DataProtection;
using Paqueteria.Infrastructure.Messaging;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.Hosting.Observability;

// OBS-003: before anything builds an HTTP handler, so no inbound or outbound trace header is ever honored or sent.
TraceCorrelation.ConfigureProcess();
var builder = WebApplication.CreateBuilder(args);
// PILOT-KEYVAULT-PRIVATE-APP-READ: allowlisted Key Vault secrets, read with the managed identity
// before anything reads configuration. Off unless KeyVaultSecrets:VaultUri is set.
builder.Configuration.AddPaqueteriaKeyVaultSecrets();

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
// OBS-003: every job cycle and outbox dispatch cycle is a new W3C trace whose id is in its log lines.
builder.Services.AddTraceCorrelation();
// OBS-003 PR 6: the organization pseudonym key is validated at start (required outside Development and Testing), and
// traces and metrics go to Application Insights only when TelemetryExport:Enabled turns the export on.
builder.Services.AddTelemetryOrganizationPseudonym(builder.Configuration, builder.Environment);
builder.AddAzureMonitorTelemetryExport(TelemetryExportProfile.Worker);
builder.Services.AddPlatformDataProtection(builder.Configuration);
builder.Services.AddOutboxRetention(builder.Configuration);
builder.Services.AddCustodyInfrastructure(
    builder.Configuration,
    builder.Environment,
    addValidationWorker: true);
builder.Services.AddOrganizationsNotificationAudienceReader(builder.Configuration);
builder.Services.AddIdentityNotificationAudienceReader(builder.Configuration);
builder.Services.AddNotificationsInfrastructure(builder.Configuration);
// GATE-004-CHANNELS: WhatsApp (Meta Cloud API) and email (ACS) adapters; every channel Disabled by default.
builder.Services.AddPaqueteriaMessaging(builder.Configuration, builder.Environment);
// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-005): the readers recipient notices use under FORCE RLS - the order, its
// active tracking link (derived in memory with the Worker's link key ring) and the notice copies of its destination.
// Recipient notices stay off unless Notifications:RecipientNotices:Enabled.
builder.Services.AddOrdersRecipientNoticeReaders(builder.Configuration, builder.Environment);
builder.Services.AddLocationsRecipientNoticeContactReader(builder.Configuration);
builder.Services.AddOrdersClaimWindowFinalization(builder.Configuration);
// ORD-AUTO-CLOSE-2026-10-10: closes DELIVERED orders whose CLOSED guards hold; off unless Orders:AutoClose:Enabled.
builder.Services.AddOrdersAutoClose(builder.Configuration);
builder.Services.AddDispatchAssignmentLifecycleWorker(builder.Configuration);
builder.Services.AddCustodyOperationalCleanup(builder.Configuration);
builder.Services
    .AddHealthChecks()
    .AddCheck("process", () => HealthCheckResult.Healthy(), tags: ["live"]);

var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live"),
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
});
app.Run();

public partial class WorkerProgram;
