using System.Reflection;
using System.Text.Json.Serialization;
using Orders.Application.Tracking;
using Orders.Endpoints;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

public sealed class PublicTrackingImplementationContractTests
{
    [Fact]
    public void Product_endpoint_and_DTO_match_AI05_without_management_surface()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "src",
            "Modules",
            "Orders",
            "Orders.Endpoints",
            "PublicTrackingEndpoints.cs"));
        Assert.Contains(
            "public const string Path = \"/api/v1/tracking/{token}\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(".AllowAnonymous()", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"publicTracking\")", source, StringComparison.Ordinal);
        Assert.Contains("IPublicTrackingProjectionReader reader", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPost(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPut(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", source, StringComparison.Ordinal);

        AssertJsonProperties<PublicTrackingResponse>(
            "aggregate_version",
            "estimated_window",
            "public_id",
            "public_status",
            "timeline");
        AssertJsonProperties<PublicTrackingTimelineResponse>("code", "occurred_at");

        var root = YamlNodes.LoadMapping(
            RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var operation = root.Mapping("paths").Mapping("/tracking/{token}").Mapping("get");
        Assert.Equal("publicTracking", operation.Scalar("operationId"));
        var schema = root.Mapping("components").Mapping("schemas")
            .Mapping("PublicTrackingProjection");
        Assert.Equal(
            ["aggregate_version", "estimated_window", "public_id", "public_status", "timeline"],
            schema.Mapping("properties").Children.Keys
                .Cast<YamlScalarNode>()
                .Select(node => node.Value!)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void Lifecycle_options_keep_the_contracted_secure_defaults()
    {
        var options = new PublicTrackingOptions();
        Assert.Equal(168d, options.TokenLifetimeHours);
        Assert.Equal(3, options.TokenCollisionRetryCount);
        Assert.Equal(60, options.LookupPermitLimit);
        Assert.Equal(60, options.LookupWindowSeconds);
        Assert.Empty(options.AllowedOrigins);

        var grantProperties = typeof(PublicTrackingTokenGrant)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["ExpiresAt", "OrderId", "Token", "TokenId"], grantProperties);
        Assert.DoesNotContain(
            typeof(IssuePublicTrackingTokenCommand).Assembly.GetTypes(),
            type =>
                type != typeof(PublicTrackingTokenGrant) &&
                type.GetProperties().Any(property =>
                    property.Name == "Token" &&
                    property.PropertyType == typeof(string)));
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
}
