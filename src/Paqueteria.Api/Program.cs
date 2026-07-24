using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Identity.Endpoints;
using Identity.Endpoints.Testing;
using Identity.Infrastructure;
using Drivers.Infrastructure;
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

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddIdentityInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddDriversInfrastructure(builder.Configuration);
builder.Services.AddOrdersInfrastructure(builder.Configuration);
builder.Services.AddOrdersEndpoints();
builder.Services.AddOrganizationsInfrastructure(builder.Configuration);
builder.Services.AddOrganizationsEndpoints();
builder.Services.AddLocationsInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddLocationsEndpoints();
builder.Services.AddPricingInfrastructure(builder.Configuration);
builder.Services.AddPricingEndpoints();
builder.Services.AddScoped<IOrganizationRequestSession, OrganizationRequestSessionAdapter>();
builder.Services.AddIdentitySecurity(builder.Configuration, builder.Environment);
builder.Services
    .AddHealthChecks()
    .AddCheck("process", () => HealthCheckResult.Healthy(), tags: ["live"]);

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (app.Configuration.GetValue<bool>("Http:UseHttpsRedirection"))
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
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

app.Run();

public partial class Program;
