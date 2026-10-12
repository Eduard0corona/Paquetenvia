using System.Text.Json;
using System.Text.RegularExpressions;
using Drivers.Application.DispatchContacts;
using Drivers.Endpoints;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// DRV-DISPATCH-CONTACT-2026-10-12 (project owner, "Sí, configurable (Recommended)"): getMyDispatchContact answers the
/// dispatch number configured for the selected organization as the closed object {"phone_e164": ...}, null when
/// there is none, to DRIVER members only, from the server configuration and never from the database.
/// </summary>
public sealed class DriverDispatchContactContractTests
{
    private const string SyntheticNumber = "+522000000001";

    private static readonly YamlMappingNode Contract =
        YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));

    [Fact]
    public void AI05_declares_the_operation_like_the_neighbouring_driver_reads()
    {
        var operation = Contract.Mapping("paths").Mapping("/driver/me/dispatch-contact").Mapping("get");
        Assert.Equal("getMyDispatchContact", operation.Scalar("operationId"));
        Assert.Equal(["Driver"], operation.Sequence("tags").Children.Select(Value));
        Assert.Equal("capability-before-persisted-state", operation.Scalar("x-authorization-precedence"));
        var description = operation.Scalar("description");
        Assert.StartsWith("DRV-DISPATCH-CONTACT-2026-10-12", description, StringComparison.Ordinal);
        Assert.Contains("\"Sí, configurable (Recommended)\"", description, StringComparison.Ordinal);
        Assert.Contains("DRIVER members only", description, StringComparison.Ordinal);
        Assert.Contains("never answers another organization's", description, StringComparison.Ordinal);
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
            "#/components/schemas/DriverDispatchContact",
            responses.Mapping("200").Mapping("content").Mapping("application/json").Mapping("schema").Scalar("$ref"));
        // The same answers as the other /driver/me read of the driver's own data (getMyDriverPhone).
        Assert.Equal(
            responses.Children.Keys.Select(Value),
            Contract.Mapping("paths").Mapping("/driver/me/phone").Mapping("get").Mapping("responses").Children.Keys
                .Select(Value));
    }

    [Fact]
    public void AI05_answer_is_a_closed_object_with_a_nullable_mexican_e164_number()
    {
        var answer = Contract.Mapping("components").Mapping("schemas").Mapping("DriverDispatchContact");
        Assert.Equal("object", answer.Scalar("type"));
        Assert.Equal("false", answer.Scalar("additionalProperties"));
        Assert.Equal(["phone_e164"], answer.Sequence("required").Children.Select(Value));
        Assert.Equal(["phone_e164"], answer.Mapping("properties").Children.Keys.Select(Value));
        Assert.StartsWith("DRV-DISPATCH-CONTACT-2026-10-12", answer.Scalar("description"), StringComparison.Ordinal);
        var phone = answer.Mapping("properties").Mapping("phone_e164");
        Assert.Equal(["string", "null"], phone.Sequence("type").Children.Select(Value));

        // The published pattern and the server's startup validation accept exactly the same numbers.
        Assert.Equal(@"^\+52[2-9][0-9]{9}$", phone.Scalar("pattern"));
        var pattern = new Regex(phone.Scalar("pattern"), RegexOptions.CultureInvariant | RegexOptions.ECMAScript);
        foreach (var candidate in new[]
                 {
                     SyntheticNumber, "+522000000009", "+520000000001", "+521000000001", "+52200000000",
                     "+5220000000011", "522000000001", "+12025550100", "+52 2000000001", "+52-200-000-0001", "",
                     "+5220000000\u06601",
                 })
        {
            Assert.Equal(pattern.IsMatch(candidate), DriverDispatchContactPolicy.IsPhoneE164(candidate));
        }

        // .NET's $ also matches before a final line break; the server never accepts one.
        Assert.False(DriverDispatchContactPolicy.IsPhoneE164(SyntheticNumber + "\n"));
        Assert.True(DriverDispatchContactPolicy.IsPhoneE164(SyntheticNumber));
        Assert.False(DriverDispatchContactPolicy.IsPhoneE164(null));

        var delta = Contract.Mapping("x-pilot-contract-deltas").Sequence("entries").Children
            .Cast<YamlMappingNode>()
            .Single(entry => entry.Scalar("id") == "DRV-DISPATCH-CONTACT");
        Assert.Equal("DECIDED", delta.Scalar("status"));
        Assert.StartsWith("DRV-DISPATCH-CONTACT-2026-10-12", delta.Scalar("decision"), StringComparison.Ordinal);
        Assert.Equal(
            "\"En la app del repartidor, \"Llama a despacho\" (cuando ya registró un cobro y no puede entregar) no " +
            "muestra ningún número porque la app no tiene uno. ¿Agregamos un teléfono de despacho?\" - Sí, " +
            "configurable (Recommended)",
            delta.Scalar("literal"));
        Assert.Equal(
            "Un número de despacho por empresa en la configuración del piloto; la app lo muestra como botón " +
            "\"Llamar a despacho\". Tú me das el número cuando lo tengas.",
            delta.Scalar("option"));
        Assert.Equal(
            "No, ya lo saben (\"Los repartidores ya tienen el número de despacho; la app solo dice que llamen.\")",
            delta.Scalar("not_chosen"));
    }

    [Fact]
    public void Response_record_serializes_exactly_the_AI05_member_and_never_prints_the_number()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using (var configured = JsonDocument.Parse(JsonSerializer.Serialize(new DriverDispatchContactResponse(SyntheticNumber), web)))
        {
            Assert.Equal(["phone_e164"], configured.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(SyntheticNumber, configured.RootElement.GetProperty("phone_e164").GetString());
        }

        using (var none = JsonDocument.Parse(JsonSerializer.Serialize(new DriverDispatchContactResponse(null), web)))
        {
            Assert.Equal(["phone_e164"], none.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(JsonValueKind.Null, none.RootElement.GetProperty("phone_e164").ValueKind);
        }

        Assert.DoesNotContain(SyntheticNumber, new DriverDispatchContactResponse(SyntheticNumber).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            SyntheticNumber,
            new Drivers.Infrastructure.DriverDispatchContactOptions
            {
                OrganizationId = "11111111-1111-1111-1111-111111111111",
                PhoneE164 = SyntheticNumber,
            }.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoint_checks_the_capability_before_reading_the_configuration_and_never_the_database()
    {
        Assert.Equal("/api/v1/driver/me/dispatch-contact", DriverDispatchContactEndpoints.Path);
        var endpoint = Read("src", "Modules", "Drivers", "Drivers.Endpoints", "DriverDispatchContactEndpoints.cs");
        foreach (var fragment in new[]
                 {
                     "endpoints.MapGet(Path, GetMyDispatchContact)",
                     ".RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)",
                     ".RequireTenantContext(StatusCodes.Status403Forbidden)",
                     ".WithName(\"getMyDispatchContact\")",
                     "TenantCapabilities.GetMyDispatchContact",
                     "directory.FindPhoneE164(tenantContext.OrganizationId)",
                 })
        {
            Assert.Contains(fragment, endpoint, StringComparison.Ordinal);
        }

        var start = endpoint.IndexOf("private static IResult GetMyDispatchContact(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var handler = endpoint[start..];
        var noStore = handler.IndexOf("CacheControl = \"no-store\"", StringComparison.Ordinal);
        var capability = handler.IndexOf("TenantCapabilityGate.Deny(", StringComparison.Ordinal);
        var read = handler.IndexOf("directory.FindPhoneE164(", StringComparison.Ordinal);
        Assert.True(noStore > 0 && noStore < capability && capability < read);
        Assert.Equal(1, Count(endpoint, "endpoints.Map"));

        var directory = Read(
            "src", "Modules", "Drivers", "Drivers.Infrastructure", "DispatchContacts",
            "ConfiguredDriverDispatchContactDirectory.cs");
        foreach (var source in new[] { endpoint, directory })
        {
            foreach (var forbidden in new[]
                     {
                         "DbContext", "TransactionContext", "Npgsql", "ILogger", "Log", "SaveChanges", "ExecuteAsync",
                     })
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
            }
        }

        var program = Read("src", "Paqueteria.Api", "Program.cs");
        Assert.Contains("app.MapDriverDispatchContactEndpoints();", program, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatchContact", Read("src", "Paqueteria.Worker", "Program.cs"), StringComparison.Ordinal);
    }

    private static string Value(YamlNode node) => Assert.IsType<YamlScalarNode>(node).Value!;

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryPaths.Root, .. segments]));

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;
}
