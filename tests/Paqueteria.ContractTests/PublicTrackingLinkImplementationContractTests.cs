using System.Reflection;
using System.Text.Json.Serialization;
using Orders.Endpoints;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// TRK-002-ISSUE-ENDPOINT: the two authenticated tracking link operations are served exactly as AI-05 contracts
/// them, and the one response that carries the plaintext token is declared no-store.
/// </summary>
public sealed class PublicTrackingLinkImplementationContractTests
{
    private static readonly YamlMappingNode Contract =
        YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));

    [Fact]
    public void Operations_paths_parameters_and_error_matrix_match_AI05()
    {
        var paths = Contract.Mapping("paths");
        var issue = paths.Mapping("/orders/{orderId}/tracking-link").Mapping("post");
        var revoke = paths.Mapping("/orders/{orderId}/tracking-link/revoke").Mapping("post");
        Assert.Equal("issueTrackingLink", issue.Scalar("operationId"));
        Assert.Equal("revokeTrackingLink", revoke.Scalar("operationId"));
        Assert.Equal("/api/v1/orders/{orderId:guid}/tracking-link", PublicTrackingLinkEndpoints.IssueRoute);
        Assert.Equal("/api/v1/orders/{orderId:guid}/tracking-link/revoke", PublicTrackingLinkEndpoints.RevokeRoute);

        foreach (var (operation, success) in new[] { (issue, "201"), (revoke, "204") })
        {
            Assert.Equal(
                [
                    "#/components/parameters/OrganizationContext",
                    "#/components/parameters/OrderId",
                    "#/components/parameters/IdempotencyKey",
                ],
                operation.Sequence("parameters").Children.Cast<YamlMappingNode>().Select(node => node.Scalar("$ref")));
            var responses = operation.Mapping("responses");
            Assert.Equal(
                [success, "401", "403", "404", "409", "503"],
                responses.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value!));
            Assert.Equal("#/components/responses/Unauthorized", responses.Mapping("401").Scalar("$ref"));
            Assert.Equal("#/components/responses/Forbidden", responses.Mapping("403").Scalar("$ref"));
            Assert.Equal("#/components/responses/UniformNotFound", responses.Mapping("404").Scalar("$ref"));
            Assert.Equal("#/components/responses/Conflict", responses.Mapping("409").Scalar("$ref"));
            Assert.Equal("#/components/responses/ServiceUnavailable", responses.Mapping("503").Scalar("$ref"));
            Assert.Equal(
                "shape-validation-then-capability-before-persisted-state",
                operation.Scalar("x-authorization-precedence"));
            Assert.Contains("GATE-004", issue.Scalar("description"), StringComparison.Ordinal);
        }

        var created = issue.Mapping("responses").Mapping("201");
        Assert.Equal(
            "no-store",
            created.Mapping("headers").Mapping("Cache-Control").Mapping("schema").Scalar("const"));
        Assert.Equal(
            "#/components/schemas/PublicTrackingLink",
            created.Mapping("content").Mapping("application/json").Mapping("schema").Scalar("$ref"));
    }

    [Fact]
    public void Response_DTO_is_exactly_the_PublicTrackingLink_schema_and_redacts_its_text()
    {
        var schema = Contract.Mapping("components").Mapping("schemas").Mapping("PublicTrackingLink");
        Assert.Equal("false", schema.Scalar("additionalProperties"));
        var properties = schema.Mapping("properties").Children.Keys
            .Cast<YamlScalarNode>()
            .Select(key => key.Value!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(properties, JsonPropertyNames<PublicTrackingLinkResponse>());
        Assert.Equal(
            properties,
            schema.Sequence("required").Children.Cast<YamlScalarNode>().Select(node => node.Value!)
                .Order(StringComparer.Ordinal));
        Assert.Equal("^[A-Za-z0-9_-]{43}$", schema.Mapping("properties").Mapping("token").Scalar("pattern"));

        const string token = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";
        var response = new PublicTrackingLinkResponse(Guid.NewGuid(), Guid.NewGuid(), token, DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain(token, response.ToString(), StringComparison.Ordinal);
        var grant = new Orders.Application.Tracking.PublicTrackingTokenGrant(
            Guid.NewGuid(), Guid.NewGuid(), token, DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain(token, grant.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoints_file_maps_only_the_two_operations_with_capability_and_tenant_gates()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "src", "Modules", "Orders", "Orders.Endpoints", "PublicTrackingLinkEndpoints.cs"));
        Assert.Equal(2, Count(source, "endpoints.MapPost("));
        Assert.DoesNotContain("MapGet(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPut(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowAnonymous", source, StringComparison.Ordinal);
        Assert.Equal(2, Count(source, ".RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)"));
        Assert.Equal(2, Count(source, ".RequireTenantContext(StatusCodes.Status403Forbidden)"));
        Assert.Contains(".WithName(\"issueTrackingLink\")", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"revokeTrackingLink\")", source, StringComparison.Ordinal);
        Assert.Contains("TenantCapabilities.IssueTrackingLink", source, StringComparison.Ordinal);
        Assert.Contains("TenantCapabilities.RevokeTrackingLink", source, StringComparison.Ordinal);
        Assert.Contains("headers.CacheControl = \"no-store\"", source, StringComparison.Ordinal);
        Assert.Contains("request.Headers[\"Idempotency-Key\"]", source, StringComparison.Ordinal);
    }

    private static string[] JsonPropertyNames<T>() => typeof(T)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
        .OfType<string>()
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static int Count(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
