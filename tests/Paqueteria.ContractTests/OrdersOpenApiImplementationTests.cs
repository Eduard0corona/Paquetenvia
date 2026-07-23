using System.Reflection;
using System.Text.Json.Serialization;
using Orders.Application.Orders;
using Orders.Endpoints;
using Paqueteria.Application.Idempotency;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

public sealed class OrdersOpenApiImplementationTests
{
    [Fact]
    public void Implementation_exposes_only_the_three_normative_order_operations()
    {
        var source = ReadRepositoryFile("src", "Modules", "Orders", "Orders.Endpoints", "OrderEndpoints.cs");
        Assert.Equal(1, Count(source, "endpoints.MapPost("));
        Assert.Equal(2, Count(source, "endpoints.MapGet("));
        Assert.Contains("MapPost(\"/api/v1/orders\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/orders\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/orders/{orderId:guid}\"", source, StringComparison.Ordinal);
        Assert.Equal(3, Count(source, ".RequireTenantContext(StatusCodes.Status403Forbidden)"));
        Assert.Contains("request.Headers[\"Idempotency-Key\"]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPut(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPatch(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_and_response_DTOs_match_AI05_without_internal_or_PII_fields()
    {
        AssertJsonProperties<CreateOrderRequest>("acceptance", "payer_type", "quote_id");
        AssertJsonProperties<OrderAcceptanceRequest>(
            "acceptance_channel", "accepted_at", "privacy_version", "terms_version");
        AssertJsonProperties<OrderResponse>(
            "city_id", "claim_window_ends_at", "destination_location_id", "finalized_at", "id",
            "operator_org_id", "origin_location_id", "owner_org_id", "price_net", "pricing_tier", "public_id",
            "quote_id", "service_area_id", "service_type", "status", "total", "version");
        AssertJsonProperties<OrderTimelineResponse>("event_type", "occurred_at");
        AssertJsonProperties<OrderPageResponse>("items", "next_cursor");

        var responseProperties = typeof(OrderResponse).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain(responseProperties, name =>
            name.Contains("Acceptance", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Evidence", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Financial", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ClientAccount", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Idempotency", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Cipher", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Shared_idempotency_policy_matches_normative_limits()
    {
        Assert.Equal(16, IdempotencyKeyPolicy.MinimumLength);
        Assert.Equal(128, IdempotencyKeyPolicy.MaximumLength);
    }

    [Fact]
    public void AI05_required_order_fields_match_product_DTOs_and_acceptance_policy()
    {
        var root = YamlNodes.LoadMapping(
            RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var createOrder = root.Mapping("components").Mapping("schemas").Mapping("CreateOrderRequest");
        var acceptance = createOrder.Mapping("properties").Mapping("acceptance");

        Assert.Equal(
            JsonPropertyNames<CreateOrderRequest>(),
            RequiredPropertyNames(createOrder));
        Assert.Equal(
            JsonPropertyNames<OrderAcceptanceRequest>(),
            RequiredPropertyNames(acceptance));
        Assert.False(OrderAcceptanceInputPolicy.IsValid(
            "terms-synthetic-v1",
            "privacy-synthetic-v1",
            default,
            "WEB"));
        Assert.True(OrderAcceptanceInputPolicy.IsValid(
            "terms-synthetic-v1",
            "privacy-synthetic-v1",
            DateTimeOffset.Parse(
                "2026-07-22T12:00:00.1234567Z",
                System.Globalization.CultureInfo.InvariantCulture),
            "WEB"));
    }

    private static void AssertJsonProperties<T>(params string[] expected)
    {
        var actual = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static string[] JsonPropertyNames<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] RequiredPropertyNames(YamlMappingNode schema) =>
        schema.Sequence("required").Children
            .Select(node => Assert.IsType<YamlScalarNode>(node).Value!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;

    private static string ReadRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(path)) return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join('/', segments));
    }
}
