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
        $$"""{"driver_id":"{{Guid.NewGuid():D}}","assignment_type":"OWN","cost_cents":0,"route_id":42}""",
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
    public async Task POST_accepts_route_id_absent_or_null_and_rejects_non_null_or_invalid_values()
    {
        var driverId = Guid.NewGuid();
        var absentBody =
            $$"""{"driver_id":"{{driverId:D}}","assignment_type":"OWN","cost_cents":0}""";
        using var absentRequest = AssignmentRequest(
            Guid.NewGuid(),
            Key(),
            MockIdentityProfiles.ActiveDispatcher,
            body: absentBody);
        using var absent = await client.SendAsync(absentRequest);
        Assert.Equal(HttpStatusCode.Created, absent.StatusCode);

        var nullBody =
            $$"""{"driver_id":"{{driverId:D}}","assignment_type":"OWN","cost_cents":0,"route_id":null}""";
        using var nullRequest = AssignmentRequest(
            Guid.NewGuid(),
            Key(),
            MockIdentityProfiles.ActiveDispatcher,
            body: nullBody);
        using var explicitNull = await client.SendAsync(nullRequest);
        Assert.Equal(HttpStatusCode.Created, explicitNull.StatusCode);

        foreach (var rejectedBody in new[]
        {
            $$"""{"driver_id":"{{driverId:D}}","assignment_type":"OWN","cost_cents":0,"route_id":"{{Guid.NewGuid():D}}"}""",
            $$"""{"driver_id":"{{driverId:D}}","assignment_type":"OWN","cost_cents":0,"route_id":false}""",
        })
        {
            using var rejectedRequest = AssignmentRequest(
                Guid.NewGuid(),
                Key(),
                MockIdentityProfiles.ActiveDispatcher,
                body: rejectedBody);
            using var rejected = await client.SendAsync(rejectedRequest);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Contains("INVALID_REQUEST", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task POST_uses_uniform_404_for_missing_or_cross_tenant_resources()
    {
        var before = factory.Effects;
        var cases = new[]
        {
            (DispatchHttpWebApplicationFactory.MissingOrderId, Guid.NewGuid()),
            (DispatchHttpWebApplicationFactory.CrossTenantOrderId, Guid.NewGuid()),
            (Guid.NewGuid(), DispatchHttpWebApplicationFactory.MissingDriverId),
            (Guid.NewGuid(), DispatchHttpWebApplicationFactory.CrossTenantDriverId),
        };
        var bodies = new List<string>();
        var mediaTypes = new List<string?>();

        foreach (var (orderId, driverId) in cases)
        {
            using var request = AssignmentRequest(
                orderId,
                Key(),
                MockIdentityProfiles.ActiveDispatcher,
                driverId: driverId);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            mediaTypes.Add(response.Content.Headers.ContentType?.MediaType);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.All(mediaTypes, value => Assert.Equal(mediaTypes[0], value));
        Assert.All(bodies, value => Assert.Equal(bodies[0].Length, value.Length));
        Assert.Equal(before, factory.Effects);
        using var problem = JsonDocument.Parse(bodies[0]);
        Assert.Equal(
            ["status", "title", "traceId", "type"],
            problem.RootElement.EnumerateObject()
                .Select(value => value.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal("Not Found.", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
        foreach (var body in bodies)
        {
            using var current = JsonDocument.Parse(body);
            Assert.Equal(
                problem.RootElement.GetProperty("type").GetString(),
                current.RootElement.GetProperty("type").GetString());
            Assert.Equal(
                problem.RootElement.GetProperty("title").GetString(),
                current.RootElement.GetProperty("title").GetString());
            Assert.Equal(
                problem.RootElement.GetProperty("status").GetInt32(),
                current.RootElement.GetProperty("status").GetInt32());
            Assert.False(current.RootElement.TryGetProperty("code", out _));
            Assert.False(current.RootElement.TryGetProperty("detail", out _));
            Assert.DoesNotContain("order", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("driver", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tenant", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("d2000000", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task POST_applies_explicit_capability_first_precedence()
    {
        foreach (var orderId in new[]
        {
            Guid.NewGuid(),
            DispatchHttpWebApplicationFactory.MissingOrderId,
        })
        {
            using var anonymous = AssignmentRequest(orderId, Key(), null);
            using var response = await client.SendAsync(anonymous);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        foreach (var profile in new[]
        {
            MockIdentityProfiles.ActiveViewer,
            MockIdentityProfiles.ActiveDriver,
            MockIdentityProfiles.ActivePlatformAdminNoMfa,
        })
        {
            foreach (var (orderId, driverId) in new[]
            {
                (Guid.NewGuid(), Guid.NewGuid()),
                (DispatchHttpWebApplicationFactory.MissingOrderId, Guid.NewGuid()),
                (Guid.NewGuid(), DispatchHttpWebApplicationFactory.MissingDriverId),
            })
            {
                using var forbidden = AssignmentRequest(
                    orderId,
                    Key(),
                    profile,
                    driverId: driverId);
                using var response = await client.SendAsync(forbidden);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }

        foreach (var (orderId, driverId) in new[]
        {
            (DispatchHttpWebApplicationFactory.MissingOrderId, Guid.NewGuid()),
            (Guid.NewGuid(), DispatchHttpWebApplicationFactory.MissingDriverId),
        })
        {
            using var admin = AssignmentRequest(
                orderId,
                Key(),
                MockIdentityProfiles.ActivePlatformAdminMfa,
                driverId: driverId);
            using var response = await client.SendAsync(admin);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var conflict = AssignmentRequest(
            DispatchHttpWebApplicationFactory.ConflictOrderId,
            Key(),
            MockIdentityProfiles.ActiveDispatcher);
        using var conflictResponse = await client.SendAsync(conflict);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        Assert.Contains("CONFLICT", await conflictResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveViewer)]
    [InlineData(MockIdentityProfiles.ActiveDriver)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa)]
    public async Task POST_valid_request_denies_non_capability_regardless_of_idempotency_state(
        string profile)
    {
        var beforeEffects = factory.Effects;
        var orderId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var cases = new (IdempotencyStubState? State, bool MatchingSignature)[]
        {
            (null, true),
            (IdempotencyStubState.Completed, true),
            (IdempotencyStubState.Completed, false),
            (IdempotencyStubState.Incomplete, true),
        };
        var bodies = new List<string>();

        foreach (var (state, matchingSignature) in cases)
        {
            var key = Key();
            if (state is { } storedState)
            {
                factory.SeedIdempotency(
                    MockIdentityProfiles.ViewerOrganizationId,
                    key,
                    orderId,
                    driverId,
                    0,
                    storedState,
                    matchingSignature);
            }

            using var request = AssignmentRequest(
                orderId,
                key,
                profile,
                driverId: driverId);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(beforeEffects, factory.Effects);
        Assert.All(bodies, body =>
        {
            using var problem = JsonDocument.Parse(body);
            Assert.Equal(
                ["status", "title", "traceId", "type"],
                problem.RootElement.EnumerateObject()
                    .Select(value => value.Name)
                    .Order(StringComparer.Ordinal));
            Assert.Equal("Forbidden.", problem.RootElement.GetProperty("title").GetString());
            Assert.Equal(403, problem.RootElement.GetProperty("status").GetInt32());
            Assert.DoesNotContain("idempotency", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hash", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("completed", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("incomplete", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(orderId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(driverId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        });
        Assert.All(bodies, body => Assert.Equal(bodies[0].Length, body.Length));
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDispatcher)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa)]
    public async Task POST_authorized_actor_preserves_idempotency_results(string profile)
    {
        var orderId = Guid.NewGuid();
        var driverId = Guid.NewGuid();

        var replayKey = Key();
        factory.SeedIdempotency(
            MockIdentityProfiles.ViewerOrganizationId,
            replayKey,
            orderId,
            driverId,
            0,
            IdempotencyStubState.Completed);
        using (var replayRequest = AssignmentRequest(
                   orderId,
                   replayKey,
                   profile,
                   driverId: driverId))
        using (var replay = await client.SendAsync(replayRequest))
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        }

        foreach (var (state, matchingSignature) in new[]
        {
            (IdempotencyStubState.Completed, false),
            (IdempotencyStubState.Incomplete, true),
            (IdempotencyStubState.InconsistentEvidence, true),
        })
        {
            var key = Key();
            factory.SeedIdempotency(
                MockIdentityProfiles.ViewerOrganizationId,
                key,
                orderId,
                driverId,
                0,
                state,
                matchingSignature);
            using var request = AssignmentRequest(
                orderId,
                key,
                profile,
                driverId: driverId);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains(
                "CONFLICT",
                await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }

        using var createRequest = AssignmentRequest(
            Guid.NewGuid(),
            Key(),
            profile,
            driverId: Guid.NewGuid());
        using var created = await client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task POST_invalid_shape_precedes_capability_without_invoking_product_service()
    {
        var beforeInvocations = factory.Invocations;

        using var invalidBody = AssignmentRequest(
            Guid.NewGuid(),
            Key(),
            MockIdentityProfiles.ActiveViewer,
            body: "{invalid-json");
        using var invalidBodyResponse = await client.SendAsync(invalidBody);
        Assert.Equal(HttpStatusCode.Conflict, invalidBodyResponse.StatusCode);
        Assert.Contains(
            "INVALID_REQUEST",
            await invalidBodyResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var invalidKey = AssignmentRequest(
            Guid.NewGuid(),
            "short",
            MockIdentityProfiles.ActiveViewer);
        using var invalidKeyResponse = await client.SendAsync(invalidKey);
        Assert.Equal(HttpStatusCode.Conflict, invalidKeyResponse.StatusCode);
        Assert.Contains(
            "INVALID_REQUEST",
            await invalidKeyResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Equal(beforeInvocations, factory.Invocations);
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
