using System.Text.Json;
using Organizations.Application.OrganizationContexts;
using Paqueteria.ContractTests.Support;
using Paqueteria.Domain.Tenancy;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// ORG-CONTEXT-ROLES-2026-10-12 (project owner, "Agregar los roles al API (Recommended)"): listOrganizationContexts
/// answers, per organization, every role of the requesting person's own ACTIVE memberships in the required roles
/// array. Additive: role and is_default keep their meaning, and nothing about any other person is answered.
/// </summary>
public sealed class OrganizationContextRolesContractTests
{
    private static readonly YamlMappingNode Contract =
        YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));

    private static readonly Guid OrganizationA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid OrganizationB = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Fact]
    public void AI05_declares_roles_as_a_required_unique_non_empty_array_of_the_membership_role_enum()
    {
        var schemas = Contract.Mapping("components").Mapping("schemas");
        var schema = schemas.Mapping("OrganizationContext");
        string[] members = ["organization_id", "display_name", "role", "is_default", "roles"];
        Assert.Equal(members, schema.Sequence("required").Children.Select(Value));
        Assert.Equal(members, schema.Mapping("properties").Children.Keys.Select(Value));

        var roles = schema.Mapping("properties").Mapping("roles");
        Assert.Equal("array", roles.Scalar("type"));
        Assert.Equal("1", roles.Scalar("minItems"));
        Assert.Equal("9", roles.Scalar("maxItems"));
        Assert.Equal("true", roles.Scalar("uniqueItems"));
        Assert.StartsWith("ORG-CONTEXT-ROLES-2026-10-12", roles.Scalar("description"), StringComparison.Ordinal);
        var items = roles.Mapping("items");
        Assert.Equal("string", items.Scalar("type"));
        var values = items.Sequence("enum").Children.Select(Value).ToArray();
        Assert.Equal(Enum.GetValues<OrganizationRole>().Select(role => role.ToContractValue()), values);
        // The same enum as every other membership role of AI-05.
        Assert.Equal(
            schemas.Mapping("PendingMembership").Mapping("properties").Mapping("role").Sequence("enum").Children
                .Select(Value),
            values);

        var operation = Contract.Mapping("paths").Mapping("/me/organization-contexts").Mapping("get");
        Assert.Equal("listOrganizationContexts", operation.Scalar("operationId"));
        Assert.Contains("ORG-CONTEXT-ROLES-2026-10-12", operation.Scalar("description"), StringComparison.Ordinal);
        Assert.Equal(
            "#/components/schemas/OrganizationContext",
            operation.Mapping("responses").Mapping("200").Mapping("content").Mapping("application/json")
                .Mapping("schema").Mapping("items").Scalar("$ref"));

        var delta = Contract.Mapping("x-pilot-contract-deltas").Sequence("entries").Children
            .Cast<YamlMappingNode>()
            .Single(entry => entry.Scalar("id") == "ORG-CONTEXT-ROLES");
        Assert.Equal("DECIDED", delta.Scalar("status"));
        Assert.StartsWith("ORG-CONTEXT-ROLES-2026-10-12", delta.Scalar("decision"), StringComparison.Ordinal);
        Assert.EndsWith("Agregar los roles al API (Recommended)", delta.Scalar("literal"), StringComparison.Ordinal);
    }

    [Fact]
    public void Response_record_serializes_exactly_the_AI05_members()
    {
        var response = new OrganizationContextResponse(OrganizationA, "Alpha", "DRIVER", true, ["DISPATCHER", "DRIVER"]);

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.Equal(
            ["display_name", "is_default", "organization_id", "role", "roles"],
            document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("DRIVER", document.RootElement.GetProperty("role").GetString());
        Assert.True(document.RootElement.GetProperty("is_default").GetBoolean());
        Assert.Equal(
            ["DISPATCHER", "DRIVER"],
            document.RootElement.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
    }

    [Fact]
    public void Roles_are_every_role_of_the_callers_memberships_per_organization_unique_and_sorted()
    {
        var roles = OrganizationContextRoles.ByOrganization(
        [
            new AuthorizedOrganizationMembership(OrganizationA, OrganizationRole.Driver, true),
            new AuthorizedOrganizationMembership(OrganizationA, OrganizationRole.Dispatcher, false),
            new AuthorizedOrganizationMembership(OrganizationA, OrganizationRole.Driver, false),
            new AuthorizedOrganizationMembership(OrganizationB, OrganizationRole.Viewer, false),
            new AuthorizedOrganizationMembership(OrganizationB, OrganizationRole.PlatformAdmin, false),
        ]);

        Assert.Equal(2, roles.Count);
        // A DRIVER who is also DISPATCHER in the same organization: one context, both roles.
        Assert.Equal(["DISPATCHER", "DRIVER"], roles[OrganizationA]);
        Assert.Equal(["PLATFORM_ADMIN", "VIEWER"], roles[OrganizationB]);
        Assert.Empty(OrganizationContextRoles.ByOrganization([]));
    }

    [Fact]
    public void Reader_answers_the_callers_own_roles_inside_the_existing_tenant_safe_read()
    {
        var reader = Read(
            "src", "Modules", "Organizations", "Organizations.Infrastructure", "OrganizationContexts",
            "PostgreSqlOrganizationContextReader.cs");
        Assert.Contains("OrganizationContextRoles.ByOrganization(memberships)", reader, StringComparison.Ordinal);
        Assert.Contains("rolesByOrganization[organization.Id]", reader, StringComparison.Ordinal);
        Assert.Equal(1, Count(reader, "transactionContext.ExecuteAsync"));
        // The roles come from the request's own memberships: inside the tenant transaction only the organizations
        // are read, never a membership row (of this person or anyone else).
        var transaction = Between(reader, "async (dbContext, token)", "}).ToArray();");
        Assert.Contains("dbContext.Organizations", transaction, StringComparison.Ordinal);
        Assert.DoesNotContain("Memberships", transaction, StringComparison.Ordinal);

        var endpoint = Read(
            "src", "Modules", "Organizations", "Organizations.Endpoints", "OrganizationEndpoints.cs");
        Assert.Contains("session.ActiveMemberships", endpoint, StringComparison.Ordinal);
        Assert.Contains("reader.ReadAsync(userId, memberships, cancellationToken)", endpoint, StringComparison.Ordinal);
    }

    private static string Value(YamlNode node) => Assert.IsType<YamlScalarNode>(node).Value!;

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryPaths.Root, .. segments]));

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Missing '{start}'.");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"Missing '{end}'.");
        return text[from..to];
    }
}
