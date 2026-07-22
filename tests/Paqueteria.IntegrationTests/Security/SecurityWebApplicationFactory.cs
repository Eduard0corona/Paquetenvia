using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Paqueteria.IntegrationTests.Security;

public sealed class SecurityWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "Mock",
            }));
    }
}

internal sealed class EnvironmentWebApplicationFactory(
    string environment,
    string provider) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = provider,
                ["IdentityBootstrap:Provider"] = provider == "Mock" ? "Mock" : "Disabled",
            }));
    }
}
