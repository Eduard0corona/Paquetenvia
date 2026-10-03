using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Paqueteria.IntegrationTests.Custody;

namespace Paqueteria.IntegrationTests.Orders;

/// <summary>
/// OPS-003-SERVER-72H-REJECTION through the assembled API: a driver replay captured more than 72
/// hours ago is refused with 409 OFFLINE_OPERATION_EXPIRED before its service runs, a timestamp
/// beyond the clock tolerance is an invalid request, and an absent optional timestamp is an online
/// operation that proceeds as before.
/// </summary>
public sealed class OfflineOperationAgeTransitionHttpTests : IClassFixture<OrderHttpWebApplicationFactory>
{
    private readonly OrderHttpWebApplicationFactory factory;
    private readonly HttpClient client;

    public OfflineOperationAgeTransitionHttpTests(OrderHttpWebApplicationFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task A_replay_captured_71_hours_59_minutes_ago_reaches_the_transition()
    {
        var orderId = await CreateOrderAsync();
        using var response = await TransitionAsync(orderId, OfflineAge.Ago(TimeSpan.FromHours(71) + TimeSpan.FromMinutes(59)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(orderId, factory.LastTransitionCommand!.OrderId);
    }

    [Fact]
    public async Task A_replay_captured_72_hours_and_one_second_ago_is_refused_before_the_service()
    {
        var orderId = await CreateOrderAsync();
        var calls = factory.TransitionCallCount;

        using var response = await TransitionAsync(orderId, OfflineAge.Ago(TimeSpan.FromHours(72) + TimeSpan.FromSeconds(1)));

        await OfflineAge.AssertExpiredAsync(response);
        Assert.Equal(calls, factory.TransitionCallCount);
        Assert.Equal(0, factory.TransitionEffectCount(orderId));
    }

    [Fact]
    public async Task A_timestamp_beyond_the_clock_tolerance_is_an_uncoded_invalid_request()
    {
        var orderId = await CreateOrderAsync();
        var calls = factory.TransitionCallCount;

        using var response = await TransitionAsync(orderId, OfflineAge.Ago(-TimeSpan.FromMinutes(6)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Conflict.", body.RootElement.GetProperty("title").GetString());
        Assert.False(body.RootElement.TryGetProperty("code", out _));
        Assert.Equal(calls, factory.TransitionCallCount);
    }

    [Fact]
    public async Task A_missing_client_timestamp_is_an_online_transition_and_proceeds()
    {
        var orderId = await CreateOrderAsync();

        using var response = await TransitionAsync(orderId, clientOccurredAt: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_replay_is_refused_even_when_its_key_was_already_used()
    {
        var orderId = await CreateOrderAsync();
        var key = $"offline-age-{Guid.NewGuid():N}";
        using (var first = await TransitionAsync(orderId, OfflineAge.Ago(TimeSpan.FromHours(1)), key))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        // The client timestamp is not part of the idempotent request: once it is older than 72 hours,
        // the replay is discarded whatever the key still holds.
        using var stale = await TransitionAsync(orderId, OfflineAge.Ago(TimeSpan.FromHours(73)), key);
        await OfflineAge.AssertExpiredAsync(stale);
        Assert.Equal(1, factory.TransitionEffectCount(orderId));
    }

    private async Task<Guid> CreateOrderAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new
            {
                quote_id = Guid.NewGuid(),
                payer_type = "SENDER",
                restricted_goods_acknowledged = true,
                acceptance = new
                {
                    terms_version = "terms-synthetic-v1",
                    privacy_version = "privacy-synthetic-v1",
                    // AI05-INPUT-LIMITS: acceptance must fall inside the server window, so it is "just now".
                    accepted_at = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture),
                    acceptance_channel = "WEB",
                },
            }),
        };
        OfflineAge.Authorize(request, MockIdentityProfiles.ActivePlatformAdminMfa);
        request.Headers.Add("Idempotency-Key", $"offline-age-{Guid.NewGuid():N}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> TransitionAsync(Guid orderId, string? clientOccurredAt, string? key = null)
    {
        var body = clientOccurredAt is null
            ? """{"target_status":"CANCELLED","reason":"synthetic cancellation","expected_version":1}"""
            : $$"""{"target_status":"CANCELLED","reason":"synthetic cancellation","expected_version":1,"client_occurred_at":"{{clientOccurredAt}}"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderId:D}/transitions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        OfflineAge.Authorize(request, MockIdentityProfiles.ActivePlatformAdminMfa);
        request.Headers.Add("Idempotency-Key", key ?? $"offline-age-{Guid.NewGuid():N}");
        return client.SendAsync(request);
    }
}

/// <summary>The same rule on the two POD operations a driver replays from its offline queue.</summary>
public sealed class OfflineOperationAgeProofHttpTests(CustodyHttpWebApplicationFactory factory)
    : IClassFixture<CustodyHttpWebApplicationFactory>
{
    private const string Sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task Finalization_accepts_a_capture_71_hours_59_minutes_old()
    {
        using var response = await FinalizeAsync(OfflineAge.Ago(TimeSpan.FromHours(71) + TimeSpan.FromMinutes(59)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Finalization_refuses_a_capture_72_hours_and_one_second_old()
    {
        using var response = await FinalizeAsync(OfflineAge.Ago(TimeSpan.FromHours(72) + TimeSpan.FromSeconds(1)));

        await OfflineAge.AssertExpiredAsync(response);
    }

    [Fact]
    public async Task Finalization_refuses_a_capture_beyond_the_clock_tolerance_as_invalid()
    {
        using var response = await FinalizeAsync(OfflineAge.Ago(-TimeSpan.FromMinutes(6)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("INVALID_REQUEST", await OfflineAge.CodeAsync(response));
    }

    [Fact]
    public async Task Finalization_without_captured_at_stays_an_invalid_request()
    {
        using var response = await FinalizeAsync(capturedAt: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("INVALID_REQUEST", await OfflineAge.CodeAsync(response));
    }

    [Fact]
    public async Task Upload_session_applies_the_rule_only_when_a_client_timestamp_is_declared()
    {
        using (var accepted = await UploadAsync(OfflineAge.Ago(TimeSpan.FromHours(71) + TimeSpan.FromMinutes(59))))
        {
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }

        using (var online = await UploadAsync(clientOccurredAt: null))
        {
            Assert.Equal(HttpStatusCode.Created, online.StatusCode);
        }

        using (var expired = await UploadAsync(OfflineAge.Ago(TimeSpan.FromHours(72) + TimeSpan.FromSeconds(1))))
        {
            await OfflineAge.AssertExpiredAsync(expired);
        }

        using var future = await UploadAsync(OfflineAge.Ago(-TimeSpan.FromMinutes(6)));
        Assert.Equal(HttpStatusCode.Conflict, future.StatusCode);
        Assert.Equal("INVALID_REQUEST", await OfflineAge.CodeAsync(future));
    }

    private Task<HttpResponseMessage> FinalizeAsync(string? capturedAt)
    {
        var captured = capturedAt is null ? string.Empty : $$""","captured_at":"{{capturedAt}}" """;
        var body = $$"""{"upload_session_id":"{{Guid.NewGuid():D}}","proof_type":"DELIVERY_PHOTO","sha256":"{{Sha256}}"{{captured}}}""";
        return SendAsync($"/api/v1/orders/{Guid.NewGuid():D}/proofs", body);
    }

    private Task<HttpResponseMessage> UploadAsync(string? clientOccurredAt)
    {
        var occurred = clientOccurredAt is null ? string.Empty : $$""","client_occurred_at":"{{clientOccurredAt}}" """;
        var body = $$"""{"proof_type":"PICKUP_PHOTO","content_type":"image/png","size_bytes":8,"sha256":"{{Sha256}}"{{occurred}}}""";
        return SendAsync($"/api/v1/orders/{Guid.NewGuid():D}/proof-upload-sessions", body);
    }

    private Task<HttpResponseMessage> SendAsync(string path, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        OfflineAge.Authorize(request, MockIdentityProfiles.ActiveDispatcher);
        request.Headers.Add("Idempotency-Key", $"offline-age-{Guid.NewGuid():N}");
        return client.SendAsync(request);
    }
}

internal static class OfflineAge
{
    /// <summary>A UTC ISO-8601 instant this long before the server clock (negative: in the future).</summary>
    public static string Ago(TimeSpan age) =>
        (DateTimeOffset.UtcNow - age).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    public static void Authorize(HttpRequestMessage request, string profile)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
    }

    public static async Task AssertExpiredAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(409, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Conflict.", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("OFFLINE_OPERATION_EXPIRED", body.RootElement.GetProperty("code").GetString());
    }

    public static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
