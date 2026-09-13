using System.Reflection;
using System.Text.Json.Serialization;
using Paqueteria.ContractTests.Support;
using Routing.Endpoints;

namespace Paqueteria.ContractTests;

public sealed class RoutingImplementationContractTests
{
    [Fact]
    public void Six_normative_route_operations_are_mapped_without_private_surface()
    {
        var root = RepositoryPaths.Root;
        var source = File.ReadAllText(Path.Combine(root,
            "src", "Modules", "Routing", "Routing.Endpoints", "RouteEndpoints.cs"));
        foreach (var operation in new[]
        {
            "createRoute", "listRoutes", "getRoute", "addRouteStop", "removeRouteStop", "reorderRouteStops",
        })
            Assert.Contains($".WithName(\"{operation}\")", source, StringComparison.Ordinal);
        Assert.Equal(2, Count(source, "MapPost("));
        Assert.Equal(2, Count(source, "MapGet("));
        Assert.Equal(1, Count(source, "MapDelete("));
        Assert.Equal(1, Count(source, "MapPut("));
        Assert.DoesNotContain("MapPatch(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_DTOs_match_the_exact_AI05_surface()
    {
        AssertProperties<CreateRouteRequest>("city_id", "driver_id", "scheduled_for", "service_area_id");
        AssertProperties<AddRouteStopRequest>("expected_version", "order_id");
        AssertProperties<ReorderRouteStopsRequest>("expected_version", "stop_ids");
        AssertProperties<RouteResponse>("assignment_cost_cents_total", "city_id", "driver_id", "id",
            "scheduled_for", "service_area_id", "status", "stop_count", "version");
        AssertProperties<RouteDetailResponse>("assignment_cost_cents_total", "city_id", "driver_id", "id",
            "scheduled_for", "service_area_id", "status", "stop_count", "stops", "version");
        AssertProperties<RouteStopResponse>("id", "order_id", "sequence", "status", "stop_type");
        AssertProperties<RoutePageResponse>("items", "next_cursor");
    }

    [Fact]
    public void Routing_implementation_keeps_REST_authority_and_transactional_outbox()
    {
        var root = RepositoryPaths.Root;
        var service = File.ReadAllText(Path.Combine(root,
            "src", "Modules", "Routing", "Routing.Infrastructure", "Routes", "PostgreSqlRouteService.cs"));
        var persistence = File.ReadAllText(Path.Combine(root,
            "src", "Modules", "Routing", "Routing.Infrastructure", "Routes", "PostgreSqlRouteService.Persistence.cs"));
        Assert.Contains("pg_advisory_xact_lock", persistence, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", persistence, StringComparison.Ordinal);
        Assert.Contains("routes.route-changed", service, StringComparison.Ordinal);
        Assert.Contains("platform.outbox_events", persistence, StringComparison.Ordinal);
        Assert.Contains("dispatch.assignments", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("EXTERNAL'", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ALLY_CAPACITY", service, StringComparison.Ordinal);
    }

    private static void AssertProperties<T>(params string[] expected)
    {
        var actual = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;
}
