using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dispatch.Application.Stops;
using Identity.Infrastructure.Mock;

namespace Paqueteria.IntegrationTests.Dispatch;

public sealed class DispatchHttpTests : IClassFixture<DispatchHttpWebApplicationFactory>
{
    private readonly DispatchHttpWebApplicationFactory factory;
    private readonly HttpClient client;

    public DispatchHttpTests(DispatchHttpWebApplicationFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task POST_requires_authentication_and_tenant()
    {
        using var anonymous = AssignmentRequest(Guid.NewGuid(), Key(), null, includeTenant: true);
        using var anonymousResponse = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var noTenant = AssignmentRequest(
            Guid.NewGuid(),
            Key(),
            MockIdentityProfiles.ActiveDispatcher,
            includeTenant: false);
        using var noTenantResponse = await client.SendAsync(noTenant);
        Assert.Equal(HttpStatusCode.Forbidden, noTenantResponse.StatusCode);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveViewer)]
    [InlineData(MockIdentityProfiles.ActiveDriver)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa)]
    public async Task POST_denies_non_dispatch_capabilities(string profile)
    {
        using var request = AssignmentRequest(Guid.NewGuid(), Key(), profile);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_rejects_missing_short_long_and_multiple_keys()
    {
        foreach (var key in new string?[] { null, "short", new('k', 129) })
        {
            using var request = AssignmentRequest(
                Guid.NewGuid(),
                key,
                MockIdentityProfiles.ActiveDispatcher);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        using var multiple = AssignmentRequest(
            Guid.NewGuid(),
            null,
            MockIdentityProfiles.ActiveDispatcher);
        multiple.Headers.TryAddWithoutValidation("Idempotency-Key", [Key(), Key()]);
        using var multipleResponse = await client.SendAsync(multiple);
        Assert.Equal(HttpStatusCode.Conflict, multipleResponse.StatusCode);
    }

    public static TheoryData<string> InvalidBodies => new()
    {
        """{"assignment_type":"OWN","cost_cents":0}""",
        """{"driver_id":"00000000-0000-0000-0000-000000000000","assignment_type":"OWN","cost_cents":0}""",
        $$"""{"driver_id":"{{Guid.NewGuid():D}}","cost_cents":0}""",
        $$"""{"driver_id":"{{Guid.NewGuid():D}}","assignment_type":"EXTERNAL","cost_cents":0}""",
        $$"""{"driver_id":"{{Guid.NewGuid():D}}","assignment_type":"ALLY_CAPACITY","cost_cents":0}""",
        $$"""{"driver_id":"{{Guid.NewGuid():D}}","assignment_type":"OWN"}""",
        $$"""{"driver_id":"{{Guid.NewGuid():D}}","assignment_type":"OWN","cost_cents":-1}""",
        $$"""{"driver_id":"{{Guid.NewGuid():D}}","assignment_type":"OWN","cost_cents":0,"route_id":"{{Guid.NewGuid():D}}"}""",
        $$"""{"driver_id":"{{Guid.NewGuid():D}}","assignment_type":"OWN","cost_cents":0,"unknown":true}""",
        $$"""{"driver_id":"{{Guid.NewGuid():D}}","assignment_type":"OWN","cost_cents":9223372036854775808}""",
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task POST_rejects_invalid_or_out_of_scope_body_with_uniform_409(string body)
    {
        using var request = AssignmentRequest(
            Guid.NewGuid(),
            Key(),
            MockIdentityProfiles.ActiveDispatcher,
            body: body);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task POST_rejects_invalid_order_id()
    {
        using var request = AssignmentRequest(
            "not-a-uuid",
            Key(),
            MockIdentityProfiles.ActiveDispatcher);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task POST_returns_exact_safe_201_and_replays_without_effects()
    {
        var orderId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var key = Key();
        var before = factory.Effects;
        using var firstRequest = AssignmentRequest(
            orderId,
            key,
            MockIdentityProfiles.ActiveDispatcher,
            driverId: driverId,
            costCents: (long)int.MaxValue + 1);
        using var first = await client.SendAsync(firstRequest);
        using var replayRequest = AssignmentRequest(
            orderId,
            key,
            MockIdentityProfiles.ActivePlatformAdminMfa,
            driverId: driverId,
            costCents: (long)int.MaxValue + 1);
        using var replay = await client.SendAsync(replayRequest);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var body = await first.Content.ReadAsStringAsync();
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            ["cost", "driver_id", "id", "order_id", "status"],
            json.RootElement.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        Assert.Equal("ACCEPTED", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("MXN", json.RootElement.GetProperty("cost").GetProperty("currency").GetString());
        Assert.Equal((long)int.MaxValue + 1,
            json.RootElement.GetProperty("cost").GetProperty("amount_cents").GetInt64());
        Assert.Equal(before + 1, factory.Effects);
        Assert.DoesNotContain("owner", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("operator", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("route", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accepted_at", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task POST_same_key_changed_body_conflicts_and_public_rejections_are_safe()
    {
        var orderId = Guid.NewGuid();
        var key = Key();
        using var firstRequest = AssignmentRequest(
            orderId,
            key,
            MockIdentityProfiles.ActiveDispatcher);
        using var first = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var changedRequest = AssignmentRequest(
            orderId,
            key,
            MockIdentityProfiles.ActiveDispatcher,
            driverId: Guid.NewGuid());
        using var changed = await client.SendAsync(changedRequest);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);

        using var expiredRequest = AssignmentRequest(
            Guid.NewGuid(),
            Key(),
            MockIdentityProfiles.ActiveDispatcher,
            driverId: DispatchHttpWebApplicationFactory.ExpiredDocumentDriverId);
        using var expired = await client.SendAsync(expiredRequest);
        Assert.Contains("DRIVER_DOCUMENT_EXPIRED", await expired.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var ineligibleRequest = AssignmentRequest(
            Guid.NewGuid(),
            Key(),
            MockIdentityProfiles.ActiveDispatcher,
            driverId: DispatchHttpWebApplicationFactory.IneligibleDriverId);
        using var ineligible = await client.SendAsync(ineligibleRequest);
        var ineligibleBody = await ineligible.Content.ReadAsStringAsync();
        Assert.Contains("DRIVER_INELIGIBLE", ineligibleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DOCUMENT_", ineligibleBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GET_requires_driver_profile_and_returns_exact_minimized_stops()
    {
        using var anonymous = new HttpRequestMessage(HttpMethod.Get, "/api/v1/driver/me/stops");
        anonymous.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        using var anonymousResponse = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var viewer = StopRequest(MockIdentityProfiles.ActiveViewer);
        using var viewerResponse = await client.SendAsync(viewer);
        Assert.Equal(HttpStatusCode.Forbidden, viewerResponse.StatusCode);

        factory.SetStops(
        [
            new DriverStopResult("ORD_SYNTHETIC", "DELIVERY", "IN_TRANSIT", "Synthetic summary"),
        ]);
        using var driver = StopRequest(MockIdentityProfiles.ActiveDriver);
        using var driverResponse = await client.SendAsync(driver);
        Assert.Equal(HttpStatusCode.OK, driverResponse.StatusCode);
        var body = await driverResponse.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var stop = Assert.Single(json.RootElement.EnumerateArray().ToArray());
        Assert.Equal(
            ["address_summary", "order_public_id", "status", "stop_type"],
            stop.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("contact_token", body, StringComparison.Ordinal);
        Assert.DoesNotContain("phone", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cost", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("driver_id", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assignment_id", body, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage AssignmentRequest(
        Guid orderId,
        string? key,
        string? profile,
        bool includeTenant = true,
        Guid? driverId = null,
        long costCents = 0,
        string? body = null) =>
        AssignmentRequest(orderId.ToString("D"), key, profile, includeTenant, driverId, costCents, body);

    private static HttpRequestMessage AssignmentRequest(
        string orderId,
        string? key,
        string? profile,
        bool includeTenant = true,
        Guid? driverId = null,
        long costCents = 0,
        string? body = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/orders/{orderId}/assignments")
        {
            Content = body is null
                ? JsonContent.Create(new
                {
                    driver_id = driverId ?? Guid.Parse("d1000000-0000-0000-0000-000000000001"),
                    assignment_type = "OWN",
                    cost_cents = costCents,
                    route_id = (Guid?)null,
                })
                : new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (profile is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        }

        if (includeTenant)
        {
            request.Headers.Add(
                "X-Organization-Id",
                MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return request;
    }

    private static HttpRequestMessage StopRequest(string profile)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/driver/me/stops");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add(
            "X-Organization-Id",
            MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        return request;
    }

    private static string Key() => $"dispatch-http-{Guid.NewGuid():N}";
}
