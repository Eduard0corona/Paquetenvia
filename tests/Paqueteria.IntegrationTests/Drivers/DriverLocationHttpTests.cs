using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;

namespace Paqueteria.IntegrationTests.Drivers;

public sealed class DriverLocationHttpTests : IClassFixture<DriverLocationHttpWebApplicationFactory>
{
    private readonly DriverLocationHttpWebApplicationFactory factory;
    private readonly HttpClient client;

    public DriverLocationHttpTests(DriverLocationHttpWebApplicationFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task POST_enforces_authentication_tenant_and_driver_capability()
    {
        using (var request = Request(null))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var request = Request("invalid-token"))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var request = Request(MockIdentityProfiles.SuspendedUser))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var request = Request(MockIdentityProfiles.ActiveDriver, includeTenant: false))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        foreach (var profile in new[]
        {
            MockIdentityProfiles.ActiveViewer,
            MockIdentityProfiles.ActiveDispatcher,
            MockIdentityProfiles.SuspendedMembership,
        })
        {
            using var request = Request(profile);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task POST_returns_uniform_not_found_for_inaccessible_profile()
    {
        foreach (var eventId in DriverLocationHttpWebApplicationFactory.InaccessibleProfileEventIds)
        {
            using var request = Request(
                MockIdentityProfiles.ActiveDriver,
                Body(eventId));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("profile", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("OWN", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task POST_forbidden_response_does_not_reveal_whether_event_id_exists()
    {
        var eventId = Guid.NewGuid();
        using (var fresh = Request(MockIdentityProfiles.ActiveViewer, Body(eventId)))
        using (var freshResponse = await client.SendAsync(fresh))
        {
            Assert.Equal(HttpStatusCode.Forbidden, freshResponse.StatusCode);
        }

        using (var accepted = Request(MockIdentityProfiles.ActiveDriver, Body(eventId)))
        using (var acceptedResponse = await client.SendAsync(accepted))
        {
            Assert.Equal(HttpStatusCode.Accepted, acceptedResponse.StatusCode);
        }

        using var existing = Request(MockIdentityProfiles.ActiveViewer, Body(eventId));
        using var existingResponse = await client.SendAsync(existing);
        Assert.Equal(HttpStatusCode.Forbidden, existingResponse.StatusCode);
    }

    [Fact]
    public async Task POST_preserves_order_and_exact_mixed_counts()
    {
        var id = Guid.NewGuid();
        var json = $$"""
            {"positions":[
              {"client_event_id":"{{id:D}}","lat":24.8091,"lng":-107.394,"accuracy_m":8.5,"captured_at":"2026-07-24T20:00:00Z"},
              {"client_event_id":"{{id:D}}","lat":24.8092,"lng":-107.394,"accuracy_m":8.5,"captured_at":"2026-07-24T20:00:01Z"},
              {"client_event_id":"{{Guid.NewGuid():D}}","lat":91,"lng":-107.394,"accuracy_m":8.5,"captured_at":"2026-07-24T20:00:02Z"}
            ]}
            """;
        using var request = Request(MockIdentityProfiles.ActiveDriver, json);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, document.RootElement.GetProperty("accepted_count").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("duplicate_count").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("rejected_count").GetInt32());
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(["ACCEPTED", "DUPLICATE", "REJECTED"],
            items.Select(item => item.GetProperty("status").GetString()));
        Assert.Equal(
            items[0].GetProperty("position_id").GetGuid(),
            items[1].GetProperty("position_id").GetGuid());
        Assert.Equal("INVALID_COORDINATES", items[2].GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task POST_rejects_non_iso_timestamp_per_item_without_substitution()
    {
        var body =
            $$"""{"positions":[{"client_event_id":"{{Guid.NewGuid():D}}","lat":24.8091,"lng":-107.394,"accuracy_m":8.5,"captured_at":"07/24/2026 20:00:00"}]}""";
        using var request = Request(MockIdentityProfiles.ActiveDriver, body);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, document.RootElement.GetProperty("rejected_count").GetInt32());
        Assert.Equal(
            "INVALID_CAPTURED_AT",
            document.RootElement.GetProperty("items")[0].GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task POST_accepts_batches_of_one_and_twenty_but_rejects_structural_shape()
    {
        using (var one = Request(MockIdentityProfiles.ActiveDriver))
        using (var response = await client.SendAsync(one))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        var items = Enumerable.Range(0, 20)
            .Select(index => Point(Guid.NewGuid(), 24.8 + index * 0.00001))
            .ToArray();
        using (var twenty = Request(
                   MockIdentityProfiles.ActiveDriver,
                   $$"""{"positions":[{{string.Join(',', items)}}]}"""))
        using (var response = await client.SendAsync(twenty))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        var before = factory.Invocations;
        foreach (var invalid in new[]
        {
            """{"positions":[]}""",
            $$"""{"positions":[{{string.Join(',', Enumerable.Range(0, 21).Select(_ => Point(Guid.NewGuid(), 24.8)))}}]}""",
            """{"positions":[],"driver_id":"00000000-0000-0000-0000-000000000001"}""",
            "{invalid",
        })
        {
            using var request = Request(MockIdentityProfiles.ActiveDriver, invalid);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        Assert.Equal(before, factory.Invocations);
    }

    [Fact]
    public async Task POST_provider_disabled_is_fail_closed()
    {
        await using var disabled = new DisabledDriverLocationWebApplicationFactory();
        using var disabledClient = disabled.CreateClient();
        using var request = Request(MockIdentityProfiles.ActiveDriver);
        using var response = await disabledClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task POST_has_identity_partitioned_batch_rate_limit_without_queue()
    {
        await using var limited = new DriverLocationHttpWebApplicationFactory(1);
        using var limitedClient = limited.CreateClient();
        using var firstRequest = Request(MockIdentityProfiles.ActiveDriver);
        using var first = await limitedClient.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using var secondRequest = Request(MockIdentityProfiles.ActiveDriver);
        using var second = await limitedClient.SendAsync(secondRequest);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    private static HttpRequestMessage Request(
        string? profile,
        string? body = null,
        bool includeTenant = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/driver/me/location-updates")
        {
            Content = new StringContent(
                body ?? Body(Guid.NewGuid()),
                Encoding.UTF8,
                "application/json"),
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
        return request;
    }

    private static string Body(Guid id) => $$"""{"positions":[{{Point(id, 24.8091)}}]}""";

    private static string Point(Guid id, double latitude) =>
        $$"""{"client_event_id":"{{id:D}}","lat":{{latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"lng":-107.394,"accuracy_m":8.5,"captured_at":"2026-07-24T20:00:00Z"}""";
}
