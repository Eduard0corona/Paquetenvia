using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Xml;
using Incidents.Application.Incidents;
using Incidents.Domain;
using Incidents.Endpoints;
using Incidents.Infrastructure;
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
    private const string ResolutionPath = "/incidents/{incidentId}/resolution";

    [Fact]
    public void Open_incident_operation_publishes_the_status_matrix_the_endpoint_implements()
    {
        var operation = OpenIncidentOperation();
        var declared = operation.Mapping("responses").Children.Keys
            .Cast<YamlScalarNode>()
            .Select(node => node.Value!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ProducedStatuses("openIncident"), declared);

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
            // openIncident's offline age refusal is answered by the endpoint itself, outside PublicCode.
            .Append(OfflineOperationAgePolicy.ExpiredCode)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(implemented, published);
        Assert.Contains(
            "return Conflict(OfflineOperationAgePolicy.ExpiredCode);",
            HandlerSource("OpenIncidentAsync"),
            StringComparison.Ordinal);
        // Resolution has no client timestamp and never answers the offline code.
        Assert.DoesNotContain("OfflineOperationAge", HandlerSource("ResolveIncidentAsync"), StringComparison.Ordinal);

        // Internal rejection evidence must never reach the published problem document.
        Assert.Equal(
            "#/components/schemas/IncidentConflictProblem",
            Contract().Mapping("components").Mapping("responses").Mapping("IncidentConflict")
                .Mapping("content").Mapping("application/problem+json").Mapping("schema").Scalar("$ref"));
    }

    /// <summary>
    /// OPS-003-INCIDENT-72H-UNIFICATION-CONFIGURABLE-2026-09-27: AI-05 lists openIncident in
    /// x-offline-operation-age with its own configurable limits, and every published value is the one
    /// the options, the domain bounds and the endpoint implement.
    /// </summary>
    [Fact]
    public void AI05_offline_operation_age_entry_for_openIncident_is_the_implemented_configurable_rule()
    {
        var age = Contract().Mapping("x-offline-operation-age");
        Assert.Contains(
            "OPS-003-INCIDENT-72H-UNIFICATION-CONFIGURABLE-2026-09-27",
            age.Sequence("decisions").Children.Cast<YamlScalarNode>().Select(node => node.Value!));
        // The shared rule for the other operations stays fixed.
        Assert.Equal("false", age.Scalar("maximum_age_configurable"));

        var entry = age.Mapping("operations").Mapping("openIncident");
        Assert.Equal(
            [
                "clock_ahead", "clock_tolerance_default", "clock_tolerance_range", "clock_tolerance_setting",
                "maximum_age_configurable", "maximum_age_default", "maximum_age_range", "maximum_age_setting",
                "missing_timestamp", "timestamp",
            ],
            entry.Children.Keys.Cast<YamlScalarNode>().Select(node => node.Value!).Order(StringComparer.Ordinal));
        Assert.Equal("occurred_at (required)", entry.Scalar("timestamp"));
        Assert.Contains("occurred_at", Required(Schema("OpenIncidentRequest")), StringComparer.Ordinal);
        Assert.Equal("true", entry.Scalar("maximum_age_configurable"));

        // Settings are the bound option names of the Incidents section.
        Assert.Equal(
            $"{IncidentsOptions.SectionName}:{nameof(IncidentsOptions.MaximumOccurrenceAgeHours)}",
            entry.Scalar("maximum_age_setting"));
        Assert.Equal(
            $"{IncidentsOptions.SectionName}:{nameof(IncidentsOptions.MaximumOccurrenceSkewMinutes)}",
            entry.Scalar("clock_tolerance_setting"));

        // Defaults are the MVP-1 policy and the unconfigured options.
        var defaults = new IncidentsOptions();
        Assert.Equal(IncidentOperationalPolicy.Mvp1, defaults.OperationalPolicy);
        Assert.Equal(IncidentOccurrenceAgePolicy.Mvp1.MaximumAge, XmlConvert.ToTimeSpan(entry.Scalar("maximum_age_default")));
        Assert.Equal(TimeSpan.FromHours(defaults.MaximumOccurrenceAgeHours), IncidentOccurrenceAgePolicy.Mvp1.MaximumAge);
        Assert.Equal(IncidentOccurrenceAgePolicy.Mvp1.ClockTolerance, XmlConvert.ToTimeSpan(entry.Scalar("clock_tolerance_default")));
        Assert.Equal(TimeSpan.FromMinutes(defaults.MaximumOccurrenceSkewMinutes), IncidentOccurrenceAgePolicy.Mvp1.ClockTolerance);

        // Ranges are exactly what the validated options accept, in their own units.
        var (minimumAge, maximumAge) = Range(entry.Scalar("maximum_age_range"));
        Assert.Equal(TimeSpan.FromHours(1), minimumAge);
        Assert.Equal(IncidentOperationalPolicy.LongestConfigurableWindow, maximumAge);
        Assert.True(WithAge((int)minimumAge.TotalHours).OperationalPolicy.IsValid);
        Assert.False(WithAge((int)minimumAge.TotalHours - 1).OperationalPolicy.IsValid);
        Assert.True(WithAge((int)maximumAge.TotalHours).OperationalPolicy.IsValid);
        Assert.False(WithAge((int)maximumAge.TotalHours + 1).OperationalPolicy.IsValid);

        var (minimumSkew, maximumSkew) = Range(entry.Scalar("clock_tolerance_range"));
        Assert.Equal(TimeSpan.Zero, minimumSkew);
        Assert.Equal(IncidentOperationalPolicy.LongestConfigurableSkew, maximumSkew);
        Assert.True(WithSkew((int)minimumSkew.TotalMinutes).OperationalPolicy.IsValid);
        Assert.False(WithSkew((int)minimumSkew.TotalMinutes - 1).OperationalPolicy.IsValid);
        Assert.True(WithSkew((int)maximumSkew.TotalMinutes).OperationalPolicy.IsValid);
        Assert.False(WithSkew((int)maximumSkew.TotalMinutes + 1).OperationalPolicy.IsValid);

        // A clock ahead of the tolerance and a missing timestamp stay the invalid request they were.
        Assert.Equal("409 INVALID_REQUEST", entry.Scalar("clock_ahead"));
        Assert.Equal("409 INVALID_REQUEST", entry.Scalar("missing_timestamp"));
        var handler = HandlerSource("OpenIncidentAsync");
        Assert.Contains("request.OccurredAt is not { } occurredAt", handler, StringComparison.Ordinal);
        Assert.Contains(
            "case OfflineOperationAge.AheadOfServerClock:\n                return Conflict(\"INVALID_REQUEST\");",
            handler.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);

        // The rule runs in the endpoint before the incident service, which owns the idempotency replay.
        var evaluation = handler.IndexOf("occurrencePolicy.Evaluate(occurredAt, clock.UtcNow)", StringComparison.Ordinal);
        var service = handler.IndexOf("service.OpenAsync(", StringComparison.Ordinal);
        Assert.True(evaluation >= 0 && service >= 0 && evaluation < service);
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

    [Fact]
    public void Incident_routes_are_exactly_the_AI05_incident_operations()
    {
        var implemented = EndpointChains()
            .Select(chain =>
            {
                var route = MappedRoutePattern().Match(chain);
                var name = OperationNamePattern().Match(chain);
                Assert.True(route.Success && name.Success, "Every incident route must be named.");
                return $"{route.Groups["method"].Value.ToUpperInvariant()} {route.Groups["path"].Value} {name.Groups[1].Value}";
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

        var published = Contract().Mapping("paths").Children
            .SelectMany(path => ((YamlMappingNode)path.Value).Children
                .Where(operation => operation.Value is YamlMappingNode mapping &&
                    mapping.Children.ContainsKey(new YamlScalarNode("tags")) &&
                    mapping.Sequence("tags").Children.Cast<YamlScalarNode>()
                        .Any(tag => tag.Value == "Incidents"))
                .Select(operation =>
                    $"{((YamlScalarNode)operation.Key).Value!.ToUpperInvariant()} " +
                    $"{((YamlScalarNode)path.Key).Value} " +
                    $"{((YamlMappingNode)operation.Value).Scalar("operationId")}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Opening and resolving are the whole incident surface: no list, get, delete or reopen.
        Assert.Equal(
            [$"POST {ResolutionPath} resolveIncident", $"POST {IncidentsPath} openIncident"],
            published);
        Assert.Equal(published, implemented);
    }

    [Fact]
    public void Resolve_incident_operation_publishes_the_status_matrix_the_endpoint_implements()
    {
        var operation = ResolveIncidentOperation();
        var declared = operation.Mapping("responses").Children.Keys
            .Cast<YamlScalarNode>()
            .Select(node => node.Value!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["200", "401", "403", "404", "409", "503"], declared);
        Assert.Equal(ProducedStatuses("resolveIncident"), declared);

        var responses = operation.Mapping("responses");
        Assert.Equal(
            "#/components/schemas/Incident",
            responses.Mapping("200").Mapping("content")
                .Mapping("application/json").Mapping("schema").Scalar("$ref"));
        Assert.Equal("#/components/responses/Unauthorized", responses.Mapping("401").Scalar("$ref"));
        Assert.Equal("#/components/responses/Forbidden", responses.Mapping("403").Scalar("$ref"));
        Assert.Equal("#/components/responses/UniformNotFound", responses.Mapping("404").Scalar("$ref"));
        Assert.Equal("#/components/responses/IncidentConflict", responses.Mapping("409").Scalar("$ref"));
        Assert.Equal("#/components/responses/ServiceUnavailable", responses.Mapping("503").Scalar("$ref"));

        // The success body is the existing Incident representation, answered as 200 by the endpoint.
        var chain = EndpointChain("resolveIncident");
        Assert.Contains(".Produces<IncidentResponse>(StatusCodes.Status200OK)", chain, StringComparison.Ordinal);
        Assert.Contains(
            "return Results.Ok(ToResponse(result));",
            HandlerSource("ResolveIncidentAsync"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Resolving_requires_the_organization_context_the_incident_id_and_the_idempotency_key()
    {
        var parameters = ResolveIncidentOperation().Sequence("parameters").Children
            .Cast<YamlMappingNode>()
            .Select(parameter => parameter.Scalar("$ref"))
            .ToArray();
        Assert.Equal(
            [
                "#/components/parameters/OrganizationContext",
                "#/components/parameters/IncidentId",
                "#/components/parameters/IdempotencyKey",
            ],
            parameters);

        var incidentId = Contract().Mapping("components").Mapping("parameters").Mapping("IncidentId");
        Assert.Equal("incidentId", incidentId.Scalar("name"));
        Assert.Equal("path", incidentId.Scalar("in"));
        Assert.Equal("true", incidentId.Scalar("required"));
        Assert.Equal("uuid", incidentId.Mapping("schema").Scalar("format"));

        // The tenant context and a single well-formed key are enforced before the service runs, and
        // the identifier is parsed in its canonical form only.
        var chain = EndpointChain("resolveIncident");
        Assert.Contains("\"/api/v1/incidents/{incidentId}/resolution\"", chain, StringComparison.Ordinal);
        Assert.Contains(".RequireTenantContext(StatusCodes.Status403Forbidden)", chain, StringComparison.Ordinal);
        Assert.Contains("TryReadContext(", HandlerSource("ResolveIncidentAsync"), StringComparison.Ordinal);
        var source = EndpointSource();
        Assert.Contains(
            "Guid.TryParseExact(resourceId, \"D\", out parsedResourceId)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("values.Count == 1", source, StringComparison.Ordinal);
        Assert.Contains("IdempotencyKeyPolicy.IsValid(values[0])", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AI05_resolution_request_is_exactly_the_implemented_ResolveIncidentRequest()
    {
        var schema = Schema("ResolveIncidentRequest");
        var transport = JsonProperties<ResolveIncidentRequest>();

        Assert.Equal(["outcome", "reason"], transport);
        Assert.Equal(transport, PropertyNames(schema));
        Assert.Equal(transport, Required(schema));
        Assert.Equal("false", schema.Scalar("additionalProperties"));
        Assert.Equal(
            "#/components/schemas/ResolveIncidentRequest",
            ResolveIncidentOperation().Mapping("requestBody").Mapping("content")
                .Mapping("application/json").Mapping("schema").Scalar("$ref"));

        // The closed schema is enforced by the resolution handler itself, not merely documented.
        Assert.Contains(
            "request.ExtensionData is { Count: > 0 }",
            HandlerSource("ResolveIncidentAsync"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AI05_resolution_outcome_is_exactly_the_terminal_incident_vocabulary()
    {
        var outcomes = Enum.GetValues<IncidentResolutionOutcome>()
            .Select(value => value.ToContractValue())
            .ToArray();
        Assert.Equal([IncidentContract.Resolved, IncidentContract.Rejected], outcomes);

        AssertEnum(Schema("ResolveIncidentRequest").Mapping("properties").Mapping("outcome"), outcomes);
        Assert.All(outcomes, value => Assert.True(IncidentContract.TryParseResolutionOutcome(value, out _)));
        Assert.False(IncidentContract.TryParseResolutionOutcome(IncidentContract.Open, out _));
        Assert.False(IncidentContract.TryParseResolutionOutcome(IncidentContract.Investigating, out _));
    }

    [Theory]
    [InlineData("Reprogramado con el cliente.")]
    [InlineData("x")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData(" lead")]
    [InlineData("line\nbreak")]
    [InlineData("tab\tinside")]
    [InlineData("nul\u0000inside")]
    [InlineData("c1\u0085inside")]
    public void AI05_reason_pattern_accepts_exactly_what_the_reason_policy_accepts(string sample)
    {
        var reason = Schema("ResolveIncidentRequest").Mapping("properties").Mapping("reason");
        Assert.Equal(
            IncidentRequestPolicy.IsValidResolutionReason(sample),
            Regex.IsMatch(sample, reason.Scalar("pattern")));
    }

    [Fact]
    public void AI05_reason_bounds_are_exactly_the_implemented_policy()
    {
        var reason = Schema("ResolveIncidentRequest").Mapping("properties").Mapping("reason");
        Assert.Equal("string", reason.Scalar("type"));
        Assert.Equal(1, int.Parse(reason.Scalar("minLength")));
        Assert.Equal(IncidentRequestPolicy.MaximumResolutionReasonLength, int.Parse(reason.Scalar("maxLength")));
        Assert.True(IncidentRequestPolicy.IsValidResolutionReason(
            new string('a', IncidentRequestPolicy.MaximumResolutionReasonLength)));
        Assert.False(IncidentRequestPolicy.IsValidResolutionReason(
            new string('a', IncidentRequestPolicy.MaximumResolutionReasonLength + 1)));
    }

    [Fact]
    public void Resolution_settles_shape_then_capability_before_any_persisted_incident_state()
    {
        var operation = ResolveIncidentOperation();
        Assert.Equal(
            "shape-validation-then-capability-before-persisted-state",
            operation.Scalar("x-authorization-precedence"));
        Assert.Equal("invalid-request-without-productive-transaction", operation.Scalar("x-shape-validation"));
        Assert.Equal(
            ["idempotency_lock", "idempotency_record", "replay_evidence", "incident"],
            operation.Sequence("x-capability-protected-state").Children
                .Cast<YamlScalarNode>()
                .Select(node => node.Value!)
                .ToArray());

        var service = ReadRepositoryFile(
            "src", "Modules", "Incidents", "Incidents.Infrastructure", "Incidents",
            "PostgreSqlIncidentService.Resolution.cs");
        int[] order =
        [
            service.IndexOf("IncidentRequestPolicy.IsValidResolveCommandShape(command)", StringComparison.Ordinal),
            service.IndexOf("transactionContext.ExecuteAsync", StringComparison.Ordinal),
            service.IndexOf("IncidentsSql.ReadResolutionCapabilityAsync", StringComparison.Ordinal),
            service.IndexOf("IncidentsSql.AcquireIdempotencyLockAsync", StringComparison.Ordinal),
            service.IndexOf("await ReadResolutionReplayAsync", StringComparison.Ordinal),
            service.IndexOf("IncidentsSql.ReadIncidentForUpdateAsync", StringComparison.Ordinal),
            service.IndexOf("IncidentResolutionPolicy.CanResolve", StringComparison.Ordinal),
            service.IndexOf("await CloseIncidentAsync", StringComparison.Ordinal),
            service.IndexOf("await WriteResolutionAuditAsync", StringComparison.Ordinal),
            service.IndexOf("await CompleteReservationAsync", StringComparison.Ordinal),
        ];
        Assert.All(order, index => Assert.True(index >= 0));
        Assert.Equal(order.Order().ToArray(), order);

        // Resolution never writes the order the incident belongs to, nor anything outside incidents.
        Assert.DoesNotMatch(@"(?i)\b(?:UPDATE|INSERT\s+INTO|DELETE\s+FROM)\s+orders\.", service);
        Assert.Equal(
            ["UPDATE incidents.incidents"],
            Regex.Matches(service, @"\b(?:UPDATE|INSERT\s+INTO|DELETE\s+FROM)\s+[a-z_]+\.[a-z_]+")
                .Select(match => match.Value)
                .ToArray());
    }

    private static YamlMappingNode Contract() =>
        YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));

    private static YamlMappingNode OpenIncidentOperation() =>
        Contract().Mapping("paths").Mapping(IncidentsPath).Mapping("post");

    private static YamlMappingNode ResolveIncidentOperation() =>
        Contract().Mapping("paths").Mapping(ResolutionPath).Mapping("post");

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

    private static (TimeSpan Minimum, TimeSpan Maximum) Range(string value)
    {
        var bounds = value.Split("..", StringSplitOptions.None);
        Assert.Equal(2, bounds.Length);
        return (XmlConvert.ToTimeSpan(bounds[0]), XmlConvert.ToTimeSpan(bounds[1]));
    }

    private static IncidentsOptions WithAge(int hours) => new() { MaximumOccurrenceAgeHours = hours };

    private static IncidentsOptions WithSkew(int minutes) => new() { MaximumOccurrenceSkewMinutes = minutes };

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

    /// <summary>Every route registration of the endpoint source, from the map call to its terminator.</summary>
    private static string[] EndpointChains()
    {
        var source = EndpointSource();
        return MappedRoutePattern().Matches(source)
            .Select(match => source[match.Index..source.IndexOf(';', match.Index)])
            .ToArray();
    }

    private static string EndpointChain(string operationId) =>
        Assert.Single(
            EndpointChains(),
            chain => chain.Contains($".WithName(\"{operationId}\")", StringComparison.Ordinal));

    /// <summary>The statuses one operation's own route registration declares, never another's.</summary>
    private static string[] ProducedStatuses(string operationId) =>
        ProducedStatusPattern()
            .Matches(EndpointChain(operationId))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string HandlerSource(string handlerName)
    {
        var source = EndpointSource();
        var start = source.IndexOf($"private static async Task<IResult> {handlerName}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"The endpoint no longer declares {handlerName}.");
        var end = source.IndexOf("\n    private static ", start + 1, StringComparison.Ordinal);
        return source[start..end];
    }

    private static string ReadRepositoryFile(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryPaths.Root, .. segments]));

    [GeneratedRegex(@"\.Produces(?:Problem)?(?:<[^>]+>)?\(StatusCodes\.Status(\d{3})")]
    private static partial Regex ProducedStatusPattern();

    [GeneratedRegex("\"([A-Z][A-Z_]+)\"")]
    private static partial Regex ConflictCodePattern();

    [GeneratedRegex(@"endpoints\.Map(?<method>Get|Post|Put|Patch|Delete)\(""/api/v1(?<path>[^""]+)""")]
    private static partial Regex MappedRoutePattern();

    [GeneratedRegex(@"\.WithName\(""([A-Za-z]+)""\)")]
    private static partial Regex OperationNamePattern();
}
