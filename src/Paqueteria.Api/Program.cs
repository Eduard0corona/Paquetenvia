using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Identity.Endpoints;
using Identity.Endpoints.AuthCenter;
using Identity.Endpoints.Testing;
using Identity.Infrastructure;
using Drivers.Infrastructure;
using Drivers.Endpoints;
using Dispatch.Endpoints;
using Dispatch.Infrastructure;
using Orders.Infrastructure;
using Orders.Endpoints.Testing;
using Orders.Endpoints;
using Organizations.Application.Session;
using Organizations.Endpoints;
using Organizations.Endpoints.Tenancy;
using Organizations.Infrastructure;
using Organizations.Endpoints.Testing;
using Paqueteria.Api.Http;
using Paqueteria.Api.Tenancy;
using Paqueteria.Infrastructure.Cloud;
using Paqueteria.Infrastructure.DataProtection;
using Paqueteria.Infrastructure.Security;
using Locations.Endpoints;
using Locations.Infrastructure;
using Pricing.Endpoints;
using Pricing.Infrastructure;
using Realtime.Endpoints;
using Realtime.Infrastructure;
using Custody.Endpoints;
using Finance.Endpoints;
using Finance.Infrastructure;
using Custody.Infrastructure;
using Incidents.Endpoints;
using Incidents.Infrastructure;
using Reporting.Endpoints;
using Reporting.Infrastructure;
using Routing.Endpoints;
using Routing.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
// PILOT-KEYVAULT-PRIVATE-APP-READ: allowlisted Key Vault secrets, read with the managed identity
// before anything reads configuration. Off unless KeyVaultSecrets:VaultUri is set.
builder.Configuration.AddPaqueteriaKeyVaultSecrets();

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.AddHttpHardening();
builder.Services.AddPlatformDataProtection(builder.Configuration);
builder.Services.AddEmailLookupHashing(builder.Configuration, builder.Environment);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddIdentityInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddDriversInfrastructure(builder.Configuration);
builder.Services.AddDriversEndpoints(builder.Configuration);
builder.Services.AddDispatchInfrastructure(builder.Configuration);
builder.Services.AddDispatchEndpoints();
builder.Services.AddOrdersInfrastructure(builder.Configuration);
builder.Services.AddOrdersEndpoints(builder.Configuration);
builder.Services.AddOrganizationsInfrastructure(builder.Configuration);
builder.Services.AddOrganizationsEndpoints();
builder.Services.AddLocationsInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddLocationsEndpoints();
builder.Services.AddPricingInfrastructure(builder.Configuration);
builder.Services.AddPricingEndpoints();
builder.Services.AddRealtimeInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddRealtimeOutboxDispatchers(builder.Configuration);
builder.Services.AddRealtimeEndpoints(builder.Configuration);
builder.Services.AddCustodyInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddIncidentsInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddReportingInfrastructure(builder.Configuration);
builder.Services.AddReportingEndpoints();
builder.Services.AddFinanceInfrastructure(builder.Configuration);
builder.Services.AddFinanceEndpoints();
builder.Services.AddRoutingInfrastructure(builder.Configuration);
builder.Services.AddRoutingEndpoints();
builder.Services.AddScoped<IOrganizationRequestSession, OrganizationRequestSessionAdapter>();
builder.Services.AddIdentitySecurity(builder.Configuration, builder.Environment);
builder.Services
    .AddHealthChecks()
    .AddCheck("process", () => HealthCheckResult.Healthy(), tags: ["live"]);

var app = builder.Build();

app.UseTrustedForwardedHeaders();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseRouting();
app.UsePublicTrackingResponseHeaders();
app.UseCors();
app.UseRequestBodyLimit();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (app.Configuration.GetValue<bool>("Http:UseHttpsRedirection"))
{
    app.UseHttpsRedirection();
}

app.UseRealtimePrivateAccessTokens();
app.UseAuthentication();
app.UseRateLimiter();
app.UseRealtimeConnectionGate();
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live"),
    ResponseWriter = static async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(
            new { status = report.Status == HealthStatus.Healthy ? "healthy" : "unhealthy" },
            context.RequestAborted);
    },
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = static async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var status = report.Status switch
        {
            HealthStatus.Healthy => "healthy",
            HealthStatus.Degraded => "degraded",
            _ => "unhealthy",
        };
        await context.Response.WriteAsJsonAsync(
            new { status },
            context.RequestAborted);
    },
}).AllowAnonymous();

app.MapAuthCenterBff();
app.MapIdentityTestProbes(app.Environment);
app.MapPublicTrackingTestProbe(app.Environment);
app.MapOrganizationEndpoints();
app.MapOrganizationTestProbes(app.Environment);
app.MapLocationEndpoints();
app.MapQuoteEndpoints();
app.MapOrderEndpoints();
app.MapCsvOrderImportEndpoints();
app.MapPublicTrackingEndpoints();
app.MapDispatchEndpoints();
app.MapDriverLocationEndpoints();
app.MapRealtimeHubs();
app.MapProofEndpoints();
app.MapIncidentEndpoints();
app.MapOperationsDashboardEndpoints();
app.MapRouteEndpoints();
app.MapCodEndpoints();
app.MapFinancialsEndpoints();
app.MapSettlementEndpoints();

app.Run();

public partial class Program;
