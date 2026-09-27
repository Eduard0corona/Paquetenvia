using Identity.Endpoints.AuthCenter;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Paqueteria.IntegrationTests.Security.AuthCenter;

/// <summary>
/// AUTH-OPEN-REGISTRATION end to end: the AuthCenter BFF (fake IdP in process) over the real
/// PostgreSQL bootstrap, identity registration and REG-001 functions, instead of the mock resolver.
/// </summary>
public sealed class AuthCenterPostgreSqlWebApplicationFactory : PostgreSqlSecurityWebApplicationFactory
{
    internal FakeAuthCenterServer AuthCenter { get; } = new();

    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri(FakeAuthCenterServer.PublicOrigin),
    });

    public async Task<T> AdminScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async Task AdminExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "AuthCenter",
                ["AuthCenter:Authority"] = FakeAuthCenterServer.Authority,
                ["AuthCenter:Issuer"] = FakeAuthCenterServer.Issuer,
                ["AuthCenter:ClientId"] = FakeAuthCenterServer.ClientId,
                ["AuthCenter:ClientSecret"] = FakeAuthCenterServer.ClientSecret,
                ["AuthCenter:PublicOrigin"] = FakeAuthCenterServer.PublicOrigin,
            }));
        builder.ConfigureTestServices(services => services.Configure<OpenIdConnectOptions>(
            AuthCenterDefaults.OpenIdConnectScheme,
            options => options.BackchannelHttpHandler = AuthCenter));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            AuthCenter.Dispose();
        }
    }
}
