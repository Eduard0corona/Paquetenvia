using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Paqueteria.IntegrationTests;

public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    [Fact]
    public async Task Live_health_returns_healthy_without_external_services()
    {
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await _client.GetAsync("/health/live", cancellationSource.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(
            cancellationToken: cancellationSource.Token);
        Assert.Equal("healthy", body?.Status);
    }

    private sealed record HealthResponse(string Status);
}
