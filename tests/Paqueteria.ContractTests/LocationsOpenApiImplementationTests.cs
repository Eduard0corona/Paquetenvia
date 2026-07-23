using System.Reflection;
using System.Text.Json.Serialization;
using Locations.Application.Locations;
using Locations.Endpoints;
using Paqueteria.ContractTests.Support;

namespace Paqueteria.ContractTests;

public sealed class LocationsOpenApiImplementationTests
{
    [Fact]
    public void Implementation_exposes_only_the_five_normative_location_operations()
    {
        var source = ReadRepositoryFile(
            "src", "Modules", "Locations", "Locations.Endpoints", "LocationEndpoints.cs");

        Assert.Equal(4, Count(source, "endpoints.MapGet("));
        Assert.Equal(1, Count(source, "endpoints.MapPost("));
        Assert.Contains("MapGet(\"/api/v1/cities\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/service-areas\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/operating-zones\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/locations\"", source, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/api/v1/locations\"", source, StringComparison.Ordinal);
        Assert.Contains("Guid city_id", source, StringComparison.Ordinal);
        Assert.Contains("Guid service_area_id", source, StringComparison.Ordinal);
        Assert.Contains("request.Headers[\"Idempotency-Key\"]", source, StringComparison.Ordinal);
        Assert.Equal(5, Count(source, ".RequireTenantContext()"));
    }

    [Fact]
    public void Transport_DTOs_match_AI05_and_never_expose_tenant_or_PII_storage_fields()
    {
        AssertJsonProperties<CityResponse>("country_code", "id", "name", "state_code", "timezone");
        AssertJsonProperties<ServiceAreaResponse>("city_id", "id", "name", "status");
        AssertJsonProperties<OperatingZoneResponse>("id", "name", "service_area_id", "status", "zone_type");
        AssertJsonProperties<LocationResponse>(
            "address_summary", "city_id", "id", "lat", "lng", "operating_zone_id", "service_area_id");
        AssertJsonProperties<CreateLocationRequest>(
            "address_summary", "address_text", "city_id", "contact_name", "lat", "lng",
            "operating_zone_id", "phone", "pii_key_version", "service_area_id");

        var exposed = typeof(LocationResponse).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain(exposed, name => name.Contains("Ciphertext", StringComparison.Ordinal));
        Assert.DoesNotContain(exposed, name => name.Contains("Phone", StringComparison.Ordinal));
        Assert.DoesNotContain(exposed, name => name.Contains("Contact", StringComparison.Ordinal));
        Assert.DoesNotContain(exposed, name => name.Contains("Pii", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(CreateLocationRequest).GetProperties(), property => property.Name == "OwnerOrganizationId");
    }

    [Fact]
    public void Canonical_Quote_and_Order_retain_geographic_references()
    {
        var contract = ReadRepositoryFile("docs", "normative", "v0.6", "contracts", "AI-05_OPENAPI.yaml");
        foreach (var schema in new[] { (Name: "Quote:", Next: "CreateOrderRequest:"), (Name: "Order:", Next: "OrderDetail:") })
        {
            var start = contract.IndexOf($"    {schema.Name}", StringComparison.Ordinal);
            Assert.True(start >= 0, $"Missing {schema.Name}");
            var next = contract.IndexOf($"\n    {schema.Next}", start + schema.Name.Length, StringComparison.Ordinal);
            var block = next < 0 ? contract[start..] : contract[start..next];
            Assert.Contains("origin_location_id", block, StringComparison.Ordinal);
            Assert.Contains("destination_location_id", block, StringComparison.Ordinal);
            Assert.Contains("city_id", block, StringComparison.Ordinal);
            Assert.Contains("service_area_id", block, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Idempotency_key_policy_matches_the_structured_AI05_contract()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var schema = root.Mapping("components").Mapping("parameters").Mapping("IdempotencyKey").Mapping("schema");

        Assert.Equal(IdempotencyKeyPolicy.MinimumLength, int.Parse(schema.Scalar("minLength")));
        Assert.Equal(IdempotencyKeyPolicy.MaximumLength, int.Parse(schema.Scalar("maxLength")));
        Assert.False(IdempotencyKeyPolicy.IsValid(new string('a', IdempotencyKeyPolicy.MinimumLength - 1)));
        Assert.True(IdempotencyKeyPolicy.IsValid(new string('a', IdempotencyKeyPolicy.MinimumLength)));
        Assert.True(IdempotencyKeyPolicy.IsValid(new string('a', IdempotencyKeyPolicy.MaximumLength)));
        Assert.False(IdempotencyKeyPolicy.IsValid(new string('a', IdempotencyKeyPolicy.MaximumLength + 1)));
    }

    private static void AssertJsonProperties<T>(params string[] expected)
    {
        var actual = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
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
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join('/', segments));
    }
}
