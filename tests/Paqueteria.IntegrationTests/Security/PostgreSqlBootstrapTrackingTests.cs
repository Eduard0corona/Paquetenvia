using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Application.Bootstrap;
using Identity.Infrastructure.Bootstrap;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Application.Tracking;
using Orders.Infrastructure.Tracking;

namespace Paqueteria.IntegrationTests.Security;

public sealed class PostgreSqlBootstrapTrackingTests(
    PostgreSqlSecurityWebApplicationFactory factory)
    : IClassFixture<PostgreSqlSecurityWebApplicationFactory>
{
    private const string IdentityProbe = "/__tests/security/authenticated";
    private readonly HttpClient _client = factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    [Fact]
    public void Fixture_executes_required_PostgreSql_and_PostGIS_versions()
    {
        Assert.StartsWith("18.", factory.PostgreSqlVersion, StringComparison.Ordinal);
        Assert.StartsWith("3.6", factory.PostGisVersion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("not-a-profile", HttpStatusCode.Unauthorized)]
    [InlineData(MockIdentityProfiles.ActiveViewer, HttpStatusCode.NoContent)]
    [InlineData(MockIdentityProfiles.UnknownSubject, HttpStatusCode.Forbidden)]
    [InlineData(MockIdentityProfiles.SuspendedUser, HttpStatusCode.Forbidden)]
    [InlineData(MockIdentityProfiles.DisabledUser, HttpStatusCode.Forbidden)]
    public async Task Identity_HTTP_matrix_separates_external_authentication_from_internal_context(
        string? credential,
        HttpStatusCode expected)
    {
        using var response = credential is null
            ? await _client.GetAsync(IdentityProbe)
            : await SendBearerAsync(IdentityProbe, credential);

        Assert.Equal(expected, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        if (expected is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemShape>();
            Assert.Equal((int)expected, problem?.Status);
            var raw = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SUSPENDED", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DISABLED", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mock-subject", raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Active_user_without_memberships_is_authenticated_but_organization_policy_forbids()
    {
        using var active = await SendBearerAsync(IdentityProbe, MockIdentityProfiles.ActiveWithoutMemberships);
        using var tenant = await SendBearerAsync(
            $"/__tests/security/organization/{PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId:D}",
            MockIdentityProfiles.ActiveWithoutMemberships);

        Assert.Equal(HttpStatusCode.NoContent, active.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, tenant.StatusCode);
    }

    [Fact]
    public async Task Multi_org_roles_are_separate_and_external_role_headers_do_not_elevate()
    {
        using var viewer = await SendBearerAsync(
            $"/__tests/security/organization/{PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId:D}",
            MockIdentityProfiles.ActiveMultiOrganization);
        using var operations = await SendBearerAsync(
            $"/__tests/security/organization/{PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId:D}",
            MockIdentityProfiles.ActiveMultiOrganization);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/__tests/security/privileged");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveViewer);
        request.Headers.TryAddWithoutValidation("X-User-Role", "PLATFORM_ADMIN");
        request.Headers.TryAddWithoutValidation("X-MFA", "true");
        using var elevated = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, viewer.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, operations.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, elevated.StatusCode);
    }

    [Fact]
    public async Task PostgreSql_bootstrap_also_protects_SignalR_negotiate()
    {
        using var active = new HttpRequestMessage(
            HttpMethod.Post,
            "/__tests/hubs/security/negotiate?negotiateVersion=1");
        active.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveViewer);
        using var activeResponse = await _client.SendAsync(active);

        using var unresolved = new HttpRequestMessage(
            HttpMethod.Post,
            "/__tests/hubs/security/negotiate?negotiateVersion=1");
        unresolved.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.UnknownSubject);
        using var unresolvedResponse = await _client.SendAsync(unresolved);

        Assert.Equal(HttpStatusCode.OK, activeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unresolvedResponse.StatusCode);
    }

    [Fact]
    public async Task Valid_tracking_is_minimal_public_and_not_cached()
    {
        using var response = await _client.GetAsync(
            $"/__tests/tracking/{PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(["estimated_window", "public_id", "public_status", "timeline"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("SEC002-PUBLIC-001", root.GetProperty("public_id").GetString());
        Assert.Equal("OUT_FOR_DELIVERY", root.GetProperty("public_status").GetString());
        Assert.Equal(2, root.GetProperty("timeline").GetArrayLength());
        var raw = root.GetRawText();
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("66666666-6666", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("11111111-1111", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-tracking-token-sec002-000000")]
    [InlineData(PostgreSqlSecurityWebApplicationFactory.ExpiredTrackingToken)]
    [InlineData(PostgreSqlSecurityWebApplicationFactory.RevokedTrackingToken)]
    [InlineData("BQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=")]
    [InlineData("%20AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA%20")]
    public async Task Invalid_tracking_variants_return_uniform_404(string token)
    {
        using var response = await _client.GetAsync($"/__tests/tracking/{token}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var problem = await response.Content.ReadFromJsonAsync<ProblemShape>();
        Assert.Equal("https://httpstatuses.com/404", problem?.Type);
        Assert.Equal("Not Found", problem?.Title);
        Assert.Equal(404, problem?.Status);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("expired", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("revoked", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organization", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("order", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Set_local_role_does_not_leak_across_pooled_connections()
    {
        await using var dataSource = NpgsqlDataSource.Create(factory.ApplicationConnectionString);
        var resolver = new PostgreSqlIdentityContextResolver(
            dataSource,
            Options.Create(new IdentityBootstrapOptions { Provider = IdentityBootstrapProviderKind.PostgreSql }),
            NullLogger<PostgreSqlIdentityContextResolver>.Instance);
        var tracking = new PostgreSqlPublicTrackingProjectionReader(
            dataSource,
            Options.Create(new PublicTrackingOptions { Provider = PublicTrackingProviderKind.PostgreSql }),
            NullLogger<PostgreSqlPublicTrackingProjectionReader>.Instance);

        Assert.True((await resolver.ResolveAsync("mock-subject-active-viewer", default)).IsResolved);
        Assert.True((await tracking.FindAsync(PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken, default)).IsFound);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT current_user, current_role", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("paqueteria_sec002_api", reader.GetString(0));
        Assert.Equal("paqueteria_sec002_api", reader.GetString(1));
    }

    [Fact]
    public async Task Database_unavailability_maps_to_generic_503_not_401_or_404()
    {
        using var unavailable = new UnavailableDatabaseWebApplicationFactory();
        using var client = unavailable.CreateClient();
        using var identity = new HttpRequestMessage(HttpMethod.Get, IdentityProbe);
        identity.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveViewer);
        using var identityResponse = await client.SendAsync(identity);
        using var trackingResponse = await client.GetAsync(
            $"/__tests/tracking/{PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, identityResponse.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, trackingResponse.StatusCode);
        Assert.Equal("Service Unavailable", (await identityResponse.Content.ReadFromJsonAsync<ProblemShape>())?.Title);
        Assert.Equal("Service Unavailable", (await trackingResponse.Content.ReadFromJsonAsync<ProblemShape>())?.Title);
    }

    private async Task<HttpResponseMessage> SendBearerAsync(string path, string credential)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await _client.SendAsync(request);
    }

    private sealed record ProblemShape(string? Type, string Title, int Status, string? TraceId);
}

internal sealed class UnavailableDatabaseWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "PostgreSql",
                ["IdentityBootstrap:CommandTimeoutSeconds"] = "1",
                ["PublicTracking:Provider"] = "PostgreSql",
                ["PublicTracking:CommandTimeoutSeconds"] = "1",
                ["ConnectionStrings:Paqueteria"] = "Host=127.0.0.1;Port=1;Database=unavailable;Username=none;Password=none;Timeout=1;Command Timeout=1;Pooling=false",
            }));
    }
}
