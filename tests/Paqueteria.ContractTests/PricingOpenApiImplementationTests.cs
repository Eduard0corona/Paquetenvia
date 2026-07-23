using System.Reflection;
using System.Text.Json.Serialization;
using Paqueteria.Application.Idempotency;
using Pricing.Endpoints;

namespace Paqueteria.ContractTests;

public sealed class PricingOpenApiImplementationTests
{
    [Fact]
    public void Implementation_exposes_only_the_two_normative_quote_operations()
    {
        var source = ReadRepositoryFile("src", "Modules", "Pricing", "Pricing.Endpoints", "QuoteEndpoints.cs");
        Assert.Equal(1, Count(source, "endpoints.MapPost("));
        Assert.Equal(1, Count(source, "endpoints.MapGet("));
        Assert.Contains("MapPost(\"/api/v1/quotes\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/quotes/{quoteId:guid}\"", source, StringComparison.Ordinal);
        Assert.Equal(2, Count(source, ".RequireTenantContext(StatusCodes.Status403Forbidden)"));
        Assert.Contains("request.Headers[\"Idempotency-Key\"]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_and_response_DTOs_match_AI05_without_internal_or_PII_fields()
    {
        AssertJsonProperties<CreateQuoteRequest>(
            "client_account_id", "consolidated_route", "destination", "origin", "packages", "service_type");
        AssertJsonProperties<AddressInput>("address_text", "contact_name", "lat", "lng", "phone", "references");
        AssertJsonProperties<PackageInput>(
            "declared_value_cents", "description", "height_mm", "length_mm", "weight_grams", "width_mm");
        AssertJsonProperties<QuoteResponse>(
            "breakdown", "city_id", "consolidated_route", "destination_location_id", "expires_at", "id",
            "minimum_total_cents_snapshot", "net", "origin_location_id", "package_snapshot", "pricing_policy_version",
            "pricing_tier", "request_snapshot_redacted", "rule_ids", "service_area_id", "service_type", "status", "tax", "total");

        var response = typeof(QuoteResponse).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain(response, name => name.Contains("Owner", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(response, name => name.Contains("ClientAccount", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(response, name => name.Contains("Hash", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(response, name => name.Contains("Cipher", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(response, name => name.Contains("Financial", StringComparison.OrdinalIgnoreCase));
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
