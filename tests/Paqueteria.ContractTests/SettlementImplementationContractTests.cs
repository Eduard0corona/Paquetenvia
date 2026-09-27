using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using Finance.Application.Settlements;
using Finance.Domain.Settlements;
using Finance.Endpoints;
using Finance.Infrastructure.Financials;
using Finance.Infrastructure.Settlements;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Organizations.Application.Session;
using Paqueteria.Application.Tenancy;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// SET-001 Slice 2: the seven settlement operations as the real endpoint code maps them, against their
/// additive AI-05 publication. A drift on either side fails here instead of reaching a client.
/// </summary>
public sealed class SettlementImplementationContractTests
{
    private const string ApiPrefix = "/api/v1";

    private static readonly (string Method, string Path, string OperationId)[] Operations =
    [
        ("POST", "/settlements", "createSettlement"),
        ("GET", "/settlements/{settlementId}", "getSettlement"),
        ("POST", "/settlements/{settlementId}/adjustments", "addSettlementAdjustment"),
        ("POST", "/settlements/{settlementId}/approve", "approveSettlement"),
        ("POST", "/settlements/{settlementId}/pay", "markSettlementPaid"),
        ("POST", "/settlements/{settlementId}/void", "voidSettlement"),
        ("GET", "/settlements/{settlementId}/export.csv", "exportSettlementCsv"),
    ];

    private static readonly IReadOnlyDictionary<string, string> ProblemResponses = new Dictionary<string, string>
    {
        ["401"] = "Unauthorized",
        ["403"] = "Forbidden",
        ["404"] = "UniformNotFound",
        ["409"] = "SettlementConflict",
        ["503"] = "ServiceUnavailable",
    };

