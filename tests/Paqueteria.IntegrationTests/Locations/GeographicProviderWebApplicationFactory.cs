using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Paqueteria.IntegrationTests.Locations;

internal sealed class GeographicProviderWebApplicationFactory(
    string environment,
    string geocodingProvider,
    string piiProtector) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Locations:Provider"] = "Disabled",
                ["Locations:GeocodingProvider"] = geocodingProvider,
                ["Locations:PiiProtector"] = piiProtector,
            }));
    }
}
