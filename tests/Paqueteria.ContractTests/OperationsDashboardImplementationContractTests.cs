using System.Reflection;
using System.Text.Json.Serialization;
using Paqueteria.ContractTests.Support;
using Reporting.Endpoints;
using Reporting.Infrastructure;

namespace Paqueteria.ContractTests;

public sealed class OperationsDashboardImplementationContractTests
{
    [Fact]
    public void Additive_endpoint_has_runtime_metadata_and_does_not_change_orders_contracts()
    {
        var source = Read(
            "src", "Modules", "Reporting", "Reporting.Endpoints",
            "OperationsDashboardEndpoints.cs");
        Assert.Contains(
            "MapGet(\"/api/v1/operations/dashboard\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            ".WithName(\"getOperationsDashboard\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            ".RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            ".RequireTenantContext(StatusCodes.Status403Forbidden)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MapPost(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPut(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", source, StringComparison.Ordinal);

        var orders = Read(
            "src", "Modules", "Orders", "Orders.Endpoints", "OrderEndpoints.cs");
        Assert.DoesNotContain("OperationsDashboard", orders, StringComparison.Ordinal);
    }

    [Fact]
    public void Response_DTOs_are_exact_and_privacy_minimized()
    {
        AssertJsonProperties<OperationsDashboardResponse>(
            "generated_at", "items", "next_cursor");
        AssertJsonProperties<OperationsDashboardOrderResponse>(
            "aggregate_version", "assignment", "client", "cost_warning",
            "created_at", "delivery_window", "delivery_zone", "latest_driver_location",
            "operator", "order_id", "owner", "pickup_window", "public_id",
            "service_type", "status", "total", "unassigned_alert", "updated_at");
        AssertJsonProperties<OperationsAssignmentResponse>(
            "assignment_id", "assignment_type", "driver_id", "driver_reference", "status");
        AssertJsonProperties<OperationsDriverLocationResponse>(
            "accuracy_m", "captured_at", "lat", "lng");
        // UI-PHASE3-INBOX-TOTAL-2026-10-10: the order total in the AI-05 Money shape, int64 cents only.
        AssertJsonProperties<OperationsMoneyResponse>("amount_cents", "currency");
        Assert.Equal(
            typeof(long),
            typeof(OperationsMoneyResponse).GetProperty(nameof(OperationsMoneyResponse.AmountCents))!.PropertyType);

        var allNames = typeof(OperationsDashboardOrderResponse)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(allNames, name =>
            name.Contains("Phone", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Email", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Address", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("AssignmentCost", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Margin", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Cod", StringComparison.Ordinal) ||
            name.Contains("Tariff", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PostgreSql_reader_is_read_only_RLS_scoped_and_uses_two_commands()
    {
        var source = Read(
            "src", "Modules", "Reporting", "Reporting.Infrastructure",
            "Operations", "PostgreSqlOperationsDashboardReader.cs");
        Assert.Equal(2, Count(source, "new NpgsqlCommand("));
        Assert.Contains(
            "set_config('app.current_org_ids', @organization_ids::uuid[]::text, true)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("SET LOCAL ROLE paqueteria_app", source, StringComparison.Ordinal);
        // The in-transaction membership check admits only the dashboard roles, so the order total
        // (UI-PHASE3-INBOX-TOTAL-2026-10-10) never reaches FINANCE, VIEWER or any other role.
        Assert.Contains("AND m.role IN ('PLATFORM_ADMIN', 'DISPATCHER')", source, StringComparison.Ordinal);
        Assert.Contains("BeginTransactionAsync", source, StringComparison.Ordinal);
        Assert.Contains("CommitAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SECURITY DEFINER", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BYPASSRLS", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Configuration_defaults_are_fixed_and_fail_closed()
    {
        var options = new OperationsDashboardOptions();
        Assert.Equal(OperationsDashboardProviderKind.Disabled, options.Provider);
        Assert.Equal(5, options.CommandTimeoutSeconds);
        Assert.Equal(50, options.DefaultPageSize);
        Assert.Equal(100, options.MaximumPageSize);
        Assert.Equal(31, options.MaximumDateRangeDays);
    }

    private static void AssertJsonProperties<T>(params string[] expected)
    {
        var actual = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property =>
                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryPaths.Root, .. segments]));

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;
}
