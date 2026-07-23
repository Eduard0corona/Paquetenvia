using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Infrastructure.Mock;

namespace Paqueteria.IntegrationTests.Locations;

public sealed class LocationHttpSuccessTests : IClassFixture<LocationHttpWebApplicationFactory>
{
    private readonly HttpClient client;

    public LocationHttpSuccessTests(LocationHttpWebApplicationFactory factory) => client = factory.CreateClient();

    [Theory]
    [InlineData("/api/v1/cities")]
    [InlineData("/api/v1/service-areas?city_id=10000000-0000-0000-0000-000000000001")]
    [InlineData("/api/v1/operating-zones?service_area_id=20000000-0000-0000-0000-000000000001")]
    [InlineData("/api/v1/locations")]
    public async Task Normative_GET_operations_return_only_transport_contracts(string path)
    {
        using var request = Authenticated(HttpMethod.Get, path);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task POST_location_returns_201_without_plaintext_or_protected_fields()
    {
        const string address = "Synthetic private address never echoed";
        using var request = Authenticated(HttpMethod.Post, "/api/v1/locations");
        request.Headers.Add("Idempotency-Key", "geo001-http-success");
        request.Content = JsonContent.Create(new
        {
            city_id = LocationHttpWebApplicationFactory.CityId,
            service_area_id = LocationHttpWebApplicationFactory.ServiceAreaId,
            operating_zone_id = LocationHttpWebApplicationFactory.ZoneId,
            address_text = address,
            address_summary = "Synthetic summary",
            contact_name = "Synthetic contact",
            phone = "+526141234567",
            lat = 28.61,
            lng = -106.09,
            pii_key_version = "mock-v1",
        });

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain(address, body, StringComparison.Ordinal);
        Assert.DoesNotContain("contact", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ciphertext", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pii_key_version", body, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string?> InvalidIdempotencyKeys => new()
    {
        null,
        string.Empty,
        "   ",
        new string('a', 15),
        new string('a', 129),
        new string('a', 200),
        $" {new string('a', 15)}",
        $"{new string('a', 15)} ",
    };

    [Theory]
    [MemberData(nameof(InvalidIdempotencyKeys))]
    public async Task POST_location_rejects_invalid_idempotency_key_before_calling_the_service(string? idempotencyKey)
    {
        using var response = await SendCreateLocationAsync(idempotencyKey is null ? null : [idempotencyKey]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POST_location_rejects_multiple_idempotency_key_values()
    {
        using var response = await SendCreateLocationAsync(
            [new string('a', 16), new string('b', 16)]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(128)]
    public async Task POST_location_accepts_valid_idempotency_key_boundaries(int length)
    {
        using var response = await SendCreateLocationAsync([new string('a', length)]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Missing_and_cross_tenant_geographic_resources_share_uniform_404()
    {
        using var get = Authenticated(
            HttpMethod.Get,
            $"/api/v1/operating-zones?service_area_id={LocationHttpWebApplicationFactory.ForeignResourceId:D}");
        using var getResponse = await client.SendAsync(get);

        using var post = Authenticated(HttpMethod.Post, "/api/v1/locations");
        post.Headers.Add("Idempotency-Key", "geo001-http-hidden");
        post.Content = JsonContent.Create(new
        {
            city_id = LocationHttpWebApplicationFactory.CityId,
            service_area_id = LocationHttpWebApplicationFactory.ForeignResourceId,
            address_text = "Synthetic private address",
            address_summary = "Synthetic summary",
            lat = 28.61,
            lng = -106.09,
            pii_key_version = "mock-v1",
        });
        using var postResponse = await client.SendAsync(post);

        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, postResponse.StatusCode);
        using var getProblem = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        using var postProblem = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
        Assert.Equal(getProblem.RootElement.GetProperty("title").GetString(), postProblem.RootElement.GetProperty("title").GetString());
        Assert.Equal(getProblem.RootElement.GetProperty("status").GetInt32(), postProblem.RootElement.GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("/api/v1/service-areas")]
    [InlineData("/api/v1/operating-zones")]
    public async Task Required_query_identifiers_are_enforced(string path)
    {
        using var request = Authenticated(HttpMethod.Get, path);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveViewer);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        return request;
    }

    private async Task<HttpResponseMessage> SendCreateLocationAsync(string[]? idempotencyKeys)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/v1/locations");
        if (idempotencyKeys is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKeys));
        }

        request.Content = JsonContent.Create(new
        {
            city_id = LocationHttpWebApplicationFactory.CityId,
            service_area_id = LocationHttpWebApplicationFactory.ServiceAreaId,
            address_text = "Synthetic private address",
            address_summary = "Synthetic summary",
            lat = 28.61,
            lng = -106.09,
            pii_key_version = "mock-v1",
        });
        return await client.SendAsync(request);
    }
}
