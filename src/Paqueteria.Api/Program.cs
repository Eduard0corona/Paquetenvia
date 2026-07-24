using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Identity.Endpoints;
using Identity.Endpoints.Testing;
using Identity.Infrastructure;
using Drivers.Infrastructure;
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
using Paqueteria.Api.Tenancy;
using Locations.Endpoints;
using Locations.Infrastructure;
using Pricing.Endpoints;
using Pricing.Infrastructure;
using Realtime.Endpoints;
using Realtime.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddIdentityInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddDriversInfrastructure(builder.Configuration);
builder.Services.AddDispatchInfrastructure(builder.Configuration);
builder.Services.AddDispatchEndpoints();
builder.Services.AddOrdersInfrastructure(builder.Configuration);
builder.Services.AddOrdersEndpoints();
builder.Services.AddOrganizationsInfrastructure(builder.Configuration);
builder.Services.AddOrganizationsEndpoints();
builder.Services.AddLocationsInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddLocationsEndpoints();
builder.Services.AddPricingInfrastructure(builder.Configuration);
builder.Services.AddPricingEndpoints();
builder.Services.AddRealtimeInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddRealtimeEndpoints(builder.Configuration);
builder.Services.AddScoped<IOrganizationRequestSession, OrganizationRequestSessionAdapter>();
builder.Services.AddIdentitySecurity(builder.Configuration, builder.Environment);
builder.Services
    .AddHealthChecks()
    .AddCheck("process", () => HealthCheckResult.Healthy(), tags: ["live"]);

var app = builder.Build();

app.UseExceptionHandler();
app.UseRouting();
app.UseCors();

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

app.MapIdentityTestProbes(app.Environment);
app.MapPublicTrackingTestProbe(app.Environment);
app.MapOrganizationEndpoints();
app.MapOrganizationTestProbes(app.Environment);
app.MapLocationEndpoints();
app.MapQuoteEndpoints();
app.MapOrderEndpoints();
app.MapDispatchEndpoints();
app.MapRealtimeHubs();

app.Run();

public partial class Program;
