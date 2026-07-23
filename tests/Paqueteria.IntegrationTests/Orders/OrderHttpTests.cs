using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Orders.Application.Orders;

namespace Paqueteria.IntegrationTests.Orders;

public sealed class OrderHttpTests : IClassFixture<OrderHttpWebApplicationFactory>
{
    private readonly HttpClient client;

    public OrderHttpTests(OrderHttpWebApplicationFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task POST_requires_authentication()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = ValidBody(Guid.NewGuid()) };
        request.Headers.Add("Idempotency-Key", Key());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task POST_requires_visible_active_tenant(bool includeTenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveViewer);
        if (includeTenant)
        {
            request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ForeignOrganizationId.ToString("D"));
        }
        request.Headers.Add("Idempotency-Key", Key());
        request.Content = ValidBody(Guid.NewGuid());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task POST_rejects_missing_or_invalid_key_with_uniform_409(string? key)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/v1/orders");
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        request.Content = ValidBody(Guid.NewGuid());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("INVALID", "WEB")]
    [InlineData("SENDER", "MOBILE")]
    public async Task POST_rejects_invalid_contract_values_with_409(string payerType, string channel)
    {
        using var request = Authenticated(HttpMethod.Post, "/api/v1/orders");
        request.Headers.Add("Idempotency-Key", Key());
        request.Content = ValidBody(Guid.NewGuid(), payerType, channel);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(UnavailableQuotes))]
    public async Task POST_hides_all_unavailable_quote_conditions_behind_409(Guid quoteId)
    {
        using var response = await CreateAsync(quoteId, Key());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(quoteId.ToString("D"), await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<Guid> UnavailableQuotes => new()
    {
        OrderHttpWebApplicationFactory.MissingQuoteId,
        OrderHttpWebApplicationFactory.ForeignQuoteId,
        OrderHttpWebApplicationFactory.ExpiredQuoteId,
        OrderHttpWebApplicationFactory.UsedQuoteId,
    };

    [Fact]
    public async Task POST_creates_and_replays_identical_safe_201_response()
    {
        var quoteId = Guid.NewGuid();
        var key = Key();
        using var first = await CreateAsync(quoteId, key);
        using var replay = await CreateAsync(quoteId, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var body = await first.Content.ReadAsStringAsync();
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        Assert.DoesNotContain("terms-synthetic", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privacy-synthetic", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acceptance", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task POST_same_key_with_changed_body_returns_409()
    {
        var key = Key();
        using var first = await CreateAsync(Guid.NewGuid(), key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var changed = await CreateAsync(Guid.NewGuid(), key);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
    }

    [Fact]
    public async Task POST_distinct_keys_for_same_quote_produce_one_order()
    {
        var quote = Guid.NewGuid();
        using var first = await CreateAsync(quote, Key());
        using var second = await CreateAsync(quote, Key());
        Assert.Equal(
            new[] { HttpStatusCode.Created, HttpStatusCode.Conflict },
            new[] { first.StatusCode, second.StatusCode });
    }

    [Fact]
    public async Task Concurrent_POST_same_key_has_one_identity()
    {
        var quote = Guid.NewGuid();
        var key = Key();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CreateIdentityAsync(quote, key)));
        Assert.Single(responses.Distinct());
    }

    [Fact]
    public async Task Concurrent_POST_distinct_keys_for_same_quote_has_one_201()
    {
        var quote = Guid.NewGuid();
        var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var response = await CreateAsync(quote, Key());
            return response.StatusCode;
        }));
        Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.Created));
        Assert.Equal(7, statuses.Count(status => status == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task GET_list_requires_authentication_and_tenant()
    {
        using var anonymous = await client.GetAsync("/api/v1/orders");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var foreign = Authenticated(HttpMethod.Get, "/api/v1/orders", MockIdentityProfiles.ForeignOrganizationId);
        using var forbidden = await client.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task GET_list_supports_filters_owner_and_fail_closed_cursor()
    {
        var created = await CreateIdentityAsync(Guid.NewGuid(), Key());
        using var current = await SendAuthenticatedAsync(HttpMethod.Get,
            $"/api/v1/orders?status=DRAFT&owner_org_id={MockIdentityProfiles.ViewerOrganizationId:D}");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var currentBody = await current.Content.ReadAsStringAsync();
        Assert.Contains(created.ToString("D"), currentBody, StringComparison.OrdinalIgnoreCase);

        using var foreignOwner = await SendAuthenticatedAsync(HttpMethod.Get,
            $"/api/v1/orders?owner_org_id={MockIdentityProfiles.ForeignOrganizationId:D}");
        Assert.Equal("{\"items\":[],\"next_cursor\":null}", await foreignOwner.Content.ReadAsStringAsync());

        using var invalidCursor = await SendAuthenticatedAsync(HttpMethod.Get, "/api/v1/orders?cursor=invalid");
        Assert.Equal("{\"items\":[],\"next_cursor\":null}", await invalidCursor.Content.ReadAsStringAsync());

        var cursor = OrderCursorCodec.Encode(DateTimeOffset.UtcNow, Guid.NewGuid());
        using var validCursor = await SendAuthenticatedAsync(HttpMethod.Get, $"/api/v1/orders?cursor={cursor}");
        Assert.Equal(HttpStatusCode.OK, validCursor.StatusCode);
    }

    [Fact]
    public async Task GET_detail_is_uniform_and_timeline_exposes_no_internal_payload()
    {
        var orderId = await CreateIdentityAsync(Guid.NewGuid(), Key());
        using var visible = await SendAuthenticatedAsync(HttpMethod.Get, $"/api/v1/orders/{orderId:D}");
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
        var body = await visible.Content.ReadAsStringAsync();
        Assert.Contains("\"event_type\":\"ORDER_CREATED\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actor_id", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("aggregate_version", body, StringComparison.OrdinalIgnoreCase);

        using var missing = await SendAuthenticatedAsync(HttpMethod.Get, $"/api/v1/orders/{Guid.NewGuid():D}");
        using var foreign = await SendAuthenticatedAsync(HttpMethod.Get,
            $"/api/v1/orders/{OrderHttpWebApplicationFactory.ForeignOrderId:D}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private async Task<Guid> CreateIdentityAsync(Guid quoteId, string key)
    {
        using var response = await CreateAsync(quoteId, key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> CreateAsync(Guid quoteId, string key)
    {
        var request = Authenticated(HttpMethod.Post, "/api/v1/orders");
        request.Headers.Add("Idempotency-Key", key);
        request.Content = ValidBody(quoteId);
        return client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(HttpMethod method, string path)
    {
        using var request = Authenticated(method, path);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string path, Guid? organizationId = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveViewer);
        request.Headers.Add("X-Organization-Id",
            (organizationId ?? MockIdentityProfiles.ViewerOrganizationId).ToString("D"));
        return request;
    }

    private static JsonContent ValidBody(Guid quoteId, string payerType = "SENDER", string channel = "WEB") =>
        JsonContent.Create(new
        {
            quote_id = quoteId,
            payer_type = payerType,
            acceptance = new
            {
                terms_version = "terms-synthetic-v1",
                privacy_version = "privacy-synthetic-v1",
                accepted_at = "2026-07-22T12:00:00.1234567Z",
                acceptance_channel = channel,
            },
        });

    private static string Key() => $"orders-http-{Guid.NewGuid():N}";
}
