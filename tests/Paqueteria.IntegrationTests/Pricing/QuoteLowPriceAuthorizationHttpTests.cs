using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Infrastructure.Mock;

namespace Paqueteria.IntegrationTests.Pricing;

/// <summary>
/// LOW-PRICE-MANUAL-AUTH-2026-10-02 over HTTP: who may send <c>low_price_authorization</c> on createQuote, the
/// capability decided before the quote service is called, the uniform 409 for an authorization the price does not
/// need, and who reads the actor and reason back.
/// </summary>
public sealed class QuoteLowPriceAuthorizationHttpTests : IClassFixture<QuoteHttpWebApplicationFactory>
{
    private readonly QuoteHttpWebApplicationFactory factory;
    private readonly HttpClient client;

    public QuoteLowPriceAuthorizationHttpTests(QuoteHttpWebApplicationFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDispatcher)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa)]
    public async Task Dispatcher_and_platform_admin_with_mfa_create_an_authorized_quote_with_the_trimmed_reason(string profile)
    {
        var key = $"lpma-allowed-{Guid.NewGuid():N}";
        using var response = await SendCreateAsync(profile, key, "  Cliente ancla, ruta en consolidación  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var command = Assert.Single(factory.ReceivedCommands, received => received.IdempotencyKey == key);
        Assert.Equal("Cliente ancla, ruta en consolidación", command.LowPriceAuthorization!.Reason);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var authorization = json.RootElement.GetProperty("low_price_authorization");
        Assert.Equal(command.ActorId, authorization.GetProperty("actor_id").GetGuid());
        Assert.Equal("Cliente ancla, ruta en consolidación", authorization.GetProperty("reason").GetString());
        Assert.Equal(QuoteHttpWebApplicationFactory.QuoteExpiresAt, authorization.GetProperty("valid_until").GetDateTimeOffset());
    }

    [Fact]
    public async Task Platform_admin_without_mfa_receives_MFA_REQUIRED_before_the_service_is_called()
    {
        var key = $"lpma-nomfa-{Guid.NewGuid():N}";
        using var response = await SendCreateAsync(MockIdentityProfiles.ActivePlatformAdminNoMfa, key, "Motivo operativo");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("MFA_REQUIRED", json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(factory.ReceivedCommands, received => received.IdempotencyKey == key);

        // Without the field the same PLATFORM_ADMIN still quotes: createQuote itself never demanded MFA.
        var plainKey = $"lpma-nomfa-plain-{Guid.NewGuid():N}";
        using var plain = await SendCreateAsync(MockIdentityProfiles.ActivePlatformAdminNoMfa, plainKey, reason: null);
        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
        using var plainJson = JsonDocument.Parse(await plain.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, plainJson.RootElement.GetProperty("low_price_authorization").ValueKind);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveViewer)]
    [InlineData(MockIdentityProfiles.ActiveFinanceMfa)]
    [InlineData(MockIdentityProfiles.ActiveDriver)]
    [InlineData(MockIdentityProfiles.ActiveBusinessAdmin)]
    public async Task Every_other_role_receives_a_generic_403_before_the_service_is_called(string profile)
    {
        var key = $"lpma-denied-{Guid.NewGuid():N}";
        using var response = await SendCreateAsync(profile, key, "Motivo operativo");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.TryGetProperty("code", out _));
        Assert.DoesNotContain(factory.ReceivedCommands, received => received.IdempotencyKey == key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("línea uno\nlínea dos")]
    public async Task An_invalid_reason_is_422_before_the_capability_and_the_service(string reason)
    {
        var key = $"lpma-invalid-{Guid.NewGuid():N}";
        using var response = await SendCreateAsync(MockIdentityProfiles.ActiveDispatcher, key, reason);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain(factory.ReceivedCommands, received => received.IdempotencyKey == key);
    }

    [Fact]
    public async Task A_reason_longer_than_200_characters_after_trimming_is_422()
    {
        using var exactly200 = await SendCreateAsync(
            MockIdentityProfiles.ActiveDispatcher, $"lpma-200-{Guid.NewGuid():N}", "  " + new string('a', 200) + "  ");
        Assert.Equal(HttpStatusCode.Created, exactly200.StatusCode);

        using var tooLong = await SendCreateAsync(
            MockIdentityProfiles.ActiveDispatcher, $"lpma-201-{Guid.NewGuid():N}", new string('a', 201));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
    }

    [Fact]
    public async Task A_missing_reason_inside_the_object_is_422()
    {
        using var request = Authenticated(MockIdentityProfiles.ActiveDispatcher);
        request.Headers.Add("Idempotency-Key", $"lpma-no-reason-{Guid.NewGuid():N}");
        request.Content = JsonContent.Create(Body("Synthetic origin 100", new { }));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task An_authorization_the_price_does_not_need_is_the_uniform_409()
    {
        using var response = await SendCreateAsync(
            MockIdentityProfiles.ActiveDispatcher,
            $"lpma-not-needed-{Guid.NewGuid():N}",
            "Motivo operativo",
            originAddress: "NOT_NEEDED synthetic origin");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Conflict.", json.RootElement.GetProperty("title").GetString());
        Assert.False(json.RootElement.TryGetProperty("code", out _));
        Assert.DoesNotContain("Motivo operativo", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_key_with_another_reason_is_an_idempotency_conflict_and_the_same_reason_replays()
    {
        var key = $"lpma-replay-{Guid.NewGuid():N}";
        using var first = await SendCreateAsync(MockIdentityProfiles.ActiveDispatcher, key, "Motivo operativo");
        using var replay = await SendCreateAsync(MockIdentityProfiles.ActiveDispatcher, key, "Motivo operativo");
        using var changed = await SendCreateAsync(MockIdentityProfiles.ActiveDispatcher, key, "Otro motivo");
        using var withoutAuthorization = await SendCreateAsync(MockIdentityProfiles.ActiveDispatcher, key, reason: null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutAuthorization.StatusCode);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDispatcher, true)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa, true)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa, false)]
    [InlineData(MockIdentityProfiles.ActiveViewer, false)]
    public async Task Actor_and_reason_are_returned_only_to_callers_that_read_order_financials(string profile, bool details)
    {
        using var request = Authenticated(profile, HttpMethod.Get, $"/api/v1/quotes/{QuoteHttpWebApplicationFactory.AuthorizedQuoteId:D}");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var authorization = json.RootElement.GetProperty("low_price_authorization");
        Assert.Equal(QuoteHttpWebApplicationFactory.QuoteExpiresAt, authorization.GetProperty("valid_until").GetDateTimeOffset());
        Assert.Equal(details, authorization.TryGetProperty("actor_id", out _));
        Assert.Equal(details, authorization.TryGetProperty("reason", out _));
        Assert.Equal(details, body.Contains(QuoteHttpWebApplicationFactory.AuthorizingActorId.ToString("D"), StringComparison.Ordinal));
        Assert.Equal(details, body.Contains("Cliente ancla", StringComparison.Ordinal));
        Assert.DoesNotContain("financial_override", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quote_without_authorization_returns_a_null_low_price_authorization()
    {
        using var request = Authenticated(
            MockIdentityProfiles.ActiveDispatcher,
            HttpMethod.Get,
            $"/api/v1/quotes/{QuoteHttpWebApplicationFactory.ActiveQuoteId:D}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("low_price_authorization").ValueKind);
    }

    private async Task<HttpResponseMessage> SendCreateAsync(
        string profile,
        string key,
        string? reason,
        string originAddress = "Synthetic origin 100")
    {
        using var request = Authenticated(profile);
        request.Headers.Add("Idempotency-Key", key);
        request.Content = JsonContent.Create(Body(originAddress, reason is null ? null : new { reason }));
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Authenticated(
        string profile,
        HttpMethod? method = null,
        string path = "/api/v1/quotes")
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        return request;
    }

    private static object Body(string originAddress, object? authorization) => new
    {
        client_account_id = (Guid?)null,
        origin = new
        {
            address_text = originAddress,
            contact_name = "Synthetic Sender",
            phone = "+526671111111",
            lat = 24.8,
            lng = -107.4,
        },
        destination = new
        {
            address_text = "Synthetic destination 200",
            contact_name = "Synthetic Receiver",
            phone = "+526672222222",
            lat = 24.81,
            lng = -107.41,
        },
        service_type = "SAME_DAY",
        consolidated_route = false,
        packages = new[]
        {
            new
            {
                description = "Synthetic parcel",
                weight_grams = 1000,
                declared_value_cents = 5000L,
            },
        },
        low_price_authorization = authorization,
    };
}
