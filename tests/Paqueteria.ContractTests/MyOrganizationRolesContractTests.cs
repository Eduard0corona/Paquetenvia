using System.Text.Json;
using Organizations.Application.OrganizationRoles;
using Organizations.Endpoints;
using Paqueteria.ContractTests.Support;
using Paqueteria.Domain.Tenancy;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// ORG-MY-ROLES-2026-10-12 (project owner, "Endpoint nuevo de roles (Recommended)"): getMyOrganizationRoles answers
/// the requesting person's own ACTIVE membership roles in the organization selected with X-Organization-Id as the
/// closed object {"roles":[...]}; listOrganizationContexts and OrganizationContext stay exactly as before.
/// </summary>
public sealed class MyOrganizationRolesContractTests
{
    private static readonly YamlMappingNode Contract =
        YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));

    [Fact]
    public void AI05_declares_the_operation_like_the_neighbouring_me_reads()
    {
        var operation = Contract.Mapping("paths").Mapping("/me/organization-roles").Mapping("get");
        Assert.Equal("getMyOrganizationRoles", operation.Scalar("operationId"));
        Assert.Equal(["Identity"], operation.Sequence("tags").Children.Select(Value));
        Assert.Equal("capability-before-persisted-state", operation.Scalar("x-authorization-precedence"));
        var description = operation.Scalar("description");
        Assert.StartsWith("ORG-MY-ROLES-2026-10-12", description, StringComparison.Ordinal);
        Assert.Contains("\"Endpoint nuevo de roles (Recommended)\"", description, StringComparison.Ordinal);
        Assert.Contains("Cache-Control no-store", description, StringComparison.Ordinal);
        Assert.Equal(
            ["#/components/parameters/OrganizationContext"],
            operation.Sequence("parameters").Children.Cast<YamlMappingNode>().Select(node => node.Scalar("$ref")));
        var responses = operation.Mapping("responses");
        Assert.Equal(["200", "401", "403", "503"], responses.Children.Keys.Select(Value));
        Assert.Equal("#/components/responses/Unauthorized", responses.Mapping("401").Scalar("$ref"));
        Assert.Equal("#/components/responses/Forbidden", responses.Mapping("403").Scalar("$ref"));
        Assert.Equal("#/components/responses/ServiceUnavailable", responses.Mapping("503").Scalar("$ref"));
        Assert.Equal(
            "#/components/schemas/MyOrganizationRoles",
            responses.Mapping("200").Mapping("content").Mapping("application/json").Mapping("schema").Scalar("$ref"));
        // The neighbouring /me read with a tenant context answers the same set (getMyDriverPhone).
        Assert.Equal(
            responses.Children.Keys.Select(Value),
            Contract.Mapping("paths").Mapping("/driver/me/phone").Mapping("get").Mapping("responses").Children.Keys
                .Select(Value));
    }

    [Fact]
    public void AI05_answer_is_a_closed_object_of_unique_membership_roles()
    {
        var schemas = Contract.Mapping("components").Mapping("schemas");
        var answer = schemas.Mapping("MyOrganizationRoles");
        Assert.Equal("object", answer.Scalar("type"));
        Assert.Equal("false", answer.Scalar("additionalProperties"));
        Assert.Equal(["roles"], answer.Sequence("required").Children.Select(Value));
        Assert.Equal(["roles"], answer.Mapping("properties").Children.Keys.Select(Value));
        var roles = answer.Mapping("properties").Mapping("roles");
        Assert.Equal("array", roles.Scalar("type"));
        Assert.Equal("1", roles.Scalar("minItems"));
        Assert.Equal("9", roles.Scalar("maxItems"));
        Assert.Equal("true", roles.Scalar("uniqueItems"));
        Assert.StartsWith("ORG-MY-ROLES-2026-10-12", roles.Scalar("description"), StringComparison.Ordinal);
        var values = roles.Mapping("items").Sequence("enum").Children.Select(Value).ToArray();
        Assert.Equal(Enum.GetValues<OrganizationRole>().Select(role => role.ToContractValue()), values);
        Assert.Equal(
            schemas.Mapping("PendingMembership").Mapping("properties").Mapping("role").Sequence("enum").Children
                .Select(Value),
            values);

        // ORG-MY-ROLES-2026-10-12: OrganizationContext keeps exactly its four members (no roles).
        var context = schemas.Mapping("OrganizationContext");
        string[] members = ["organization_id", "display_name", "role", "is_default"];
        Assert.Equal(members, context.Sequence("required").Children.Select(Value));
        Assert.Equal(members, context.Mapping("properties").Children.Keys.Select(Value));

        var delta = Contract.Mapping("x-pilot-contract-deltas").Sequence("entries").Children
            .Cast<YamlMappingNode>()
            .Single(entry => entry.Scalar("id") == "ORG-MY-ROLES");
        Assert.Equal("DECIDED", delta.Scalar("status"));
        Assert.StartsWith("ORG-MY-ROLES-2026-10-12", delta.Scalar("decision"), StringComparison.Ordinal);
        Assert.EndsWith("Endpoint nuevo de roles (Recommended)", delta.Scalar("literal"), StringComparison.Ordinal);
        Assert.Contains("Agregar los roles al API (Recommended)", delta.Scalar("earlier"), StringComparison.Ordinal);
        Assert.DoesNotContain(
            Contract.Mapping("x-pilot-contract-deltas").Sequence("entries").Children.Cast<YamlMappingNode>(),
            entry => entry.Scalar("id") == "ORG-CONTEXT-ROLES");
    }

    [Fact]
    public void Response_record_serializes_exactly_the_AI05_member()
    {
        var response = new MyOrganizationRolesResponse(["DISPATCHER", "DRIVER"]);

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.Equal(["roles"], document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["DISPATCHER", "DRIVER"],
            document.RootElement.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
    }

    [Fact]
    public void Roles_are_contract_values_each_once_in_ordinal_order()
    {
        Assert.Equal(
            ["DISPATCHER", "DRIVER"],
            MyOrganizationRoles.FromRoles([OrganizationRole.Driver, OrganizationRole.Dispatcher, OrganizationRole.Driver]));
        Assert.Equal(
            ["DRIVER", "PLATFORM_ADMIN"],
            MyOrganizationRoles.FromRoles([OrganizationRole.PlatformAdmin, OrganizationRole.Driver]));
        Assert.Equal(["DRIVER"], MyOrganizationRoles.FromRoles([OrganizationRole.Driver]));
        Assert.Empty(MyOrganizationRoles.FromRoles([]));
    }

    [Fact]
    public void Endpoint_and_reader_follow_the_tenant_read_pattern_and_never_write()
    {
        Assert.Equal("/api/v1/me/organization-roles", OrganizationEndpoints.MyOrganizationRolesPath);
        var endpoint = Read("src", "Modules", "Organizations", "Organizations.Endpoints", "OrganizationEndpoints.cs");
        foreach (var fragment in new[]
                 {
                     "endpoints.MapGet(MyOrganizationRolesPath, GetMyOrganizationRolesAsync)",
                     ".RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)",
                     ".RequireTenantContext(StatusCodes.Status403Forbidden)",
                     ".WithName(\"getMyOrganizationRoles\")",
                     "httpContext.Response.Headers.CacheControl = \"no-store\";",
                     "TenantCapabilities.GetMyOrganizationRoles",
                     "reader.ReadAsync(userId, tenantContext.OrganizationId, cancellationToken)",
                 })
        {
            Assert.Contains(fragment, endpoint, StringComparison.Ordinal);
        }

        // Capability first, then the read; no roles, the uniform 403.
        var start = endpoint.IndexOf("Task<IResult> GetMyOrganizationRolesAsync(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var handler = endpoint[start..];
        var capability = handler.IndexOf("TenantCapabilityGate.Deny(", StringComparison.Ordinal);
        var read = handler.IndexOf("reader.ReadAsync(", StringComparison.Ordinal);
        Assert.True(capability > 0 && capability < read);
        Assert.Contains("roles.Count == 0", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPost(MyOrganizationRolesPath", endpoint, StringComparison.Ordinal);

        var reader = Read(
            "src", "Modules", "Organizations", "Organizations.Infrastructure", "OrganizationRoles",
            "PostgreSqlMyOrganizationRolesReader.cs");
        Assert.Equal(1, Count(reader, "transactionContext.ExecuteAsync("));
        Assert.Contains("new TenantDatabaseExecutionContext(userId, [organizationId])", reader, StringComparison.Ordinal);
        Assert.Contains("membership.UserId == userId", reader, StringComparison.Ordinal);
        Assert.Contains("membership.Status == MembershipStatus.Active", reader, StringComparison.Ordinal);
        Assert.Contains("organization.Status == OrganizationStatus.Active", reader, StringComparison.Ordinal);
        Assert.Contains(".AsNoTracking()", reader, StringComparison.Ordinal);
        foreach (var write in new[] { "SaveChanges", ".Add(", ".Update(", ".Remove(", "ExecuteUpdate", "ExecuteDelete" })
        {
            Assert.DoesNotContain(write, reader, StringComparison.Ordinal);
        }
    }

    private static string Value(YamlNode node) => Assert.IsType<YamlScalarNode>(node).Value!;

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryPaths.Root, .. segments]));

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;
}
