using System.Reflection;
using System.Text.Json.Serialization;
using Notifications.Application.RecipientNotices;
using Notifications.Endpoints;
using Notifications.Infrastructure.Persistence.Migrations;
using Organizations.Endpoints.Authorization;
using Paqueteria.ContractTests.Support;
using Paqueteria.Domain.Tenancy;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007; AI-05 x-pilot-contract-deltas NTF-RECIPIENT-NOTICES part
/// NTF-007): listOrderRecipientNotices and stopOrderRecipientNotices are served exactly as AI-05 publishes them - paths,
/// parameters, the error matrix, <c>Cache-Control: no-store</c>, the answer keys of the endpoint records, the closed sets
/// and the 409 codes - and no answer schema can carry a recipient value or a scheduled time (T2).
/// </summary>
public sealed class RecipientNoticePanelImplementationContractTests
{
    private static readonly YamlMappingNode Contract =
        YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));

    private static YamlMappingNode Schemas => Contract.Mapping("components").Mapping("schemas");

    [Fact]
    public void Paths_parameters_and_error_matrix_match_AI05()
    {
        var paths = Contract.Mapping("paths");
        var list = paths.Mapping("/orders/{orderId}/recipient-notices").Mapping("get");
        var stop = paths.Mapping("/orders/{orderId}/recipient-notices/stop").Mapping("post");
        Assert.Equal(["get"], Keys(paths.Mapping("/orders/{orderId}/recipient-notices")));
        Assert.Equal(["post"], Keys(paths.Mapping("/orders/{orderId}/recipient-notices/stop")));
        Assert.Equal("listOrderRecipientNotices", list.Scalar("operationId"));
        Assert.Equal("stopOrderRecipientNotices", stop.Scalar("operationId"));
        Assert.Equal("/api/v1/orders/{orderId}/recipient-notices", RecipientNoticePanelEndpoints.ListRoute);
        Assert.Equal("/api/v1/orders/{orderId}/recipient-notices/stop", RecipientNoticePanelEndpoints.StopRoute);
        // T6 choice 1: they keep the AI-05 tag Orders.
        Assert.Equal(["Orders"], Scalars(list.Sequence("tags")));
        Assert.Equal(["Orders"], Scalars(stop.Sequence("tags")));

        Assert.Equal(
            ["#/components/parameters/OrganizationContext", "#/components/parameters/OrderId"],
            list.Sequence("parameters").Children.Cast<YamlMappingNode>().Select(node => node.Scalar("$ref")));
        Assert.Equal(
            [
                "#/components/parameters/OrganizationContext",
                "#/components/parameters/OrderId",
                "#/components/parameters/IdempotencyKey",
            ],
            stop.Sequence("parameters").Children.Cast<YamlMappingNode>().Select(node => node.Scalar("$ref")));
        Assert.False(stop.Children.ContainsKey(new YamlScalarNode("requestBody")));
        Assert.Equal("capability-before-persisted-state", list.Scalar("x-authorization-precedence"));
        Assert.Equal("shape-validation-then-capability-before-persisted-state", stop.Scalar("x-authorization-precedence"));
        Assert.Equal("invalid-request-without-productive-transaction", stop.Scalar("x-shape-validation"));

        Assert.Equal(["200", "401", "403", "404", "503"], Keys(list.Mapping("responses")));
        Assert.Equal(["200", "401", "403", "404", "409", "503"], Keys(stop.Mapping("responses")));
        foreach (var operation in new[] { list, stop })
        {
            var responses = operation.Mapping("responses");
            Assert.Equal("#/components/responses/Unauthorized", responses.Mapping("401").Scalar("$ref"));
            Assert.Equal("#/components/responses/Forbidden", responses.Mapping("403").Scalar("$ref"));
            Assert.Equal("#/components/responses/UniformNotFound", responses.Mapping("404").Scalar("$ref"));
            Assert.Equal("#/components/responses/ServiceUnavailable", responses.Mapping("503").Scalar("$ref"));
            var cacheControl = responses.Mapping("200").Mapping("headers").Mapping("Cache-Control").Mapping("schema");
            Assert.Equal("no-store", cacheControl.Scalar("const"));
            // Neither operation is limited under SEC-004 (T6 choice 10).
            Assert.False(responses.Children.ContainsKey(new YamlScalarNode("429")));
            Assert.Contains("NTF-RECIPIENT-NOTICE-PANEL-2026-10-30", operation.Scalar("description"), StringComparison.Ordinal);
        }

        Assert.Equal(
            "#/components/schemas/RecipientNoticeList",
            list.Mapping("responses").Mapping("200").Mapping("content").Mapping("application/json").Mapping("schema").Scalar("$ref"));
        Assert.Equal(
            "#/components/schemas/RecipientNoticeStopResult",
            stop.Mapping("responses").Mapping("200").Mapping("content").Mapping("application/json").Mapping("schema").Scalar("$ref"));
        Assert.Equal("#/components/responses/RecipientNoticeStopConflict", stop.Mapping("responses").Mapping("409").Scalar("$ref"));
    }

    [Fact]
    public void The_endpoint_records_carry_exactly_the_AI05_properties_and_no_recipient_value()
    {
        foreach (var (schema, type) in new[]
                 {
                     ("RecipientNoticeList", typeof(RecipientNoticeListResponse)),
                     ("RecipientNotice", typeof(RecipientNoticeResponse)),
                     ("RecipientNoticeStopResult", typeof(RecipientNoticeStopResponse)),
                 })
        {
            var published = Schemas.Mapping(schema);
            Assert.Equal("false", published.Scalar("additionalProperties"));
            var properties = Keys(published.Mapping("properties")).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(properties, Scalars(published.Sequence("required")).Order(StringComparer.Ordinal));
            Assert.Equal(properties, JsonNames(type).Order(StringComparer.Ordinal));
            // email_on_file is a boolean; no property is or ends with a recipient value, a link or a scheduled time.
            foreach (var forbidden in new[] { "phone", "email", "e_mail", "link", "url", "token", "parameters", "hash", "contact", "scheduled_for", "recipient" })
            {
                Assert.DoesNotContain(
                    properties,
                    property => property == forbidden || property.EndsWith("_" + forbidden, StringComparison.Ordinal));
            }
        }

        Assert.Equal(
            RecipientNoticePanelContract.MaximumItems.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Schemas.Mapping("RecipientNoticeList").Mapping("properties").Mapping("items").Scalar("maxItems"));
        var channels = Schemas.Mapping("RecipientNoticeStopResult").Mapping("properties").Mapping("channels");
        Assert.Equal("1", channels.Scalar("minItems"));
        Assert.Equal("2", channels.Scalar("maxItems"));
        Assert.Equal("true", channels.Scalar("uniqueItems"));
        Assert.Equal(RecipientNoticePanelContract.Channels, Scalars(channels.Mapping("items").Sequence("enum")));
    }

    [Fact]
    public void The_closed_sets_and_the_409_codes_are_the_published_ones()
    {
        var notice = Schemas.Mapping("RecipientNotice").Mapping("properties");
        Assert.Equal(
            ["TRACKING_LINK", "IN_TRANSIT", "DELIVERY_FAILED", "DELIVERED"],
            Scalars(notice.Mapping("notice").Sequence("enum")));
        Assert.Equal(RecipientNoticePanelContract.Channels, Scalars(notice.Mapping("channel").Sequence("enum")));
        Assert.Equal(RecipientNoticePanelContract.Statuses, Scalars(notice.Mapping("status").Sequence("enum")));
        // T6 choice 4: the lane's external codes, MAX_ATTEMPTS_EXHAUSTED and null.
        var codes = Scalars(notice.Mapping("last_code").Sequence("enum"));
        Assert.Equal(RecipientNoticePanelContract.LastCodes.Append("null"), codes);
        Assert.Equal(AddRecipientNoticeLane.ExternalCodes.Append("MAX_ATTEMPTS_EXHAUSTED"), RecipientNoticePanelContract.LastCodes);
        Assert.Equal(["string", "null"], Scalars(notice.Mapping("last_code").Sequence("type")));

        Assert.Equal(
            RecipientNoticeStopConflictCodes.All,
            Scalars(Schemas.Mapping("RecipientNoticeStopConflictProblem").Mapping("properties").Mapping("code").Sequence("enum")));
        Assert.Equal(
            "#/components/schemas/RecipientNoticeStopConflictProblem",
            Contract.Mapping("components").Mapping("responses").Mapping("RecipientNoticeStopConflict")
                .Mapping("content").Mapping("application/problem+json").Mapping("schema").Scalar("$ref"));
    }

    [Fact]
    public void The_capabilities_are_the_tracking_link_rule_and_decided_before_any_read()
    {
        foreach (var capability in new[] { TenantCapabilities.ListOrderRecipientNotices, TenantCapabilities.StopOrderRecipientNotices })
        {
            Assert.Equal(
                [(OrganizationRole.Dispatcher, false), (OrganizationRole.PlatformAdmin, true)],
                capability.Grants.Select(grant => (grant.Role, grant.RequiresMfa)));
            Assert.Equal(
                TenantCapabilities.IssueTrackingLink.Grants.Select(grant => (grant.Role, grant.RequiresMfa)),
                capability.Grants.Select(grant => (grant.Role, grant.RequiresMfa)));
        }

        var source = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "src", "Modules", "Notifications", "Notifications.Endpoints", "RecipientNoticePanelEndpoints.cs"));
        // The stop's shape, then the capability, then the order; never a read before the capability.
        var stop = source[source.IndexOf("private static async Task<IResult> StopAsync(", StringComparison.Ordinal)..];
        Assert.True(
            stop.IndexOf("TryReadShape(", StringComparison.Ordinal) <
            stop.IndexOf("TenantCapabilities.StopOrderRecipientNotices", StringComparison.Ordinal));
        Assert.True(
            stop.IndexOf("TenantCapabilities.StopOrderRecipientNotices", StringComparison.Ordinal) <
            stop.IndexOf("service.StopAsync(", StringComparison.Ordinal));
        var list = source[source.IndexOf("private static async Task<IResult> ListAsync(", StringComparison.Ordinal)..];
        Assert.True(
            list.IndexOf("TenantCapabilities.ListOrderRecipientNotices", StringComparison.Ordinal) <
            list.IndexOf("service.ListAsync(", StringComparison.Ordinal));
        Assert.Contains("httpContext.Response.Headers.CacheControl = \"no-store\";", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RequireRateLimiting", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Log", source, StringComparison.Ordinal);
    }

    private static string[] JsonNames(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.DeclaringType == type && property.Name != "EqualityContract")
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)
            .ToArray();

    private static string[] Keys(YamlMappingNode node) =>
        node.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value!).ToArray();

    private static string[] Scalars(YamlSequenceNode node) =>
        node.Children.Cast<YamlScalarNode>().Select(value => value.Value ?? "null").ToArray();
}
