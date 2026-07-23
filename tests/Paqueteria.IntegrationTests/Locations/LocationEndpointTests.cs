using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Mvc.Testing;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Locations;

public sealed class LocationEndpointTests : IClassFixture<SecurityWebApplicationFactory>
{
    private readonly HttpClient client;

    public LocationEndpointTests(SecurityWebApplicationFactory factory) =>
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Theory]
    [InlineData("/api/v1/cities")]
    [InlineData("/api/v1/service-areas?city_id=11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/v1/operating-zones?service_area_id=11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/v1/locations")]
    public async Task Location_endpoints_require_authentication(string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authorized_location_query_fails_closed_when_product_provider_is_disabled()
    {
        using var request = Authenticated(HttpMethod.Get, "/api/v1/locations");
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("connection", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_requires_idempotency_and_never_echoes_sensitive_address()
    {
        const string sensitiveAddress = "Avenida Universidad 1234 interior 5";
        using var request = Authenticated(HttpMethod.Post, "/api/v1/locations");
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        request.Content = JsonContent.Create(new
        {
            city_id = Guid.NewGuid(),
            address_text = sensitiveAddress,
            address_summary = "Centro Chihuahua",
            lat = 28.63,
            lng = -106.07,
            pii_key_version = "mock-v1",
        });

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(sensitiveAddress, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void Mock_geographic_providers_are_rejected_outside_development_and_testing(string environment)
    {
        using var factory = new GeographicProviderWebApplicationFactory(environment, "Mock", "Mock");
        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("mock", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveViewer);
        return request;
    }
}
