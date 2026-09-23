using System.Reflection;
using System.Text.Json.Serialization;
using Finance.Endpoints;
using Finance.Infrastructure.Cod;
using Finance.Infrastructure.Financials;
using Paqueteria.ContractTests.Support;

namespace Paqueteria.ContractTests;

public sealed class FinanceImplementationContractTests
{
    [Fact]
    public void Two_normative_finance_operations_are_mapped_with_mandatory_idempotency()
    {
        var source = Read("src", "Modules", "Finance", "Finance.Endpoints", "CodEndpoints.cs");

        Assert.Contains(
            "MapPost(\"/api/v1/orders/{orderId}/cod-records\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MapPost(\"/api/v1/cod-records/{codId}/reconcile\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(".WithName(\"recordCodCollection\")", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"reconcileCod\")", source, StringComparison.Ordinal);
        Assert.Equal(2, Count(source, "MapPost("));
        Assert.Equal(2, Count(source, "TryReadIdempotencyKey"));
        Assert.DoesNotContain("MapGet(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPut(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPatch(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Additive_financials_surface_is_read_only_and_tenant_scoped()
    {
        var source = Read("src", "Modules", "Finance", "Finance.Endpoints", "OrderFinancialsEndpoints.cs");

        Assert.Contains("MapGet(\"/api/v1/orders/{orderId}/financials\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/routes/{routeId}/financials\"", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"getOrderFinancials\")", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"getRouteFinancials\")", source, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Count(source, ".RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)"));
        Assert.Equal(2, Count(source, ".RequireTenantContext(StatusCodes.Status403Forbidden)"));
        Assert.DoesNotContain("MapPost(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPut(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPatch(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Cod_DTOs_match_the_exact_AI05_surface()
    {
        AssertProperties<RecordCodRequest>("amount_cents", "reference");
        AssertProperties<CodTransactionResponse>(
            "amount_cents", "id", "order_id", "recorded_at", "reconciled_at", "status");
    }

    [Fact]
    public void Financials_DTOs_report_money_as_integer_cents_only()
    {
        AssertProperties<ModalityCostResponse>("assignment_count", "cost_cents", "modality");
        AssertProperties<CodPositionResponse>(
            "amount_cents", "expected_cents", "reconciled", "recorded", "satisfies_close_requirement",
            "satisfies_delivery_requirement", "status");
        AssertProperties<OrderFinancialsResponse>(
            "cod", "cost_by_modality", "cost_cents", "currency", "margin_basis_points", "margin_cents",
            "order_id", "order_status", "revenue_cents");
        AssertProperties<RouteOrderFinancialsResponse>(
            "cod", "cost_by_modality", "cost_cents", "margin_basis_points", "margin_cents", "order_id",
            "revenue_cents");
        AssertProperties<RouteFinancialsResponse>(
            "cod_expected_cents_total", "cod_pending_reconciliation_cents_total",
            "cod_pending_reconciliation_count", "cost_by_modality", "cost_cents_total", "currency",
            "margin_basis_points", "margin_cents_total", "order_count", "orders", "revenue_cents_total",
            "route_id", "route_status");

        foreach (var type in new[]
        {
            typeof(CodTransactionResponse), typeof(ModalityCostResponse), typeof(CodPositionResponse),
            typeof(OrderFinancialsResponse), typeof(RouteOrderFinancialsResponse),
            typeof(RouteFinancialsResponse),
        })
        {
            Assert.All(
                type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(property => Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType),
                propertyType =>
                {
                    Assert.NotEqual(typeof(float), propertyType);
                    Assert.NotEqual(typeof(double), propertyType);
                    Assert.NotEqual(typeof(decimal), propertyType);
                });
        }
    }

    [Fact]
    public void Cod_write_path_keeps_REST_authority_tenant_locking_and_append_only_evidence()
    {
        var service = Read(
            "src", "Modules", "Finance", "Finance.Infrastructure", "Cod", "PostgreSqlCodTransactionService.cs");
        var persistence = Read(
            "src", "Modules", "Finance", "Finance.Infrastructure", "Cod",
            "PostgreSqlCodTransactionService.Persistence.cs");
        var gateway = Read(
            "src", "Modules", "Finance", "Finance.Infrastructure", "Persistence", "FinanceTenantGateway.cs");

        Assert.Equal("FIN-001:RECORD_COD", PostgreSqlCodTransactionService.RecordIdempotencyScope);
        Assert.Equal("FIN-001:RECONCILE_COD", PostgreSqlCodTransactionService.ReconcileIdempotencyScope);
        Assert.Contains("pg_advisory_xact_lock", gateway, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock", persistence, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", service, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", persistence, StringComparison.Ordinal);
        Assert.Contains("platform.idempotency_keys", persistence, StringComparison.Ordinal);
        Assert.Contains("finance.cod_transactions", persistence, StringComparison.Ordinal);

        // COD evidence is the append-only audit log. No outbox event is published, because an unrouted
        // topic would be claimed as UNOWNED and settled DEAD with UNKNOWN_TOPIC.
        Assert.Contains("IAppendOnlyAuditWriter", service, StringComparison.Ordinal);
        Assert.Contains("platform.audit_logs", ReadAuditWriter(), StringComparison.Ordinal);
        Assert.DoesNotContain("platform.outbox_events", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("platform.outbox_events", service, StringComparison.Ordinal);

        // The COD row is created directly as RECORDED: the expectation itself lives on the order, so
        // FIN-001 never duplicates it as a separate EXPECTED row.
        Assert.Contains("'RECORDED'", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("'EXPECTED'", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("RETURNING", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("finance.settlement", service, StringComparison.Ordinal);
        Assert.DoesNotContain("finance.settlement", persistence, StringComparison.Ordinal);
    }

    [Fact]
    public void Financials_read_path_derives_money_and_never_writes()
    {
        var source = Read(
            "src", "Modules", "Finance", "Finance.Infrastructure", "Financials",
            "PostgreSqlOrderFinancialsService.cs");

        Assert.Equal(
            ["ACCEPTED", "ACTIVE", "COMPLETED"],
            PostgreSqlOrderFinancialsService.CostBearingAssignmentStatuses);
        Assert.Contains("orders.orders", source, StringComparison.Ordinal);
        Assert.Contains("dispatch.assignments", source, StringComparison.Ordinal);
        Assert.Contains("routes.route_stops", source, StringComparison.Ordinal);
        Assert.Contains("finance.cod_transactions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", source, StringComparison.Ordinal);
        Assert.DoesNotContain("finance.settlement", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Finance_does_not_reach_into_SET001_settlement_tables_anywhere()
    {
        var offenders = Directory
            .GetFiles(
                Path.Combine(RepositoryPaths.Root, "src", "Modules", "Finance"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("settlement", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileName(path))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static string ReadAuditWriter() => Read(
        "src", "BuildingBlocks", "Paqueteria.Infrastructure", "Auditing",
        "PostgreSqlAppendOnlyAuditWriter.cs");

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryPaths.Root, .. segments]));

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
