using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Infrastructure.Mock;

namespace Paqueteria.IntegrationTests.Orders;

public sealed class OrderTransitionHttpTests : IClassFixture<OrderHttpWebApplicationFactory>
{
    private readonly OrderHttpWebApplicationFactory factory;
    private readonly HttpClient client;

    public OrderTransitionHttpTests(OrderHttpWebApplicationFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task POST_transition_requires_authentication()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/orders/{Guid.NewGuid():D}/transitions")
        {
            Content = TransitionBody("CANCELLED", "synthetic cancellation", 1),
        };
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        request.Headers.Add("Idempotency-Key", Key());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task POST_transition_requires_active_tenant_and_capability()
    {
        using var noTenant = Authorized(
            Guid.NewGuid(),
            Key(),
            MockIdentityProfiles.ActivePlatformAdminMfa,
            includeTenant: false);
        noTenant.Content = TransitionBody("CANCELLED", "synthetic cancellation", 1);
        using var noTenantResponse = await client.SendAsync(noTenant);
        Assert.Equal(HttpStatusCode.Forbidden, noTenantResponse.StatusCode);

        var orderId = await CreateOrderAsync(MockIdentityProfiles.ActiveViewer);
        using var viewer = Authorized(orderId, Key(), MockIdentityProfiles.ActiveViewer);
        viewer.Content = TransitionBody("CANCELLED", "synthetic cancellation", 1);
        using var viewerResponse = await client.SendAsync(viewer);
        Assert.Equal(HttpStatusCode.Forbidden, viewerResponse.StatusCode);
    }

    [Fact]
    public async Task POST_transition_requires_MFA_for_platform_admin()
    {
        var orderId = await CreateOrderAsync(MockIdentityProfiles.ActivePlatformAdminMfa);
        using var request = Authorized(
            orderId,
            Key(),
            MockIdentityProfiles.ActivePlatformAdminNoMfa);
        request.Content = TransitionBody("CANCELLED", "synthetic cancellation", 1);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task POST_transition_rejects_missing_or_short_key(string? key)
    {
        using var request = Authorized(
            Guid.NewGuid(),
            key,
            MockIdentityProfiles.ActivePlatformAdminMfa);
        request.Content = TransitionBody("CANCELLED", "synthetic cancellation", 1);
        using var response = await client.SendAsync(request);
        await AssertConflictAsync(response);
    }

    [Fact]
    public async Task POST_transition_rejects_long_or_multiple_keys()
    {
        using var tooLong = Authorized(
            Guid.NewGuid(),
            new string('k', 129),
            MockIdentityProfiles.ActivePlatformAdminMfa);
        tooLong.Content = TransitionBody("CANCELLED", "synthetic cancellation", 1);
        using var longResponse = await client.SendAsync(tooLong);
        await AssertConflictAsync(longResponse);

        using var multiple = Authorized(
            Guid.NewGuid(),
            null,
            MockIdentityProfiles.ActivePlatformAdminMfa);
        multiple.Headers.TryAddWithoutValidation("Idempotency-Key", new[] { Key(), Key() });
        multiple.Content = TransitionBody("CANCELLED", "synthetic cancellation", 1);
        using var multipleResponse = await client.SendAsync(multiple);
        await AssertConflictAsync(multipleResponse);
    }

    public static TheoryData<object> InvalidBodies => new()
    {
        new { reason = "synthetic", expected_version = 1 },
        new { target_status = "UNKNOWN", reason = "synthetic", expected_version = 1 },
        new { target_status = "CANCELLED", expected_version = 1 },
        new { target_status = "CANCELLED", reason = "   ", expected_version = 1 },
        new { target_status = "CANCELLED", reason = new string('r', 501), expected_version = 1 },
        new { target_status = "CANCELLED", reason = "synthetic" },
        new { target_status = "CANCELLED", reason = "synthetic", expected_version = 0 },
        new { target_status = "CANCELLED", reason = "synthetic", expected_version = 1, metadata = new[] { "bad" } },
        new { target_status = "CANCELLED", reason = "synthetic", expected_version = 1, metadata = new { unknown = true } },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task POST_transition_rejects_invalid_contract_shape_with_uniform_409(object body)
    {
        using var request = Authorized(
            Guid.NewGuid(),
            Key(),
            MockIdentityProfiles.ActivePlatformAdminMfa);
        request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        await AssertConflictAsync(response);
    }

    [Fact]
    public async Task POST_transition_hides_missing_and_foreign_orders_behind_same_409()
    {
        using var missing = await TransitionAsync(
            Guid.NewGuid(),
            Key(),
            "CANCELLED",
            1);
        using var foreign = await TransitionAsync(
            OrderHttpWebApplicationFactory.ForeignOrderId,
            Key(),
            "CANCELLED",
            1);
        await AssertConflictAsync(missing);
        await AssertConflictAsync(foreign);
        var missingBody = await missing.Content.ReadAsStringAsync();
        var foreignBody = await foreign.Content.ReadAsStringAsync();
        Assert.DoesNotContain(OrderHttpWebApplicationFactory.ForeignOrderId.ToString("D"), foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("foreign", foreignBody, StringComparison.OrdinalIgnoreCase);
        using var missingJson = JsonDocument.Parse(missingBody);
        using var foreignJson = JsonDocument.Parse(foreignBody);
        Assert.Equal(
            missingJson.RootElement.GetProperty("title").GetString(),
            foreignJson.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task POST_transition_rejects_invalid_state_version_terminal_and_guard_failure()
    {
        var orderId = await CreateOrderAsync(MockIdentityProfiles.ActivePlatformAdminMfa);
        using var invalidState = await TransitionAsync(orderId, Key(), "DELIVERED", 1);
        await AssertConflictAsync(invalidState);

        using var wrongVersion = await TransitionAsync(orderId, Key(), "CANCELLED", 2);
        await AssertConflictAsync(wrongVersion);

        using var missingGuard = await TransitionAsync(orderId, Key(), "CONFIRMED", 1, metadata: new { });
        await AssertConflictAsync(missingGuard);

        using var cancelled = await TransitionAsync(orderId, Key(), "CANCELLED", 1);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        using var terminal = await TransitionAsync(orderId, Key(), "CONFIRMED", 2,
            metadata: new { restricted_goods_acknowledged = true });
        await AssertConflictAsync(terminal);
    }

    [Fact]
    public async Task POST_transition_returns_incremented_order_and_replays_identical_safe_200()
    {
        var orderId = await CreateOrderAsync(MockIdentityProfiles.ActivePlatformAdminMfa);
        var key = Key();
        using var first = await TransitionAsync(orderId, key, "CANCELLED", 1);
        using var replay = await TransitionAsync(orderId, key, "CANCELLED", 1);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(firstBody);
        Assert.Equal("CANCELLED", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("version").GetInt32());
        Assert.DoesNotContain("synthetic cancellation", firstBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reason", firstBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("metadata", firstBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actor", firstBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task POST_transition_same_key_changed_body_conflicts()
    {
        var orderId = await CreateOrderAsync(MockIdentityProfiles.ActivePlatformAdminMfa);
        var key = Key();
        using var first = await TransitionAsync(orderId, key, "CANCELLED", 1);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var changed = await TransitionAsync(orderId, key, "CANCELLED", 1, "changed exact reason");
        await AssertConflictAsync(changed);
    }

    [Fact]
    public async Task Completed_replay_rechecks_current_role_and_MFA_without_new_effects()
    {
        var orderId = await CreateOrderAsync(MockIdentityProfiles.ActivePlatformAdminMfa);
        var key = Key();
        using var first = await TransitionAsync(orderId, key, "CANCELLED", 1);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var storedBody = await first.Content.ReadAsStringAsync();

        using var viewer = await TransitionAsync(
            orderId, key, "CANCELLED", 1, profile: MockIdentityProfiles.ActiveViewer);
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
        var viewerBody = await viewer.Content.ReadAsStringAsync();
        using (var storedJson = JsonDocument.Parse(storedBody))
        {
            Assert.DoesNotContain(
                storedJson.RootElement.GetProperty("public_id").GetString()!,
                viewerBody,
                StringComparison.Ordinal);
        }
        Assert.DoesNotContain("\"status\":\"CANCELLED\"", viewerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"version\":2", viewerBody, StringComparison.Ordinal);

        using var adminWithoutMfa = await TransitionAsync(
            orderId, key, "CANCELLED", 1, profile: MockIdentityProfiles.ActivePlatformAdminNoMfa);
        Assert.Equal(HttpStatusCode.Forbidden, adminWithoutMfa.StatusCode);

        using var dispatcher = await TransitionAsync(
            orderId, key, "CANCELLED", 1, profile: MockIdentityProfiles.ActiveDispatcher);
        Assert.Equal(HttpStatusCode.OK, dispatcher.StatusCode);
        Assert.Equal(storedBody, await dispatcher.Content.ReadAsStringAsync());
        Assert.Equal(1, factory.TransitionEffectCount(orderId));

        using var hashConflict = await TransitionAsync(
            orderId,
            key,
            "CANCELLED",
            1,
            reason: "a different exact reason",
            profile: MockIdentityProfiles.ActiveViewer);
        await AssertConflictAsync(hashConflict);
        Assert.Equal(1, factory.TransitionEffectCount(orderId));
    }

    [Fact]
    public async Task Completed_driver_replay_requires_the_current_exact_active_assignment()
    {
        var orderId = await CreateOrderAsync(MockIdentityProfiles.ActivePlatformAdminMfa);
        using var confirmed = await TransitionAsync(
            orderId,
            Key(),
            "CONFIRMED",
            1,
            metadata: new { restricted_goods_acknowledged = true });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        using var ready = await TransitionAsync(orderId, Key(), "READY_FOR_PICKUP", 2);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var assigned = await TransitionAsync(orderId, Key(), "ASSIGNED", 3);
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

        var key = Key();
        using var original = await TransitionAsync(orderId, key, "AT_PICKUP", 4);
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        var storedBody = await original.Content.ReadAsStringAsync();

        factory.SetDriverAssignment(orderId, false);
        using var missing = await TransitionAsync(
            orderId, key, "AT_PICKUP", 4, profile: MockIdentityProfiles.ActiveDriver);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);

        factory.SetDriverAssignment(orderId, true);
        using var allowed = await TransitionAsync(
            orderId, key, "AT_PICKUP", 4, profile: MockIdentityProfiles.ActiveDriver);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(storedBody, await allowed.Content.ReadAsStringAsync());
        Assert.Equal(4, factory.TransitionEffectCount(orderId));
    }

    [Fact]
    public async Task Concurrent_distinct_keys_with_same_expected_version_have_exactly_one_200()
    {
        var orderId = await CreateOrderAsync(MockIdentityProfiles.ActivePlatformAdminMfa);
        var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var response = await TransitionAsync(orderId, Key(), "CANCELLED", 1);
            return response.StatusCode;
        }));
        Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.OK));
        Assert.Equal(7, statuses.Count(status => status == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task GET_detail_reflects_transition_and_timeline_hides_internal_payload()
    {
        var orderId = await CreateOrderAsync(MockIdentityProfiles.ActivePlatformAdminMfa);
        using var transition = await TransitionAsync(orderId, Key(), "CANCELLED", 1);
        Assert.Equal(HttpStatusCode.OK, transition.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/orders/{orderId:D}");
        Authorize(request, MockIdentityProfiles.ActivePlatformAdminMfa, true);
        using var detail = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var body = await detail.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"CANCELLED\"", body, StringComparison.Ordinal);
        Assert.Contains("\"event_type\":\"ORDER_STATUS_CHANGED\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reason", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actor_id", body, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Guid> CreateOrderAsync(string profile)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new
            {
                quote_id = Guid.NewGuid(),
                payer_type = "SENDER",
                acceptance = new
                {
                    terms_version = "terms-synthetic-v1",
                    privacy_version = "privacy-synthetic-v1",
                    accepted_at = "2026-07-23T12:00:00.0000000Z",
                    acceptance_channel = "WEB",
                },
            }),
        };
        Authorize(request, profile, true);
        request.Headers.Add("Idempotency-Key", Key());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> TransitionAsync(
        Guid orderId,
        string key,
        string target,
        int expectedVersion,
        string reason = "synthetic cancellation",
        object? metadata = null,
        string profile = MockIdentityProfiles.ActivePlatformAdminMfa)
    {
        var request = Authorized(orderId, key, profile);
        request.Content = TransitionBody(target, reason, expectedVersion, metadata);
        return client.SendAsync(request);
    }

    private static HttpRequestMessage Authorized(
        Guid orderId,
        string? key,
        string profile,
        bool includeTenant = true)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/orders/{orderId:D}/transitions");
        Authorize(request, profile, includeTenant);
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return request;
    }

    private static void Authorize(HttpRequestMessage request, string profile, bool includeTenant)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        if (includeTenant)
        {
            request.Headers.Add(
                "X-Organization-Id",
                MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        }
    }

    private static JsonContent TransitionBody(
        string target,
        string reason,
        int expectedVersion,
        object? metadata = null) =>
        JsonContent.Create(new
        {
            target_status = target,
            reason,
            expected_version = expectedVersion,
            metadata,
        });

    private static async Task AssertConflictAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(409, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Conflict.", body.RootElement.GetProperty("title").GetString());
    }

    private static string Key() => $"transition-http-{Guid.NewGuid():N}";
}