    [Fact]
    public void AI05_publishes_exactly_the_seven_settlement_operations_and_no_list()
    {
        var paths = OpenApi().Mapping("paths");
        var published = paths.Children
            .Where(path => ((YamlScalarNode)path.Key).Value!.Contains("settlement", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => ((YamlMappingNode)path.Value).Children
                .Where(operation => ((YamlScalarNode)operation.Key).Value != "parameters")
                .Select(operation => (
                    Method: ((YamlScalarNode)operation.Key).Value!.ToUpperInvariant(),
                    Path: ((YamlScalarNode)path.Key).Value!,
                    OperationId: ((YamlMappingNode)operation.Value).Scalar("operationId"))))
            .OrderBy(operation => operation.Path, StringComparer.Ordinal)
            .ThenBy(operation => operation.Method, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            Operations.OrderBy(operation => operation.Path, StringComparer.Ordinal)
                .ThenBy(operation => operation.Method, StringComparer.Ordinal),
            published);

        // listSettlements is deferred: the collection path only creates.
        Assert.Equal(["post"], Keys(paths.Mapping("/settlements")));
        Assert.DoesNotContain(published, operation => operation.OperationId == "listSettlements");
    }

    [Fact]
    public void Settlement_endpoints_and_their_AI05_operations_describe_the_same_surface()
    {
        var root = OpenApi();
        var endpoints = MappedSettlementEndpoints();
        Assert.Equal(
            Operations.Select(operation => operation.OperationId).Order(StringComparer.Ordinal),
            endpoints.Select(EndpointName).Order(StringComparer.Ordinal));

        foreach (var endpoint in endpoints)
        {
            var route = endpoint.RoutePattern.RawText!;
            Assert.StartsWith(ApiPrefix + "/", route, StringComparison.Ordinal);
            var method = Assert.Single(endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods);
            Assert.Contains((method, route[ApiPrefix.Length..], EndpointName(endpoint)), Operations);
            var operation = root.Mapping("paths").Mapping(route[ApiPrefix.Length..]).Mapping(method.ToLowerInvariant());
            Assert.Equal(EndpointName(endpoint), operation.Scalar("operationId"));
            Assert.Equal(["Finance"], Scalars(operation.Sequence("tags")));

            // Every mutation requires Idempotency-Key; reads never take one.
            var parameters = operation.Sequence("parameters").Children
                .Cast<YamlMappingNode>()
                .Select(parameter => Resolve(root, parameter))
                .ToArray();
            Assert.All(parameters, parameter => Assert.Equal("true", parameter.Scalar("required")));
            Assert.Equal(
                endpoint.RoutePattern.Parameters.Select(parameter => $"path:{parameter.Name}")
                    .Append("header:X-Organization-Id")
                    .Concat(method == "POST" ? ["header:Idempotency-Key"] : Array.Empty<string>())
                    .Order(StringComparer.Ordinal),
                parameters.Select(parameter => $"{parameter.Scalar("in")}:{parameter.Scalar("name")}")
                    .Order(StringComparer.Ordinal));
            Assert.All(
                parameters.Where(parameter => parameter.Scalar("in") == "path"),
                parameter => Assert.Equal("uuid", parameter.Mapping("schema").Scalar("format")));
            Assert.Equal(
                "capability-before-persisted-state",
                operation.Scalar("x-authorization-precedence").Replace(
                    "shape-validation-then-", string.Empty, StringComparison.Ordinal));

            var accepts = endpoint.Metadata.GetMetadata<IAcceptsMetadata>();
            if (accepts is null)
            {
                Assert.False(operation.Children.ContainsKey(new YamlScalarNode("requestBody")));
            }
            else
            {
                var body = operation.Mapping("requestBody");
                Assert.Equal("true", body.Scalar("required"));
                Assert.Equal(accepts.ContentTypes, Keys(body.Mapping("content")));
                AssertRequestDescribes(
                    Resolve(root, body.Mapping("content").Mapping("application/json").Mapping("schema")),
                    accepts.RequestType!);
            }

            var produced = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();
            var responses = operation.Mapping("responses");
            Assert.Equal(
                produced.Select(response => Status(response.StatusCode)).Order(StringComparer.Ordinal),
                Keys(responses).Order(StringComparer.Ordinal));
            foreach (var (status, component) in ProblemResponses)
            {
                Assert.Equal($"#/components/responses/{component}", responses.Mapping(status).Scalar("$ref"));
            }

            var success = Assert.Single(produced, response => response.StatusCode < 300);
            var content = responses.Mapping(Status(success.StatusCode)).Mapping("content");
            if (EndpointName(endpoint) == "exportSettlementCsv")
            {
                Assert.Equal(["text/csv"], success.ContentTypes);
                Assert.Equal(["text/csv"], Keys(content));
                Assert.Equal("string", content.Mapping("text/csv").Mapping("schema").Scalar("type"));
                continue;
            }

            Assert.Equal(["application/json"], Keys(content));
            Assert.Equal(typeof(SettlementResponse), success.Type);
            AssertSchemaDescribes(root, content.Mapping("application/json").Mapping("schema").Scalar("$ref"), success.Type!);
        }
    }

    [Fact]
    public void Settlement_success_statuses_are_201_for_creations_and_200_otherwise()
    {
        var statuses = MappedSettlementEndpoints().ToDictionary(
            EndpointName,
            endpoint => Assert.Single(
                endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>(),
                response => response.StatusCode < 300).StatusCode);

        Assert.Equal(
            new Dictionary<string, int>
            {
                ["createSettlement"] = 201,
                ["getSettlement"] = 200,
                ["addSettlementAdjustment"] = 201,
                ["approveSettlement"] = 200,
                ["markSettlementPaid"] = 200,
                ["voidSettlement"] = 200,
                ["exportSettlementCsv"] = 200,
            },
            statuses);
    }

    [Fact]
    public void Settlement_AI05_vocabularies_are_exactly_what_the_implementation_emits()
    {
        var schemas = OpenApi().Mapping("components").Mapping("schemas");
        var settlement = schemas.Mapping("Settlement").Mapping("properties");
        Assert.Equal(
            SettlementContractValues.AllStatuses.Select(status => status.ToContractValue()),
            Scalars(settlement.Mapping("status").Sequence("enum")));
        Assert.Equal(
            SettlementContractValues.AllPayeeTypes.Select(payee => payee.ToContractValue()),
            Scalars(settlement.Mapping("payee_type").Sequence("enum")));
        Assert.Equal(
            SettlementContractValues.AllLineTypes.Select(lineType => lineType.ToContractValue()),
            Scalars(schemas.Mapping("SettlementLine").Mapping("properties").Mapping("line_type").Sequence("enum")));

        // Reserved AI-06 vocabulary is never published: ALLY/BUSINESS payees and the source-less line types.
        foreach (var reserved in new[] { "ALLY", "BUSINESS", "ROUTE_BASE", "BONUS", "WAITING", "COD" })
        {
            Assert.DoesNotContain(reserved, Scalars(settlement.Mapping("payee_type").Sequence("enum")));
            Assert.DoesNotContain(
                reserved,
                Scalars(schemas.Mapping("SettlementLine").Mapping("properties").Mapping("line_type").Sequence("enum")));
        }

        var conflict = schemas.Mapping("SettlementConflictProblem").Mapping("properties");
        Assert.Equal("409", conflict.Mapping("status").Scalar("const"));
        Assert.Equal(
            SettlementEndpoints.PublicCodes.Order(StringComparer.Ordinal),
            Scalars(conflict.Mapping("code").Sequence("enum")).Order(StringComparer.Ordinal));
        Assert.Equal(
            SettlementEndpoints.PublicCodes.Order(StringComparer.Ordinal),
            Enum.GetValues<SettlementConflictCode>()
                .Select(SettlementEndpoints.PublicCode)
                .Distinct()
                .Order(StringComparer.Ordinal));

        // FIN-001's conflict vocabulary is untouched: settlements publish their own problem.
        Assert.DoesNotContain(
            "SETTLEMENT_STATE_CONFLICT",
            Scalars(schemas.Mapping("FinanceConflictProblem").Mapping("properties").Mapping("code").Sequence("enum")));
    }

    [Theory]
    [InlineData(SettlementConflictCode.InvalidRequest, "INVALID_REQUEST")]
    [InlineData(SettlementConflictCode.IdempotencyConflict, "CONFLICT")]
    [InlineData(SettlementConflictCode.InconsistentReplayEvidence, "CONFLICT")]
    [InlineData(SettlementConflictCode.SettlementStateConflict, "SETTLEMENT_STATE_CONFLICT")]
    [InlineData(SettlementConflictCode.CashPending, "CASH_PENDING")]
    [InlineData(SettlementConflictCode.IncidentPending, "INCIDENT_PENDING")]
    [InlineData(SettlementConflictCode.ClaimPending, "CLAIM_PENDING")]
    public void Settlement_conflicts_map_to_stable_public_codes(SettlementConflictCode code, string expected)
    {
        Assert.Equal(expected, SettlementEndpoints.PublicCode(code));
    }

    [Fact]
    public void Settlement_request_DTOs_match_their_AI05_schemas()
    {
        var schemas = OpenApi().Mapping("components").Mapping("schemas");

        var create = schemas.Mapping("CreateSettlementRequest").Mapping("properties");
        Assert.Equal(("string", "uuid"), (create.Mapping("driver_id").Scalar("type"), create.Mapping("driver_id").Scalar("format")));
        Assert.Equal(("string", "date"), (create.Mapping("period_from").Scalar("type"), create.Mapping("period_from").Scalar("format")));
        Assert.Equal(("string", "date"), (create.Mapping("period_to").Scalar("type"), create.Mapping("period_to").Scalar("format")));
        Assert.Contains(
            SettlementPeriod.MaximumDays.ToString(CultureInfo.InvariantCulture),
            create.Mapping("period_to").Scalar("description"),
            StringComparison.Ordinal);

        var adjustment = schemas.Mapping("AddSettlementAdjustmentRequest").Mapping("properties");
        var amount = adjustment.Mapping("amount_cents");
        Assert.Equal(("integer", "int64"), (amount.Scalar("type"), amount.Scalar("format")));
        Assert.Equal("0", amount.Mapping("not").Scalar("const"));
        Assert.False(amount.Children.ContainsKey(new YamlScalarNode("minimum")));
        AssertReasonSchema(adjustment.Mapping("reason"));
        AssertReasonSchema(schemas.Mapping("VoidSettlementRequest").Mapping("properties").Mapping("reason"));
    }

    [Fact]
    public void Settlement_response_DTOs_report_money_as_integer_cents_only()
    {
        AssertProperties<SettlementResponse>(
            "created_at", "id", "lines", "payee_id", "payee_type", "period_from", "period_to", "status",
            "total_cents");
        AssertProperties<SettlementLineResponse>(
            "amount_cents", "created_at", "id", "line_type", "order_id", "source_reference");
        foreach (var type in new[] { typeof(SettlementResponse), typeof(SettlementLineResponse) })
        {
            Assert.All(
                type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(property => Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType),
                propertyType =>
                {
                    Assert.NotEqual(typeof(float), propertyType);
                    Assert.NotEqual(typeof(double), propertyType);
                    Assert.NotEqual(typeof(decimal), propertyType);
                });
        }
    }

    [Fact]
    public void Settlement_mutations_use_distinct_idempotency_scopes()
    {
        string[] scopes =
        [
            PostgreSqlSettlementService.CreateIdempotencyScope,
            PostgreSqlSettlementService.AdjustIdempotencyScope,
            PostgreSqlSettlementService.ApproveIdempotencyScope,
            PostgreSqlSettlementService.PayIdempotencyScope,
            PostgreSqlSettlementService.VoidIdempotencyScope,
        ];
        Assert.Equal(
            [
                "SET-001:CREATE_SETTLEMENT", "SET-001:ADD_SETTLEMENT_ADJUSTMENT", "SET-001:APPROVE_SETTLEMENT",
                "SET-001:MARK_SETTLEMENT_PAID", "SET-001:VOID_SETTLEMENT",
            ],
            scopes);
        Assert.Equal(scopes.Length, scopes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Settlement_write_path_reuses_authoritative_sources_and_append_only_evidence()
    {
        var service = Read(
            "src", "Modules", "Finance", "Finance.Infrastructure", "Settlements", "PostgreSqlSettlementService.cs");
        var persistence = Read(
            "src", "Modules", "Finance", "Finance.Infrastructure", "Settlements",
            "PostgreSqlSettlementService.Persistence.cs");

        // Amounts come from the assignment cost FIN-001 already counts, under the same statuses.
        Assert.Equal(["ACCEPTED", "ACTIVE", "COMPLETED"], PostgreSqlOrderFinancialsService.CostBearingAssignmentStatuses);
        Assert.Contains("PostgreSqlOrderFinancialsService.CostBearingAssignmentStatuses", persistence, StringComparison.Ordinal);
        Assert.Contains("a.cost_cents", persistence, StringComparison.Ordinal);

        // The period is placed by the ORD-002 outcome event, never by mutable row timestamps.
        Assert.Contains("orders.order_events", persistence, StringComparison.Ordinal);
        Assert.Contains("e.event_type='ORDER_STATUS_CHANGED'", persistence, StringComparison.Ordinal);
        Assert.Contains("outcome.occurred_at >= @starts", persistence, StringComparison.Ordinal);
        Assert.Contains("outcome.occurred_at < @ends", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("updated_at", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("a.created_at", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("o.created_at", persistence, StringComparison.Ordinal);

        // Locking, idempotency and evidence follow the FIN-001 pattern; nothing is published to the outbox.
        Assert.Contains("pg_advisory_xact_lock", persistence, StringComparison.Ordinal);
        Assert.Contains(" FOR UPDATE", persistence, StringComparison.Ordinal);
        Assert.Contains("platform.idempotency_keys", persistence, StringComparison.Ordinal);
        Assert.Contains("IAppendOnlyAuditWriter", service, StringComparison.Ordinal);
        Assert.DoesNotContain("platform.outbox_events", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("platform.outbox_events", service, StringComparison.Ordinal);

        // Lines are only ever inserted: the ledger is append-only and SET-001 never deletes a settlement.
        Assert.DoesNotContain("UPDATE finance.settlement_lines", persistence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", persistence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", service, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["finance.settlement.adjusted", "finance.settlement.approved", "finance.settlement.calculated",
                "finance.settlement.paid", "finance.settlement.voided"],
            System.Text.RegularExpressions.Regex.Matches(service, "\"(finance\\.settlement\\.[a-z]+)\"")
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Settlement_surface_is_mapped_by_the_API()
    {
        var program = Read("src", "Paqueteria.Api", "Program.cs");
        Assert.Contains("app.MapSettlementEndpoints();", program, StringComparison.Ordinal);
        var endpoints = Read("src", "Modules", "Finance", "Finance.Endpoints", "SettlementEndpoints.cs");
        Assert.Equal(7, Count(endpoints, ".RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)"));
        Assert.Equal(7, Count(endpoints, ".RequireTenantContext(StatusCodes.Status403Forbidden)"));
        Assert.DoesNotContain("MapPut(", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPatch(", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", endpoints, StringComparison.Ordinal);
    }

    private static RouteEndpoint[] MappedSettlementEndpoints()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddScoped<IOrganizationRequestSession>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<ITenantContext>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<ISettlementService>(_ => throw new NotSupportedException());
        using var app = builder.Build();
        app.MapSettlementEndpoints();
        return
        [
            .. ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
        ];
    }

    private static void AssertReasonSchema(YamlMappingNode reason)
    {
        Assert.Equal("string", reason.Scalar("type"));
        Assert.Equal("1", reason.Scalar("minLength"));
        Assert.Equal(
            SettlementReasonPolicy.MaximumLength.ToString(CultureInfo.InvariantCulture),
            reason.Scalar("maxLength"));
        Assert.Equal(
            @"^[^\s\x00-\x1F\x7F-\x9F](?:[^\x00-\x1F\x7F-\x9F]*[^\s\x00-\x1F\x7F-\x9F])?$",
            reason.Scalar("pattern"));
    }

    /// <summary>
    /// A request DTO binds nullable members only to detect absent ones: every member is required, none is
    /// nullable, and any unknown member is rejected as INVALID_REQUEST.
    /// </summary>
    private static void AssertRequestDescribes(YamlMappingNode schema, Type dto)
    {
        Assert.Equal("object", schema.Scalar("type"));
        Assert.Equal("false", schema.Scalar("additionalProperties"));
        var properties = JsonProperties(dto);
        Assert.Equal(properties.Keys.Order(StringComparer.Ordinal), Keys(schema.Mapping("properties")).Order(StringComparer.Ordinal));
        Assert.Equal(properties.Keys.Order(StringComparer.Ordinal), Scalars(schema.Sequence("required")).Order(StringComparer.Ordinal));
        Assert.NotNull(dto.GetProperty("ExtensionData")?.GetCustomAttribute<JsonExtensionDataAttribute>());
    }

    /// <summary>
    /// A response DTO and its AI-05 schema agree property by property: names, required-ness, nullability,
    /// JSON type and format, recursively through arrays of nested objects.
    /// </summary>
    private static void AssertSchemaDescribes(YamlMappingNode root, string reference, Type dto)
    {
        var schema = Resolve(root, reference);
        Assert.Equal("object", schema.Scalar("type"));
        Assert.Equal("false", schema.Scalar("additionalProperties"));
        var properties = JsonProperties(dto);
        var declared = schema.Mapping("properties");
        Assert.Equal(properties.Keys.Order(StringComparer.Ordinal), Keys(declared).Order(StringComparer.Ordinal));
        Assert.Equal(properties.Keys.Order(StringComparer.Ordinal), Scalars(schema.Sequence("required")).Order(StringComparer.Ordinal));
        foreach (var (name, property) in properties)
        {
            var context = $"{dto.Name}.{name}";
            var declaredProperty = declared.Mapping(name);
            var type = Nullable.GetUnderlyingType(property.Type) ?? property.Type;
            var typeNode = declaredProperty.Required("type");
            string[] types = typeNode is YamlSequenceNode sequence
                ? [.. sequence.Children.Cast<YamlScalarNode>().Select(node => node.Value!)]
                : [Assert.IsType<YamlScalarNode>(typeNode).Value!];
            Assert.True(property.Nullable == types.Contains("null"), $"{context}: nullability differs from AI-05.");
            var jsonType = Assert.Single(types, value => value != "null");
            if (type == typeof(Guid))
            {
                Assert.Equal(("string", "uuid"), (jsonType, declaredProperty.Scalar("format")));
            }
            else if (type == typeof(string))
            {
                Assert.Equal("string", jsonType);
            }
            else if (type == typeof(long))
            {
                Assert.Equal(("integer", "int64"), (jsonType, declaredProperty.Scalar("format")));
            }
            else if (type == typeof(DateOnly))
            {
                Assert.Equal(("string", "date"), (jsonType, declaredProperty.Scalar("format")));
            }
            else if (type == typeof(DateTimeOffset))
            {
                Assert.Equal(("string", "date-time"), (jsonType, declaredProperty.Scalar("format")));
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            {
                Assert.Equal("array", jsonType);
                AssertSchemaDescribes(root, declaredProperty.Mapping("items").Scalar("$ref"), type.GetGenericArguments()[0]);
            }
            else
            {
                Assert.Fail($"{context}: no AI-05 mapping for {type}.");
            }
        }
    }

    private static Dictionary<string, DtoProperty> JsonProperties(Type dto)
    {
        var nullability = new NullabilityInfoContext();
        return dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
            .ToDictionary(
                property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name,
                property => new DtoProperty(
                    property.PropertyType,
                    Nullable.GetUnderlyingType(property.PropertyType) is not null ||
                    nullability.Create(property).ReadState == NullabilityState.Nullable),
                StringComparer.Ordinal);
    }

    private static YamlMappingNode Resolve(YamlMappingNode root, YamlMappingNode node) =>
        node.Children.ContainsKey(new YamlScalarNode("$ref")) ? Resolve(root, node.Scalar("$ref")) : node;

    private static YamlMappingNode Resolve(YamlMappingNode root, string reference)
    {
        Assert.StartsWith("#/", reference, StringComparison.Ordinal);
        var current = root;
        foreach (var segment in reference[2..].Split('/'))
        {
            current = current.Mapping(segment);
        }

        return current;
    }

    private static YamlMappingNode OpenApi() =>
        YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));

    private static string EndpointName(Endpoint endpoint) =>
        endpoint.Metadata.GetRequiredMetadata<IEndpointNameMetadata>().EndpointName;

    private static string Status(int statusCode) => statusCode.ToString(CultureInfo.InvariantCulture);

    private static string[] Keys(YamlMappingNode mapping) =>
        [.. mapping.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value!)];

    private static string[] Scalars(YamlSequenceNode sequence) =>
        [.. sequence.Children.Cast<YamlScalarNode>().Select(node => node.Value!)];

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryPaths.Root, .. segments]));

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static void AssertProperties<T>(params string[] expected)
    {
        var actual = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private sealed record DtoProperty(Type Type, bool Nullable);
}
