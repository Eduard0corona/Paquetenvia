using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dispatch.Application.Assignments;
using Identity.Infrastructure.Mock;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application.Orders;

namespace Paqueteria.IntegrationTests.Orders;

/// <summary>
/// ORD-002-API-GUARD-REGISTRY end to end: the real API (<c>Program</c>, mock authentication, PostgreSQL identity,
/// tenancy, Orders, Drivers and Dispatch providers on the runtime application role) over a real PostgreSQL baseline.
/// Until the fix the API composed the AI-04 guard registry without a single guard, so "Cerrar orden" closed a
/// DELIVERED order with an open incident and a DELIVERING order became DELIVERED without its delivery proof. Orders,
/// incidents, drivers and packages are seeded directly; every transition and assignment goes through HTTP.
/// </summary>
public sealed class OrderTransitionGuardsPostgreSqlHttpTests(OrderTransitionGuardsHttpFixture fixture)
    : IClassFixture<OrderTransitionGuardsHttpFixture>
{
    private readonly HttpClient client = fixture.Api.CreateClient();

    [Fact]
    public async Task Closing_a_delivered_order_with_an_unresolved_incident_is_409_UNRESOLVED_INCIDENT_until_it_is_resolved()
    {
        var orderId = await fixture.SeedOrderAsync("DELIVERED", version: 8, claimWindowSet: true);
        var incidentId = await fixture.SeedOpenIncidentAsync(orderId);
        var before = await fixture.ReadOrderAsync(orderId);
        var refusedKey = NewKey();

        using (var refused = await client.SendAsync(Transition(orderId, "CLOSED", 8, refusedKey)))
        {
            await AssertCodedConflictAsync(refused, "UNRESOLVED_INCIDENT");
        }

        // A refused transition writes nothing: no status, version, event, outbox, audit or idempotency row.
        Assert.Equal(before, await fixture.ReadOrderAsync(orderId));
        Assert.Equal(("DELIVERED", 8), (before.Status, before.Version));
        Assert.Equal(0, await fixture.CountTransitionReservationsAsync(refusedKey));

        // Resolving the incident through the API is all the close was missing: the guard read the live incident.
        using (var resolution = await client.SendAsync(ResolveIncident(incidentId)))
        {
            Assert.Equal(HttpStatusCode.OK, resolution.StatusCode);
        }

        using var closed = await client.SendAsync(Transition(orderId, "CLOSED", 8, NewKey()));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        using (var body = JsonDocument.Parse(await closed.Content.ReadAsStringAsync()))
        {
            Assert.Equal("CLOSED", body.RootElement.GetProperty("status").GetString());
            Assert.Equal(9, body.RootElement.GetProperty("version").GetInt32());
        }

        var after = await fixture.ReadOrderAsync(orderId);
        Assert.Equal(("CLOSED", 9), (after.Status, after.Version));
        Assert.Equal(before.StatusEvents + 1, after.StatusEvents);
    }

    [Fact]
    public async Task Delivering_without_a_delivery_proof_is_409_DELIVERY_PROOF_REQUIRED()
    {
        var orderId = await fixture.SeedOrderAsync("DELIVERING", version: 7, claimWindowSet: false);
        var before = await fixture.ReadOrderAsync(orderId);
        var key = NewKey();

        using var refused = await client.SendAsync(Transition(orderId, "DELIVERED", 7, key));

        await AssertCodedConflictAsync(refused, "DELIVERY_PROOF_REQUIRED");
        Assert.Equal(before, await fixture.ReadOrderAsync(orderId));
        Assert.Equal(0, await fixture.CountTransitionReservationsAsync(key));
    }

    /// <summary>
    /// The own-driver assignment (AI-13 flow assignment -> order) evaluates the registry's ASSIGNED guards inside its
    /// transaction. It feeds them the snapshot of its own eligibility, capacity and cost checks, so a request can
    /// only show that the full registry admits a valid assignment; that the coordinator serving this host holds the
    /// full registry is asserted on the host itself.
    /// </summary>
    [Fact]
    public async Task An_own_driver_assignment_runs_under_every_AI04_guard_and_moves_the_order_to_ASSIGNED()
    {
        var registry = fixture.Api.Services.GetRequiredService<OrderTransitionGuardRegistry>();
        Assert.Equal(
            new OrderTransitionGuardRegistry().Guards.Select(guard => guard.Code),
            registry.Guards.Select(guard => guard.Code));
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var coordinator = scope.ServiceProvider.GetRequiredService<IAssignmentService>();
            Assert.Equal("PostgreSqlAssignmentToOrderCoordinator", coordinator.GetType().Name);
            Assert.Same(registry, HeldRegistry(coordinator));
        }

        var orderId = await fixture.SeedOrderAsync("READY_FOR_PICKUP", version: 3, claimWindowSet: false);
        await fixture.SeedPackageAsync(orderId);
        var driverId = await fixture.SeedEligibleDriverAsync();
        var before = await fixture.ReadOrderAsync(orderId);

        using var response = await client.SendAsync(Assign(orderId, driverId));

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Expected 201, got {(int)response.StatusCode}: {responseBody}");
        var after = await fixture.ReadOrderAsync(orderId);
        Assert.Equal(("ASSIGNED", 4), (after.Status, after.Version));
        Assert.Equal(before.StatusEvents + 1, after.StatusEvents);
        Assert.Equal(1, await fixture.CountActiveAssignmentsAsync(orderId, driverId));
    }

    private static HttpRequestMessage Transition(Guid orderId, string target, int expectedVersion, string key) =>
        Authorized(
            $"/api/v1/orders/{orderId:D}/transitions",
            key,
            JsonSerializer.Serialize(new
            {
                target_status = target,
                reason = "Cierre operativo sintético ORD-002.",
                expected_version = expectedVersion,
                metadata = new { },
            }));

    private static HttpRequestMessage ResolveIncident(Guid incidentId) =>
        Authorized(
            $"/api/v1/incidents/{incidentId:D}/resolution",
            NewKey(),
            JsonSerializer.Serialize(new
            {
                outcome = "RESOLVED",
                reason = "El destinatario confirmó la recepción del paquete.",
            }));

    private static HttpRequestMessage Assign(Guid orderId, Guid driverId) =>
        Authorized(
            $"/api/v1/orders/{orderId:D}/assignments",
            NewKey(),
            JsonSerializer.Serialize(new
            {
                driver_id = driverId,
                assignment_type = "OWN",
                cost_cents = 1500L,
                route_id = (Guid?)null,
            }));

    private static HttpRequestMessage Authorized(string path, string key, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveDispatcher);
        request.Headers.Add("X-Organization-Id", OrderTransitionGuardsHttpFixture.TenantId.ToString("D"));
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static async Task AssertCodedConflictAsync(HttpResponseMessage response, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Conflict,
            $"Expected 409 {code}, got {(int)response.StatusCode}: {text}");
        Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.ToString());
        using var body = JsonDocument.Parse(text);
        Assert.Equal(409, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        // AI-05 TransitionConflictProblem: the rule code is the only addition, never the guard evidence.
        Assert.Equal(
            ["code", "status", "title", "traceId", "type"],
            body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    private static string NewKey() => $"ord002-guards-{Guid.NewGuid():N}";

    /// <summary>The registry a composed service captured from its constructor.</summary>
    private static OrderTransitionGuardRegistry HeldRegistry(object service)
    {
        var field = Assert.Single(
            service.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            candidate => candidate.FieldType == typeof(OrderTransitionGuardRegistry));
        return Assert.IsType<OrderTransitionGuardRegistry>(field.GetValue(service));
    }
}
