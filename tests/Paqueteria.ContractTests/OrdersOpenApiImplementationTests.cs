using System.Reflection;
using System.Text.Json.Serialization;
using Orders.Endpoints;
using Paqueteria.Application.Idempotency;

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

    private static void AssertJsonProperties<T>(params string[] expected)
    {
        var actual = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

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
