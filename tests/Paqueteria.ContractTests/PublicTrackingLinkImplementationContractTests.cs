using System.Reflection;
using System.Text.Json.Serialization;
using Orders.Endpoints;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// TRK-002-ISSUE-ENDPOINT and TRK-002-AUTO-LINK: the two authenticated tracking link operations are served exactly
/// as AI-05 contracts them (get-or-create 200 and revoke 204), and the one response that carries the plaintext token
/// is declared no-store.
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

        foreach (var (operation, success, conflict) in new[]
                 {
                     (issue, "200", "#/components/responses/TrackingLinkConflict"),
                     (revoke, "204", "#/components/responses/Conflict"),
                 })
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
            Assert.Equal(conflict, responses.Mapping("409").Scalar("$ref"));
            Assert.Equal("#/components/responses/ServiceUnavailable", responses.Mapping("503").Scalar("$ref"));
            Assert.Equal(
                "shape-validation-then-capability-before-persisted-state",
                operation.Scalar("x-authorization-precedence"));
            Assert.Contains("GATE-004", issue.Scalar("description"), StringComparison.Ordinal);
        }

        Assert.Contains("TRK-002-AUTO-LINK", issue.Scalar("description"), StringComparison.Ordinal);
        Assert.Contains("get-or-create", issue.Scalar("description"), StringComparison.Ordinal);
        Assert.Equal(
            ["TRACKING_LINK_ORDER_FINISHED"],
            Contract.Mapping("components").Mapping("schemas").Mapping("TrackingLinkConflictProblem")
                .Mapping("properties").Mapping("code").Sequence("enum").Children
                .Cast<YamlScalarNode>().Select(node => node.Value!));
        Assert.Equal(
            Orders.Application.Tracking.PublicTrackingLinkOrderFinishedException.ProblemCode,
            "TRACKING_LINK_ORDER_FINISHED");

        var created = issue.Mapping("responses").Mapping("200");
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

        Assert.Equal(
            "^https://[^/?#]+/track/[A-Za-z0-9_-]{43}$",
            schema.Mapping("properties").Mapping("url").Scalar("pattern"));

        const string token = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";
        var grant = new Orders.Application.Tracking.PublicTrackingTokenGrant(
            Guid.NewGuid(), Guid.NewGuid(), token, 2, DateTimeOffset.UnixEpoch);
        var response = PublicTrackingLinkResponse.From(grant, "https://paquetenvia.com");
        Assert.Equal($"https://paquetenvia.com/track/{token}", response.Url);
        Assert.Matches(schema.Mapping("properties").Mapping("url").Scalar("pattern"), response.Url);
        Assert.Equal(2, response.Generation);
        Assert.Equal(DateTimeOffset.UnixEpoch, response.ValidUntil);
        Assert.DoesNotContain(token, response.ToString(), StringComparison.Ordinal);
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
