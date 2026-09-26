using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Finance.Application;
using Finance.Application.Cod;
using Finance.Application.Financials;
using Finance.Domain;
using Finance.Endpoints;
using Finance.Infrastructure.Cod;
using Finance.Infrastructure.Financials;
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

public sealed class FinanceImplementationContractTests
{
    private const string ApiPrefix = "/api/v1";

    private static readonly IReadOnlyDictionary<string, string> ProblemResponses = new Dictionary<string, string>
    {
        ["401"] = "Unauthorized",
        ["403"] = "Forbidden",
        ["404"] = "UniformNotFound",
        ["409"] = "FinanceConflict",
        ["503"] = "ServiceUnavailable",
    };

    /// <summary>
    /// The four FIN-001 operations as the real endpoint code maps them, against AI-05: path, method,
    /// operationId, tag, parameters, request body, the complete response status matrix, the shared response
    /// each problem status resolves to, and the success schema property by property. A change on either
    /// side fails here instead of drifting silently.
    /// </summary>
    [Fact]
    public void Finance_endpoints_and_their_AI05_operations_describe_the_same_surface()
    {
        var root = OpenApi();
        var endpoints = MappedFinanceEndpoints();
        Assert.Equal(
            ["getOrderFinancials", "getRouteFinancials", "reconcileCod", "recordCodCollection"],
            endpoints.Select(EndpointName).Order(StringComparer.Ordinal));

        foreach (var endpoint in endpoints)
        {
            var route = endpoint.RoutePattern.RawText!;
            Assert.StartsWith(ApiPrefix + "/", route, StringComparison.Ordinal);
            var method = Assert.Single(endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods);
            var operation = root.Mapping("paths").Mapping(route[ApiPrefix.Length..]).Mapping(method.ToLowerInvariant());
            Assert.Equal(EndpointName(endpoint), operation.Scalar("operationId"));
            Assert.Equal(["Finance"], Scalars(operation.Sequence("tags")));

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
                Assert.Equal(typeof(RecordCodRequest), accepts.RequestType);
                AssertRecordCodRequestSchema(
                    Resolve(root, body.Mapping("content").Mapping("application/json").Mapping("schema")));
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
            var schema = responses.Mapping(Status(success.StatusCode))
                .Mapping("content").Mapping("application/json").Mapping("schema");
            AssertSchemaDescribes(root, schema.Scalar("$ref"), success.Type!);
        }
    }

