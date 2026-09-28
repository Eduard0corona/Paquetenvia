using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Custody.Infrastructure;
using Dispatch.Infrastructure.Lifecycle;
using Identity.Infrastructure.Notifications;
using Notifications.Infrastructure;
using Orders.Infrastructure;
using Organizations.Infrastructure.Notifications;
using Paqueteria.Infrastructure.Database.Outbox.Retention;
using Paqueteria.Infrastructure.Cloud;
using Paqueteria.Infrastructure.DataProtection;

var builder = WebApplication.CreateBuilder(args);
// PILOT-KEYVAULT-PRIVATE-APP-READ: allowlisted Key Vault secrets, read with the managed identity
// before anything reads configuration. Off unless KeyVaultSecrets:VaultUri is set.
builder.Configuration.AddPaqueteriaKeyVaultSecrets();

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddPlatformDataProtection(builder.Configuration);
builder.Services.AddOutboxRetention(builder.Configuration);
builder.Services.AddCustodyInfrastructure(
    builder.Configuration,
    builder.Environment,
    addValidationWorker: true);
builder.Services.AddOrganizationsNotificationAudienceReader(builder.Configuration);
builder.Services.AddIdentityNotificationAudienceReader(builder.Configuration);
builder.Services.AddNotificationsInfrastructure(builder.Configuration);
builder.Services.AddOrdersClaimWindowFinalization(builder.Configuration);
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
