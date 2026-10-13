using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Paqueteria.IntegrationTests.Observability;

namespace Paqueteria.IntegrationTests.Locations;

internal sealed class GeographicProviderWebApplicationFactory(
    string environment,
    string geocodingProvider,
    string piiProtector) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        // OBS-003 PR 6: outside Development and Testing the host needs the organization pseudonym key.
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(TelemetryPseudonymTestKey.Settings));
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Locations:Provider"] = "Disabled",
                ["Locations:GeocodingProvider"] = geocodingProvider,
                ["Locations:PiiProtector"] = piiProtector,
            }));
    }
}
