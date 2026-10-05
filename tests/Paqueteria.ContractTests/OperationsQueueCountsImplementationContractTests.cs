using System.Reflection;
using System.Text.Json.Serialization;
using Paqueteria.ContractTests.Support;
using Reporting.Application.Operations;
using Reporting.Endpoints;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// UI-PHASE2-QUEUE-COUNTS-2026-10-05: the implementation of getOperationsQueueCounts matches its AI-05 declaration -
/// route, roles, responses and the exact integer-only representation - and the reader stays a read-only, RLS-scoped
/// single aggregate in one tenant transaction.
/// </summary>
public sealed class OperationsQueueCountsImplementationContractTests
{
    private static readonly YamlMappingNode Contract =
        YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));

    [Fact]
    public void Endpoint_metadata_matches_the_AI05_operation()
    {
        var source = Read("src", "Modules", "Reporting", "Reporting.Endpoints", "OperationsQueueCountsEndpoints.cs");
        Assert.Equal("/api/v1/operations/queue-counts", OperationsQueueCountsEndpoints.Route);
        Assert.Contains("endpoints.MapGet(Route, GetAsync)", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"getOperationsQueueCounts\")", source, StringComparison.Ordinal);
        Assert.Contains(".RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)", source, StringComparison.Ordinal);
        Assert.Contains(".RequireTenantContext(StatusCodes.Status403Forbidden)", source, StringComparison.Ordinal);
        Assert.Contains("TenantCapabilities.GetOperationsQueueCounts", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPost(", source, StringComparison.Ordinal);

        // Shape (any query string) is decided before the capability, and the capability before the reader.
        var shape = source.IndexOf("Request.Query.Count", StringComparison.Ordinal);
        var capability = source.IndexOf("TenantCapabilityGate.Deny(", StringComparison.Ordinal);
        var read = source.IndexOf("reader.ReadAsync(", StringComparison.Ordinal);
        Assert.True(shape > 0 && shape < capability && capability < read);

        var operation = Contract.Mapping("paths").Mapping("/operations/queue-counts").Mapping("get");
        Assert.Equal("getOperationsQueueCounts", operation.Scalar("operationId"));
        Assert.Equal(
            "shape-validation-then-capability-before-persisted-state",
            operation.Scalar("x-authorization-precedence"));
        Assert.StartsWith("UI-PHASE2-QUEUE-COUNTS-2026-10-05", operation.Scalar("description"), StringComparison.Ordinal);
        Assert.Equal(
            ["#/components/parameters/OrganizationContext"],
            operation.Sequence("parameters").Children.Cast<YamlMappingNode>().Select(node => node.Scalar("$ref")));
        Assert.Equal(
            ["200", "400", "401", "403", "503"],
            operation.Mapping("responses").Children.Keys.Select(key => ((YamlScalarNode)key).Value));
        Assert.Equal(
            "#/components/schemas/OperationsQueueCounts",
            operation.Mapping("responses").Mapping("200").Mapping("content").Mapping("application/json")
                .Mapping("schema").Scalar("$ref"));
    }

    [Fact]
    public void Response_DTOs_are_the_AI05_schemas_and_carry_counts_only()
    {
        var schemas = Contract.Mapping("components").Mapping("schemas");
        var counts = schemas.Mapping("OperationsQueueCounts");
        Assert.Equal("false", counts.Scalar("additionalProperties"));
        Assert.Equal(Required(counts), JsonNames<OperationsQueueCountsResponse>());

        var queues = schemas.Mapping("OperationsQueues");
        Assert.Equal("false", queues.Scalar("additionalProperties"));
        Assert.Equal(Required(queues), JsonNames<OperationsQueuesResponse>());
        Assert.All(
            typeof(OperationsQueuesResponse).GetProperties(),
            property => Assert.Equal(typeof(long), property.PropertyType));

        // by_status: exactly the 17 AI-04 statuses in AI-04 order, each a non-negative int64 count.
        var byStatus = counts.Mapping("properties").Mapping("by_status");
        Assert.Equal("false", byStatus.Scalar("additionalProperties"));
        Assert.Equal(OperationsDashboardVocabulary.Statuses, Required(byStatus));
        var orderStatus = schemas.Mapping("OrderStatus").Sequence("enum").Children
            .Select(node => ((YamlScalarNode)node).Value!);
        Assert.Equal(orderStatus, Required(byStatus));
        var count = schemas.Mapping("OperationsCount");
        Assert.Equal("integer", count.Scalar("type"));
        Assert.Equal("int64", count.Scalar("format"));
        Assert.Equal("0", count.Scalar("minimum"));

        // Integer counts and one UTC timestamp: no identifier, name or amount can be serialized.
        Assert.Equal(
            [typeof(DateTimeOffset), typeof(long), typeof(IReadOnlyDictionary<string, long>), typeof(OperationsQueuesResponse)],
            typeof(OperationsQueueCountsResponse).GetProperties().Select(property => property.PropertyType));
    }

    [Fact]
    public void PostgreSql_reader_is_one_read_only_tenant_transaction_with_a_single_aggregate()
    {
        var source = Read(
            "src", "Modules", "Reporting", "Reporting.Infrastructure", "Operations",
            "PostgreSqlOperationsQueueCountsReader.cs");
        Assert.Equal(2, Count(source, "new NpgsqlCommand("));
        Assert.Equal(1, Count(source, "BeginTransactionAsync"));
        var begin = source.IndexOf("BeginTransactionAsync", StringComparison.Ordinal);
        Assert.True(begin < source.IndexOf("AuthorizeAsync(connection, transaction", StringComparison.Ordinal));
        Assert.Contains(
            "set_config('app.current_org_ids', @organization_ids::uuid[]::text, true)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("SET LOCAL ROLE paqueteria_app", source, StringComparison.Ordinal);
        Assert.Contains("OperationsRolePolicy.IsAllowed(role.Value, request.MfaSatisfied)", source, StringComparison.Ordinal);
        Assert.Contains("GROUP BY order_row.status", source, StringComparison.Ordinal);
        Assert.Equal(1, Count(source, "FROM orders.orders"));
        Assert.DoesNotContain("SECURITY DEFINER", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BYPASSRLS", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FOR UPDATE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner_org_id =", source, StringComparison.Ordinal);

        // The two queues computed in SQL are exactly the dashboard's alert and cost-warning predicates.
        var dashboard = Read(
            "src", "Modules", "Reporting", "Reporting.Infrastructure", "Operations",
            "PostgreSqlOperationsDashboardReader.cs");
        foreach (var fragment in new[]
                 {
                     "order_row.status IN ('READY_FOR_PICKUP', 'RESCHEDULED')",
                     "candidate.status IN ('ACCEPTED', 'ACTIVE')",
                     "minimum_total_cents_snapshot",
                     "financial_override ?& ARRAY['actor_id', 'reason', 'valid_until']",
                 })
        {
            Assert.Contains(fragment, source, StringComparison.Ordinal);
            Assert.Contains(fragment, dashboard, StringComparison.Ordinal);
        }
    }

    private static string[] Required(YamlMappingNode schema) =>
        schema.Sequence("required").Children.Select(node => ((YamlScalarNode)node).Value!).ToArray();

    private static string[] JsonNames<T>() => typeof(T)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name)
        .ToArray();

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryPaths.Root, .. segments]));

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;
}
