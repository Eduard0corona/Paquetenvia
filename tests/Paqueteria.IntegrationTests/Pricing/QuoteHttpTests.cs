using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Infrastructure.Mock;

namespace Paqueteria.IntegrationTests.Pricing;

public sealed class QuoteHttpTests : IClassFixture<QuoteHttpWebApplicationFactory>
{
    private readonly HttpClient client;

    public QuoteHttpTests(QuoteHttpWebApplicationFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task POST_requires_authentication()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/quotes") { Content = ValidBody() };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task POST_requires_visible_active_tenant()
    {
        using var request = Authenticated(HttpMethod.Post, "/api/v1/quotes", MockIdentityProfiles.ForeignOrganizationId);
        request.Headers.Add("Idempotency-Key", "quote-http-foreign-0001");
        request.Content = ValidBody();
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_without_tenant_returns_403()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/quotes");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveViewer);
        request.Headers.Add("Idempotency-Key", "quote-http-no-tenant-01");
        request.Content = ValidBody();
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task POST_rejects_missing_or_invalid_idempotency_key(string? key)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/v1/quotes");
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        request.Content = ValidBody();
        using var response = await client.SendAsync(request);
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
    }

    [Fact]
    public async Task POST_rejects_invalid_body_and_missing_coordinates()
    {
        using var invalid = Authenticated(HttpMethod.Post, "/api/v1/quotes");
        invalid.Headers.Add("Idempotency-Key", "quote-http-invalid-body1");
        invalid.Content = JsonContent.Create(new { service_type = "INVALID", packages = Array.Empty<object>() });
        using var invalidResponse = await client.SendAsync(invalid);
        Assert.Equal((HttpStatusCode)422, invalidResponse.StatusCode);

        using var noCoordinates = Authenticated(HttpMethod.Post, "/api/v1/quotes");
        noCoordinates.Headers.Add("Idempotency-Key", "quote-http-no-coords-01");
        noCoordinates.Content = ValidBody(includeCoordinates: false);
        using var coordinateResponse = await client.SendAsync(noCoordinates);
        Assert.Equal((HttpStatusCode)422, coordinateResponse.StatusCode);
    }

    [Theory]
    [InlineData("OUTSIDE synthetic address")]
    [InlineData("EXCLUDED synthetic address")]
    [InlineData("NO_RULE synthetic address")]
    [InlineData("AMBIGUOUS synthetic address")]
    [InlineData("TAX_BLOCKED synthetic address")]
    public async Task POST_maps_pricing_and_geography_failures_to_422(string marker)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/v1/quotes");
        request.Headers.Add("Idempotency-Key", $"quote-http-failure-{marker.GetHashCode():x8}");
        request.Content = ValidBody(originAddress: marker);
        using var response = await client.SendAsync(request);
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
    }

    [Fact]
    public async Task POST_creates_and_replays_same_safe_response()
    {
        const string key = "quote-http-replay-0001";
        using var first = await SendCreateAsync(key);
        using var replay = await SendCreateAsync(key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        var replayBody = await replay.Content.ReadAsStringAsync();
        Assert.Equal(firstBody, replayBody);
        Assert.DoesNotContain("Synthetic Sender", firstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("+526671111111", firstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic origin", firstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("cipher", firstBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task POST_same_key_different_body_returns_422()
    {
        const string key = "quote-http-hash-conflict";
        using var first = await SendCreateAsync(key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var changedRequest = Authenticated(HttpMethod.Post, "/api/v1/quotes");
        changedRequest.Headers.Add("Idempotency-Key", key);
        changedRequest.Content = ValidBody(consolidated: true);
        using var changed = await client.SendAsync(changedRequest);
        Assert.Equal((HttpStatusCode)422, changed.StatusCode);
    }

    [Fact]
    public async Task Concurrent_POST_returns_one_quote_identity()
    {
        const string key = "quote-http-concurrent-001";
        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var response = await SendCreateAsync(key);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("id").GetGuid();
        });
        var ids = await Task.WhenAll(tasks);
        Assert.Single(ids.Distinct());
    }

    [Fact]
    public async Task GET_requires_authentication_and_visible_tenant()
    {
        using var anonymous = await client.GetAsync($"/api/v1/quotes/{QuoteHttpWebApplicationFactory.ActiveQuoteId:D}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var foreign = Authenticated(HttpMethod.Get, $"/api/v1/quotes/{QuoteHttpWebApplicationFactory.ActiveQuoteId:D}", MockIdentityProfiles.ForeignOrganizationId);
        using var foreignResponse = await client.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.Forbidden, foreignResponse.StatusCode);
    }

    [Theory]
    [MemberData(nameof(VisibleQuoteCases))]
    public async Task GET_returns_visible_ACTIVE_and_USED_quotes(Guid quoteId, string expectedStatus)
    {
        using var request = Authenticated(HttpMethod.Get, $"/api/v1/quotes/{quoteId:D}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedStatus, json.RootElement.GetProperty("status").GetString());
    }

    public static TheoryData<Guid, string> VisibleQuoteCases => new()
    {
        { QuoteHttpWebApplicationFactory.ActiveQuoteId, "ACTIVE" },
        { QuoteHttpWebApplicationFactory.UsedQuoteId, "USED" },
    };

    [Theory]
    [MemberData(nameof(HiddenQuoteCases))]
    public async Task GET_returns_uniform_404_for_missing_cross_tenant_expired_and_revoked(Guid quoteId)
    {
        using var request = Authenticated(HttpMethod.Get, $"/api/v1/quotes/{quoteId:D}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public static TheoryData<Guid> HiddenQuoteCases => new()
    {
        QuoteHttpWebApplicationFactory.MissingQuoteId,
        QuoteHttpWebApplicationFactory.ExpiredQuoteId,
        QuoteHttpWebApplicationFactory.RevokedQuoteId,
        Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
    };

    private async Task<HttpResponseMessage> SendCreateAsync(string key)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/v1/quotes");
        request.Headers.Add("Idempotency-Key", key);
        request.Content = ValidBody();
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string path, Guid? organizationId = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveViewer);
        request.Headers.Add("X-Organization-Id", (organizationId ?? MockIdentityProfiles.ViewerOrganizationId).ToString("D"));
        return request;
    }

    private static JsonContent ValidBody(
        bool includeCoordinates = true,
        bool consolidated = false,
        string originAddress = "Synthetic origin 100") => JsonContent.Create(new
        {
            client_account_id = (Guid?)null,
            origin = new
            {
                address_text = originAddress,
                contact_name = "Synthetic Sender",
                phone = "+526671111111",
                lat = includeCoordinates ? 24.8 : (double?)null,
                lng = includeCoordinates ? -107.4 : (double?)null,
                references = "Synthetic gate",
            },
            destination = new
            {
                address_text = "Synthetic destination 200",
                contact_name = "Synthetic Receiver",
                phone = "+526672222222",
                lat = includeCoordinates ? 24.81 : (double?)null,
                lng = includeCoordinates ? -107.41 : (double?)null,
            },
            service_type = "SAME_DAY",
            consolidated_route = consolidated,
            packages = new[]
            {
                new
                {
                    description = "Synthetic parcel",
                    weight_grams = 1000,
                    declared_value_cents = 5000L,
                    length_mm = 100,
                    width_mm = 100,
                    height_mm = 100,
                },
            },
        });
}
