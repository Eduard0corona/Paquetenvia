using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Incidents.Application.Incidents;
using Incidents.Domain;
using Incidents.Endpoints;
using Paqueteria.Application.Idempotency;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// Binds AI-05 to the INC-001 implementation in both directions. Every expectation is derived
/// from the domain vocabulary, the transport records or the endpoint source rather than repeated
/// as a literal, so widening either side without republishing the contract fails here.
/// </summary>
public sealed partial class IncidentsOpenApiImplementationTests
{
    private const string IncidentsPath = "/orders/{orderId}/incidents";

    [Fact]
    public void Open_incident_operation_publishes_the_status_matrix_the_endpoint_implements()
    {
        var operation = OpenIncidentOperation();
        var declared = operation.Mapping("responses").Children.Keys
            .Cast<YamlScalarNode>()
            .Select(node => node.Value!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var implemented = ProducedStatusPattern()
            .Matches(EndpointSource())
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(implemented, declared);

        Assert.Equal(
            "#/components/schemas/Incident",
            operation.Mapping("responses").Mapping("201").Mapping("content")
                .Mapping("application/json").Mapping("schema").Scalar("$ref"));
        Assert.Equal(
            "#/components/responses/IncidentConflict",
            operation.Mapping("responses").Mapping("409").Scalar("$ref"));
        Assert.Equal(
            "#/components/responses/UniformNotFound",
            operation.Mapping("responses").Mapping("404").Scalar("$ref"));
        Assert.Equal(
            "#/components/responses/ServiceUnavailable",
            operation.Mapping("responses").Mapping("503").Scalar("$ref"));
    }

    [Fact]
    public void Opening_requires_the_idempotency_key_header_the_endpoint_enforces()
    {
        var operation = OpenIncidentOperation();
        var parameters = operation.Sequence("parameters").Children
            .Cast<YamlMappingNode>()
            .Select(parameter => parameter.Scalar("$ref"))
            .ToArray();
        Assert.Contains("#/components/parameters/IdempotencyKey", parameters, StringComparer.Ordinal);

        var parameter = Contract().Mapping("components").Mapping("parameters").Mapping("IdempotencyKey");
        Assert.Equal("Idempotency-Key", parameter.Scalar("name"));
        Assert.Equal("header", parameter.Scalar("in"));
        Assert.Equal("true", parameter.Scalar("required"));
        Assert.Equal(IdempotencyKeyPolicy.MinimumLength, int.Parse(parameter.Mapping("schema").Scalar("minLength")));
        Assert.Equal(IdempotencyKeyPolicy.MaximumLength, int.Parse(parameter.Mapping("schema").Scalar("maxLength")));

        // The endpoint rejects a missing, duplicated or malformed key before reaching the service.
        var source = EndpointSource();
        Assert.Contains("context.Request.Headers[\"Idempotency-Key\"]", source, StringComparison.Ordinal);
        Assert.Contains("values.Count == 1", source, StringComparison.Ordinal);
        Assert.Contains("IdempotencyKeyPolicy.IsValid(values[0])", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AI05_request_schema_is_exactly_the_implemented_OpenIncidentRequest()
    {
        var schema = Schema("OpenIncidentRequest");
        var transport = JsonProperties<OpenIncidentRequest>();

        Assert.Equal(transport, PropertyNames(schema));

        // AI-08 makes every INC-001 opening field mandatory; none of them may become optional.
        Assert.Equal(transport, Required(schema));
        Assert.Equal("false", schema.Scalar("additionalProperties"));

        // The closed schema is enforced at runtime, not merely documented.
        Assert.Contains("request.ExtensionData is { Count: > 0 }", EndpointSource(), StringComparison.Ordinal);
    }

    [Fact]
    public void AI05_incident_schema_is_exactly_the_implemented_IncidentResponse()
    {
        var schema = Schema("Incident");
        var transport = JsonProperties<IncidentResponse>();

        Assert.Equal(transport, PropertyNames(schema));
        Assert.Equal(transport, Required(schema));
    }

    [Fact]
    public void AI05_enumerations_are_exactly_the_incident_domain_vocabularies()
    {
        var request = Schema("OpenIncidentRequest").Mapping("properties");
        var incident = Schema("Incident").Mapping("properties");

        var severities = Enum.GetValues<IncidentSeverity>().Select(value => value.ToContractValue()).ToArray();
        var reasonCodes = Enum.GetValues<IncidentReasonCode>().Select(value => value.ToContractValue()).ToArray();
        var nextActions = Enum.GetValues<IncidentNextAction>().Select(value => value.ToContractValue()).ToArray();
        string[] statuses =
        [
            IncidentContract.Open,
            IncidentContract.Investigating,
            IncidentContract.Resolved,
            IncidentContract.Rejected,
        ];
        Assert.Equal(statuses.Length, Enum.GetValues<IncidentStatus>().Length);

        foreach (var properties in new[] { request, incident })
        {
            AssertEnum(properties.Mapping("severity"), severities);
            AssertEnum(properties.Mapping("reason_code"), reasonCodes);
            AssertEnum(properties.Mapping("next_action"), nextActions);
        }

        AssertEnum(incident.Mapping("status"), statuses);

        // Every published value round-trips through the parser the endpoint uses.
        Assert.All(severities, value => Assert.True(IncidentContract.TryParseSeverity(value, out _)));
        Assert.All(reasonCodes, value => Assert.True(IncidentContract.TryParseReasonCode(value, out _)));
        Assert.All(nextActions, value => Assert.True(IncidentContract.TryParseNextAction(value, out _)));
    }

    [Fact]
    public void AI05_bounds_are_exactly_the_implemented_request_policies()
    {
        var properties = Schema("OpenIncidentRequest").Mapping("properties");

        var type = properties.Mapping("type");
        Assert.Equal(IncidentRequestPolicy.MaximumIncidentTypeLength, int.Parse(type.Scalar("maxLength")));
        Assert.Equal(1, int.Parse(type.Scalar("minLength")));
        var pattern = type.Scalar("pattern");
        Assert.Matches(pattern, "FAILED_ATTEMPT");
        Assert.DoesNotMatch(pattern, "failed_attempt");
        Assert.True(IncidentRequestPolicy.IsValidIncidentType("FAILED_ATTEMPT"));
        Assert.False(IncidentRequestPolicy.IsValidIncidentType("failed_attempt"));
        Assert.False(IncidentRequestPolicy.IsValidIncidentType(
            new string('A', IncidentRequestPolicy.MaximumIncidentTypeLength + 1)));

        var description = properties.Mapping("description");
        Assert.Equal(IncidentRequestPolicy.MaximumDescriptionLength, int.Parse(description.Scalar("maxLength")));
        Assert.Equal(1, int.Parse(description.Scalar("minLength")));
        Assert.False(IncidentRequestPolicy.IsValidDescription(
            new string('a', IncidentRequestPolicy.MaximumDescriptionLength + 1)));

        foreach (var evidence in new[]
                 {
                     properties.Mapping("evidence_proof_ids"),
                     Schema("Incident").Mapping("properties").Mapping("evidence_proof_ids"),
                 })
        {
            Assert.Equal("array", evidence.Scalar("type"));
            Assert.Equal("true", evidence.Scalar("uniqueItems"));
            Assert.Equal("uuid", evidence.Mapping("items").Scalar("format"));
            Assert.Equal(IncidentEvidencePolicy.MinimumEvidenceCount, int.Parse(evidence.Scalar("minItems")));
            Assert.Equal(IncidentEvidencePolicy.MaximumEvidenceCount, int.Parse(evidence.Scalar("maxItems")));
        }

        // Evidence is mandatory and bounded, so an empty or oversized list is not a valid opening.
        Assert.False(IncidentEvidencePolicy.IsAllowedCount(IncidentEvidencePolicy.MinimumEvidenceCount - 1));
        Assert.False(IncidentEvidencePolicy.IsAllowedCount(IncidentEvidencePolicy.MaximumEvidenceCount + 1));
        Assert.False(IncidentRequestPolicy.IsValidEvidence([]));

        Assert.Equal("date-time", properties.Mapping("occurred_at").Scalar("format"));
        Assert.Equal("date-time", Schema("Incident").Mapping("properties").Mapping("sla_due_at").Scalar("format"));
    }

    [Fact]
    public void AI05_conflict_codes_are_exactly_the_codes_the_endpoint_publishes()
    {
        var schema = Schema("IncidentConflictProblem");
        Assert.Equal("409", schema.Mapping("properties").Mapping("status").Scalar("const"));
        Assert.Contains("code", Required(schema), StringComparer.Ordinal);

        var published = schema.Mapping("properties").Mapping("code").Sequence("enum").Children
            .Cast<YamlScalarNode>()
            .Select(node => node.Value!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var source = EndpointSource();
        var switchStart = source.IndexOf("private static string PublicCode(string code) => code switch", StringComparison.Ordinal);
        Assert.True(switchStart >= 0, "The endpoint no longer maps conflicts through PublicCode.");
        var switchEnd = source.IndexOf("};", switchStart, StringComparison.Ordinal);
        var implemented = ConflictCodePattern()
            .Matches(source[switchStart..switchEnd])
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(implemented, published);

        // Internal rejection evidence must never reach the published problem document.
        Assert.Equal(
            "#/components/schemas/IncidentConflictProblem",
            Contract().Mapping("components").Mapping("responses").Mapping("IncidentConflict")
                .Mapping("content").Mapping("application/problem+json").Mapping("schema").Scalar("$ref"));
    }

    [Fact]
    public void Shape_validation_precedes_any_productive_incident_transaction()
    {
        Assert.Equal(
            "invalid-request-without-productive-transaction",
            OpenIncidentOperation().Scalar("x-shape-validation"));

        var service = ReadRepositoryFile(
            "src", "Modules", "Incidents", "Incidents.Infrastructure", "Incidents", "PostgreSqlIncidentService.cs");
        var guard = service.IndexOf("IncidentRequestPolicy.IsValidCommandShape(command)", StringComparison.Ordinal);
        var transaction = service.IndexOf("transactionContext.ExecuteAsync", StringComparison.Ordinal);
        Assert.True(guard >= 0 && transaction >= 0);
        Assert.True(guard < transaction, "Shape validation must reject before a tenant transaction is opened.");
    }

    private static YamlMappingNode Contract() =>
        YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));

    private static YamlMappingNode OpenIncidentOperation() =>
        Contract().Mapping("paths").Mapping(IncidentsPath).Mapping("post");

    private static YamlMappingNode Schema(string name) =>
        Contract().Mapping("components").Mapping("schemas").Mapping(name);

    private static string[] PropertyNames(YamlMappingNode schema) =>
        schema.Mapping("properties").Children.Keys
            .Cast<YamlScalarNode>()
            .Select(node => node.Value!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] Required(YamlMappingNode schema) =>
        schema.Sequence("required").Children
            .Cast<YamlScalarNode>()
            .Select(node => node.Value!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void AssertEnum(YamlMappingNode property, string[] expected)
    {
        Assert.Equal("string", property.Scalar("type"));
        var actual = property.Sequence("enum").Children.Cast<YamlScalarNode>().Select(node => node.Value!);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The wire names of a transport record, excluding the extension-data sink, which is the
    /// mechanism that rejects undeclared members rather than a published property.
    /// </summary>
    private static string[] JsonProperties<T>()
    {
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var undeclared = properties
            .Where(property => property.GetCustomAttribute<JsonPropertyNameAttribute>() is null)
            .ToArray();
        Assert.All(undeclared, property =>
            Assert.NotNull(property.GetCustomAttribute<JsonExtensionDataAttribute>()));

        return properties
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string EndpointSource() => ReadRepositoryFile(
        "src", "Modules", "Incidents", "Incidents.Endpoints", "IncidentEndpoints.cs");

    private static string ReadRepositoryFile(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryPaths.Root, .. segments]));

    [GeneratedRegex(@"\.Produces(?:Problem)?(?:<[^>]+>)?\(StatusCodes\.Status(\d{3})")]
    private static partial Regex ProducedStatusPattern();

    [GeneratedRegex("\"([A-Z][A-Z_]+)\"")]
    private static partial Regex ConflictCodePattern();
}
