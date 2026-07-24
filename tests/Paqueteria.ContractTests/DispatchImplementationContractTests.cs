using System.Reflection;
using System.Text.Json.Serialization;
using Dispatch.Domain;
using Dispatch.Endpoints;
using Dispatch.Infrastructure.Assignments;
using Dispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Paqueteria.ContractTests.Support;
using Paqueteria.Infrastructure.Tenancy;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

public sealed class DispatchImplementationContractTests
{
    [Fact]
    public void Implementation_exposes_exactly_the_two_DSP002_routes()
    {
        var path = Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Dispatch", "Dispatch.Endpoints", "DispatchEndpoints.cs");
        var source = File.ReadAllText(path);

        Assert.Equal(1, Count(source, "endpoints.MapPost("));
        Assert.Equal(1, Count(source, "endpoints.MapGet("));
        Assert.Contains("MapPost(\"/api/v1/orders/{orderId}/assignments\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/driver/me/stops\"", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"assignDriver\")", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"listMyStops\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPut(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPatch(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_assignment_and_stop_DTOs_match_AI05_and_expose_no_PII()
    {
        AssertJsonProperties<CreateAssignmentRequest>(
            "assignment_type", "cost_cents", "driver_id", "route_id");
        AssertJsonProperties<AssignmentResponse>("cost", "driver_id", "id", "order_id", "status");
        AssertJsonProperties<MoneyResponse>("amount_cents", "currency");
        AssertJsonProperties<DriverStopResponse>(
            "address_summary", "order_public_id", "status", "stop_type");

        var stopNames = typeof(DriverStopResponse).GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(stopNames, name =>
            name.Contains("Phone", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Contact", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Cost", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("DriverId", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("AssignmentId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AI05_assignment_and_driver_stop_shapes_are_implemented_structurally()
    {
        var root = YamlNodes.LoadMapping(
            RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var schemas = root.Mapping("components").Mapping("schemas");
        var request = schemas.Mapping("CreateAssignmentRequest");
        var assignment = schemas.Mapping("Assignment");
        var money = schemas.Mapping("Money");
        var stop = schemas.Mapping("DriverStop");

        Assert.Equal(
            ["assignment_type", "cost_cents", "driver_id"],
            RequiredPropertyNames(request));
        Assert.Equal(
            ["cost", "driver_id", "id", "order_id", "status"],
            RequiredPropertyNames(assignment));
        Assert.Equal(["amount_cents", "currency"], RequiredPropertyNames(money));
        Assert.Equal(
            ["address_summary", "order_public_id", "status", "stop_type"],
            RequiredPropertyNames(stop));

        Assert.Equal(
            ["OWN", "EXTERNAL", "ALLY_CAPACITY"],
            request.Mapping("properties").Mapping("assignment_type").Sequence("enum")
                .Children.Cast<YamlScalarNode>().Select(value => value.Value));
        Assert.Equal(
            ["PICKUP", "DELIVERY", "RETURN"],
            stop.Mapping("properties").Mapping("stop_type").Sequence("enum")
                .Children.Cast<YamlScalarNode>().Select(value => value.Value));
        Assert.Equal("int64", request.Mapping("properties").Mapping("cost_cents").Scalar("format"));
        Assert.Equal("MXN", money.Mapping("properties").Mapping("currency").Scalar("const"));
    }

    [Fact]
    public void Ef_model_maps_only_dispatch_assignments_with_the_exact_columns_and_partial_index()
    {
        var options = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql("Host=localhost;Database=model;Username=model;Password=model")
            .Options;
        using var context = new DispatchDbContext(options, new TenantDatabaseExecutionState());
        var entity = Assert.Single(context.Model.GetEntityTypes());
        Assert.Equal("dispatch", entity.GetSchema());
        Assert.Equal("assignments", entity.GetTableName());
        var table = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(
            "assignments",
            "dispatch");
        Assert.Equal(
            [
                "accepted_at", "assignment_type", "cost_cents", "created_at", "driver_id", "id",
                "operator_org_id", "order_id", "owner_org_id", "route_id", "status",
            ],
            entity.GetProperties()
                .Select(property => property.GetColumnName(table))
                .Order(StringComparer.Ordinal));
        var index = Assert.Single(entity.GetIndexes());
        Assert.True(index.IsUnique);
        Assert.Equal("one_active_assignment_per_order", index.GetDatabaseName());
        Assert.Equal("status IN ('ACCEPTED','ACTIVE')", index.GetFilter());
    }

    [Fact]
    public void Adoption_migration_is_single_non_destructive_and_uses_its_own_history()
    {
        var migrationDirectory = Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Dispatch", "Dispatch.Infrastructure", "Persistence", "Migrations");
        var source = File.ReadAllText(Assert.Single(
            Directory.GetFiles(migrationDirectory, "*AdoptCanonicalDispatchAssignmentsBaseline.cs")));

        Assert.Contains("20260723_AdoptCanonicalDispatchAssignmentsBaseline", source, StringComparison.Ordinal);
        Assert.Contains("dispatch.assignments", source, StringComparison.Ordinal);
        Assert.Contains("one_active_assignment_per_order", source, StringComparison.Ordinal);
        Assert.Contains("assignments_tenant", source, StringComparison.Ordinal);
        foreach (var destructive in new[]
        {
            "CREATE TABLE",
            "DROP TABLE",
            "ADD COLUMN",
            "DROP COLUMN",
            "DROP CONSTRAINT",
            "DISABLE ROW LEVEL SECURITY",
            "DROP POLICY",
        })
        {
            Assert.DoesNotContain(destructive, source, StringComparison.OrdinalIgnoreCase);
        }

        var dependencyInjection = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Dispatch", "Dispatch.Infrastructure", "DependencyInjection.cs"));
        Assert.Contains("__ef_migrations_history_dispatch", dependencyInjection, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_has_exact_scope_flow_and_no_returning_or_extra_assignment_topic()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src", "Modules", "Dispatch", "Dispatch.Infrastructure",
            "Assignments", "PostgreSqlAssignmentToOrderCoordinator.cs"));

        Assert.Equal("DSP-002:ASSIGN_OWN_DRIVER", PostgreSqlAssignmentToOrderCoordinator.IdempotencyScope);
        Assert.Equal("assignment_to_order_status_event", PostgreSqlAssignmentToOrderCoordinator.CoordinationFlow);
        Assert.DoesNotContain("RETURNING", source, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Count(source, "\"orders.status-changed\""));
        Assert.DoesNotContain("dispatch.assignment-created", source, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertJsonProperties<T>(params string[] expected)
    {
        var actual = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static string[] RequiredPropertyNames(YamlMappingNode schema) =>
        schema.Sequence("required").Children
            .Select(node => Assert.IsType<YamlScalarNode>(node).Value!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;
}