    /// <summary>Every vocabulary AI-05 publishes for Finance is exactly the one the implementation emits.</summary>
    [Fact]
    public void Finance_AI05_vocabularies_are_exactly_what_the_implementation_emits()
    {
        var schemas = OpenApi().Mapping("components").Mapping("schemas");
        string[] codStatuses = [.. Enum.GetValues<CodStatus>().Select(status => status.ToContractValue())];

        Assert.Equal(
            FinanceContractValues.AllModalities.Select(modality => modality.ToContractValue()),
            Scalars(schemas.Mapping("ModalityCost").Mapping("properties").Mapping("modality").Sequence("enum")));
        Assert.Equal(
            codStatuses,
            Scalars(schemas.Mapping("CodTransaction").Mapping("properties").Mapping("status").Sequence("enum")));

        // A COD position has no status until a collection exists, so its enum admits the null value itself.
        var positionStatuses = schemas.Mapping("CodPosition").Mapping("properties").Mapping("status").Sequence("enum")
            .Children.Cast<YamlScalarNode>().ToArray();
        Assert.Equal([.. codStatuses, "null"], positionStatuses.Select(node => node.Value));
        Assert.Equal(YamlDotNet.Core.ScalarStyle.Plain, positionStatuses[^1].Style);

        var conflict = schemas.Mapping("FinanceConflictProblem").Mapping("properties");
        Assert.Equal("409", conflict.Mapping("status").Scalar("const"));
        Assert.Equal(
            Enum.GetValues<FinanceConflictCode>()
                .Select(FinanceEndpointBinding.PublicCode)
                .Append("INVALID_REQUEST")
                .Distinct()
                .Order(StringComparer.Ordinal),
            Scalars(conflict.Mapping("code").Sequence("enum")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Two_normative_finance_operations_are_mapped_with_mandatory_idempotency()
    {
        var source = Read("src", "Modules", "Finance", "Finance.Endpoints", "CodEndpoints.cs");

        Assert.Contains(
            "MapPost(\"/api/v1/orders/{orderId}/cod-records\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MapPost(\"/api/v1/cod-records/{codId}/reconcile\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(".WithName(\"recordCodCollection\")", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"reconcileCod\")", source, StringComparison.Ordinal);
        Assert.Equal(2, Count(source, "MapPost("));
        Assert.Equal(2, Count(source, "TryReadIdempotencyKey"));
        Assert.DoesNotContain("MapGet(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPut(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPatch(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Financials_surface_is_read_only_and_tenant_scoped()
    {
        var source = Read("src", "Modules", "Finance", "Finance.Endpoints", "OrderFinancialsEndpoints.cs");

        Assert.Contains("MapGet(\"/api/v1/orders/{orderId}/financials\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/routes/{routeId}/financials\"", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"getOrderFinancials\")", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"getRouteFinancials\")", source, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Count(source, ".RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)"));
        Assert.Equal(2, Count(source, ".RequireTenantContext(StatusCodes.Status403Forbidden)"));
        Assert.DoesNotContain("TryReadIdempotencyKey", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPost(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPut(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPatch(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Cod_DTOs_match_the_exact_AI05_surface()
    {
        AssertProperties<RecordCodRequest>("amount_cents", "reference");
        AssertProperties<CodTransactionResponse>(
            "amount_cents", "id", "order_id", "recorded_at", "reconciled_at", "status");
    }

    [Fact]
    public void Financials_DTOs_report_money_as_integer_cents_only()
    {
        AssertProperties<ModalityCostResponse>("assignment_count", "cost_cents", "modality");
        AssertProperties<CodPositionResponse>(
            "amount_cents", "expected_cents", "reconciled", "recorded", "satisfies_close_requirement",
            "satisfies_delivery_requirement", "status");
        AssertProperties<OrderFinancialsResponse>(
            "cod", "cost_by_modality", "cost_cents", "currency", "margin_basis_points", "margin_cents",
            "order_id", "order_status", "revenue_cents");
        AssertProperties<RouteOrderFinancialsResponse>(
            "cod", "cost_by_modality", "cost_cents", "margin_basis_points", "margin_cents", "order_id",
            "revenue_cents");
        AssertProperties<RouteFinancialsResponse>(
            "cod_expected_cents_total", "cod_pending_reconciliation_cents_total",
            "cod_pending_reconciliation_count", "cost_by_modality", "cost_cents_total", "currency",
            "margin_basis_points", "margin_cents_total", "order_count", "orders", "revenue_cents_total",
            "route_id", "route_status");

        foreach (var type in new[]
        {
            typeof(CodTransactionResponse), typeof(ModalityCostResponse), typeof(CodPositionResponse),
            typeof(OrderFinancialsResponse), typeof(RouteOrderFinancialsResponse),
            typeof(RouteFinancialsResponse),
        })
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
    public void Cod_write_path_keeps_REST_authority_tenant_locking_and_append_only_evidence()
    {
        var service = Read(
            "src", "Modules", "Finance", "Finance.Infrastructure", "Cod", "PostgreSqlCodTransactionService.cs");
        var persistence = Read(
            "src", "Modules", "Finance", "Finance.Infrastructure", "Cod",
            "PostgreSqlCodTransactionService.Persistence.cs");
        var gateway = Read(
            "src", "Modules", "Finance", "Finance.Infrastructure", "Persistence", "FinanceTenantGateway.cs");

        Assert.Equal("FIN-001:RECORD_COD", PostgreSqlCodTransactionService.RecordIdempotencyScope);
        Assert.Equal("FIN-001:RECONCILE_COD", PostgreSqlCodTransactionService.ReconcileIdempotencyScope);
        Assert.Contains("pg_advisory_xact_lock", gateway, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock", persistence, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", service, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", persistence, StringComparison.Ordinal);
        Assert.Contains("platform.idempotency_keys", persistence, StringComparison.Ordinal);
        Assert.Contains("finance.cod_transactions", persistence, StringComparison.Ordinal);

        // COD evidence is the append-only audit log. No outbox event is published, because an unrouted
        // topic would be claimed as UNOWNED and settled DEAD with UNKNOWN_TOPIC.
        Assert.Contains("IAppendOnlyAuditWriter", service, StringComparison.Ordinal);
        Assert.Contains("platform.audit_logs", ReadAuditWriter(), StringComparison.Ordinal);
        Assert.DoesNotContain("platform.outbox_events", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("platform.outbox_events", service, StringComparison.Ordinal);

        // The COD row is created directly as RECORDED: the expectation itself lives on the order, so
        // FIN-001 never duplicates it as a separate EXPECTED row.
        Assert.Contains("'RECORDED'", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("'EXPECTED'", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("RETURNING", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("finance.settlement", service, StringComparison.Ordinal);
        Assert.DoesNotContain("finance.settlement", persistence, StringComparison.Ordinal);
    }

    [Fact]
    public void Financials_read_path_derives_money_and_never_writes()
    {
        var source = Read(
            "src", "Modules", "Finance", "Finance.Infrastructure", "Financials",
            "PostgreSqlOrderFinancialsService.cs");

        Assert.Equal(
            ["ACCEPTED", "ACTIVE", "COMPLETED"],
            PostgreSqlOrderFinancialsService.CostBearingAssignmentStatuses);
        Assert.Contains("orders.orders", source, StringComparison.Ordinal);
        Assert.Contains("dispatch.assignments", source, StringComparison.Ordinal);
        Assert.Contains("routes.route_stops", source, StringComparison.Ordinal);
        Assert.Contains("finance.cod_transactions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", source, StringComparison.Ordinal);
        Assert.DoesNotContain("finance.settlement", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The FIN-001 implementation areas, listed one by one so that none of them can disappear from the
    /// scan unnoticed. SET-001 lives in the same module, but none of these sources may acquire settlement
    /// behavior.
    /// </summary>
    private static readonly string[] Fin001Sources =
    [
        "src/Modules/Finance/Finance.Application/Cod",
        "src/Modules/Finance/Finance.Application/Financials",
        "src/Modules/Finance/Finance.Application/FinanceContracts.cs",
        "src/Modules/Finance/Finance.Domain/CodLifecyclePolicy.cs",
        "src/Modules/Finance/Finance.Domain/FinanceEnums.cs",
        "src/Modules/Finance/Finance.Domain/MoneyCents.cs",
        "src/Modules/Finance/Finance.Domain/UnitEconomics.cs",
        "src/Modules/Finance/Finance.Endpoints/CodEndpoints.cs",
        "src/Modules/Finance/Finance.Endpoints/FinanceEndpointBinding.cs",
        "src/Modules/Finance/Finance.Endpoints/OrderFinancialsEndpoints.cs",
        "src/Modules/Finance/Finance.Infrastructure/Cod",
        "src/Modules/Finance/Finance.Infrastructure/Financials",
    ];

    /// <summary>
    /// The only production code that may name the settlement tables: the Slice 1 ledger migration and the
    /// Slice 2 settlement persistence. Later slices extend this list on purpose.
    /// </summary>
    private static readonly string[] Set001SettlementTableSources =
    [
        "src/Modules/Finance/Finance.Infrastructure/Persistence/Migrations/20260925000100_EnforceSettlementLedgerIntegrity.cs",
        "src/Modules/Finance/Finance.Infrastructure/Settlements/PostgreSqlSettlementService.Persistence.cs",
    ];

    /// <summary>
    /// Every Finance source that may mention settlements at all: the ledger migration, the SET-001
    /// domain, application, infrastructure and endpoint files, and the one composition line that
    /// registers the settlement service. COD, Financials and every other FIN-001 source stay out.
    /// </summary>
    private static readonly string[] Set001SettlementSources =
    [
        "src/Modules/Finance/Finance.Application/Settlements/SettlementContracts.cs",
        "src/Modules/Finance/Finance.Application/Settlements/SettlementCsvWriter.cs",
        "src/Modules/Finance/Finance.Domain/Settlements/SettlementModel.cs",
        "src/Modules/Finance/Finance.Domain/Settlements/SettlementPeriod.cs",
        "src/Modules/Finance/Finance.Domain/Settlements/SettlementSourcePolicy.cs",
        "src/Modules/Finance/Finance.Endpoints/SettlementEndpoints.cs",
        "src/Modules/Finance/Finance.Infrastructure/DependencyInjection.cs",
        "src/Modules/Finance/Finance.Infrastructure/Persistence/Migrations/20260925000100_EnforceSettlementLedgerIntegrity.cs",
        "src/Modules/Finance/Finance.Infrastructure/Settlements/DisabledSettlementService.cs",
        "src/Modules/Finance/Finance.Infrastructure/Settlements/PostgreSqlSettlementService.Persistence.cs",
        "src/Modules/Finance/Finance.Infrastructure/Settlements/PostgreSqlSettlementService.cs",
    ];

    [Fact]
    public void Fin001_sources_remain_settlement_free()
    {
        var sources = Fin001Sources
            .SelectMany(relative =>
            {
                var path = Path.Combine([RepositoryPaths.Root, .. relative.Split('/')]);
                if (Directory.Exists(path))
                {
                    var files = Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories);
                    Assert.NotEmpty(files);
                    return files;
                }

                Assert.True(File.Exists(path), $"FIN-001 source {relative} is missing.");
                return [path];
            })
            .ToArray();

        var offenders = sources
            .Where(path => File.ReadAllText(path).Contains("settlement", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/'))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Settlement_persistence_is_owned_by_the_SET001_settlement_slices()
    {
        var production = Directory
            .GetFiles(Path.Combine(RepositoryPaths.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .ToArray();
        string Relative(string path) => Path.GetRelativePath(RepositoryPaths.Root, path).Replace('\\', '/');

        // Every production reference to the settlement tables belongs to SET-001, and SET-001 references them.
        var settlementTables = new Regex(@"\bfinance\.settlement(s|_lines)\b", RegexOptions.IgnoreCase);
        Assert.Equal(
            Set001SettlementTableSources,
            production
                .Where(path => settlementTables.IsMatch(File.ReadAllText(path)))
                .Select(Relative)
                .Order(StringComparer.Ordinal));

        // Inside Finance, no other source even mentions settlements, so SET-001 cannot leak into FIN-001
        // through a shared helper, a contract or an endpoint.
        var financeRoot = Path.Combine(RepositoryPaths.Root, "src", "Modules", "Finance") + Path.DirectorySeparatorChar;
        Assert.Equal(
            Set001SettlementSources,
            production
                .Where(path => path.StartsWith(financeRoot, StringComparison.Ordinal))
                .Where(path => File.ReadAllText(path).Contains("settlement", StringComparison.OrdinalIgnoreCase))
                .Select(Relative)
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The Finance endpoints exactly as production maps them. Only the services their handlers bind are
    /// registered, so parameter inference matches the real host; none is ever resolved.
    /// </summary>
    private static RouteEndpoint[] MappedFinanceEndpoints()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddScoped<IOrganizationRequestSession>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<ITenantContext>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<ICodTransactionService>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IOrderFinancialsService>(_ => throw new NotSupportedException());
        using var app = builder.Build();
        app.MapCodEndpoints();
        app.MapFinancialsEndpoints();
        return
        [
            .. ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
        ];
    }

    private static void AssertRecordCodRequestSchema(YamlMappingNode schema)
    {
        // The request DTO binds nullable members only to detect absent ones; both are required and non-null,
        // and any unknown member is rejected as INVALID_REQUEST.
        Assert.Equal("false", schema.Scalar("additionalProperties"));
        Assert.Equal(
            JsonProperties(typeof(RecordCodRequest)).Keys.Order(StringComparer.Ordinal),
            Keys(schema.Mapping("properties")).Order(StringComparer.Ordinal));
        Assert.Equal(
            JsonProperties(typeof(RecordCodRequest)).Keys.Order(StringComparer.Ordinal),
            Scalars(schema.Sequence("required")).Order(StringComparer.Ordinal));
        var amount = schema.Mapping("properties").Mapping("amount_cents");
        Assert.Equal("integer", amount.Scalar("type"));
        Assert.Equal("int64", amount.Scalar("format"));
        Assert.Equal("1", amount.Scalar("minimum"));
        var reference = schema.Mapping("properties").Mapping("reference");
        Assert.Equal("string", reference.Scalar("type"));
        Assert.Equal("1", reference.Scalar("minLength"));
        Assert.Equal(
            CodInputPolicy.MaximumReferenceLength.ToString(CultureInfo.InvariantCulture),
            reference.Scalar("maxLength"));
    }

    /// <summary>
    /// A response DTO and its AI-05 schema agree property by property: names, required-ness (a member is
    /// required exactly when it is always written), nullability, JSON type and format, recursively through
    /// arrays and nested objects.
    /// </summary>
    private static void AssertSchemaDescribes(YamlMappingNode root, string reference, Type dto)
    {
        var schema = Resolve(root, reference);
        Assert.Equal("object", schema.Scalar("type"));
        Assert.Equal("false", schema.Scalar("additionalProperties"));
        var properties = JsonProperties(dto);
        var declared = schema.Mapping("properties");
        Assert.Equal(properties.Keys.Order(StringComparer.Ordinal), Keys(declared).Order(StringComparer.Ordinal));
        Assert.Equal(
            properties.Where(pair => !pair.Value.OmittedWhenNull).Select(pair => pair.Key).Order(StringComparer.Ordinal),
            Scalars(schema.Sequence("required")).Order(StringComparer.Ordinal));
        foreach (var (name, property) in properties)
        {
            AssertPropertyDescribes(root, declared.Mapping(name), property, $"{dto.Name}.{name}");
        }
    }

    private static void AssertPropertyDescribes(
        YamlMappingNode root,
        YamlMappingNode schema,
        DtoProperty property,
        string context)
    {
        var type = Nullable.GetUnderlyingType(property.Type) ?? property.Type;
        if (schema.Children.ContainsKey(new YamlScalarNode("$ref")))
        {
            Assert.False(property.Nullable, context);
            if (type == typeof(string))
            {
                // A string drawn from a shared vocabulary such as OrderStatus or RouteStatus.
                var vocabulary = Resolve(root, schema);
                Assert.Equal("string", vocabulary.Scalar("type"));
                Assert.NotEmpty(vocabulary.Sequence("enum").Children);
                return;
            }

            AssertSchemaDescribes(root, schema.Scalar("$ref"), type);
            return;
        }

        var typeNode = schema.Required("type");
        string[] types = typeNode is YamlSequenceNode sequence
            ? [.. sequence.Children.Cast<YamlScalarNode>().Select(node => node.Value!)]
            : [Assert.IsType<YamlScalarNode>(typeNode).Value!];
        Assert.True(
            (property.Nullable && !property.OmittedWhenNull) == types.Contains("null"),
            $"{context}: nullability differs from AI-05.");
        var jsonType = Assert.Single(types, value => value != "null");
        if (type == typeof(Guid))
        {
            Assert.Equal(("string", "uuid"), (jsonType, schema.Scalar("format")));
        }
        else if (type == typeof(string))
        {
            Assert.Equal("string", jsonType);
        }
        else if (type == typeof(long))
        {
            Assert.Equal(("integer", "int64"), (jsonType, schema.Scalar("format")));
        }
        else if (type == typeof(int))
        {
            Assert.Equal(("integer", "int32"), (jsonType, schema.Scalar("format")));
        }
        else if (type == typeof(bool))
        {
            Assert.Equal("boolean", jsonType);
        }
        else if (type == typeof(DateTimeOffset))
        {
            Assert.Equal(("string", "date-time"), (jsonType, schema.Scalar("format")));
        }
        else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            Assert.Equal("array", jsonType);
            AssertSchemaDescribes(root, schema.Mapping("items").Scalar("$ref"), type.GetGenericArguments()[0]);
        }
        else
        {
            Assert.Fail($"{context}: no AI-05 mapping for {type}.");
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
                    nullability.Create(property).ReadState == NullabilityState.Nullable,
                    property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition ==
                    JsonIgnoreCondition.WhenWritingNull),
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

    private sealed record DtoProperty(Type Type, bool Nullable, bool OmittedWhenNull);

    private static string ReadAuditWriter() => Read(
        "src", "BuildingBlocks", "Paqueteria.Infrastructure", "Auditing",
        "PostgreSqlAppendOnlyAuditWriter.cs");

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryPaths.Root, .. segments]));

    private static void AssertProperties<T>(params string[] expected)
    {
        var actual = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;
}
