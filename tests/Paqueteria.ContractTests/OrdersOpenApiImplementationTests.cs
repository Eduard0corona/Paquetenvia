using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Orders.Application.Csv;
using Orders.Application.Orders;
using Orders.Domain;
using Orders.Endpoints;
using Paqueteria.Application.Idempotency;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

public sealed class OrdersOpenApiImplementationTests
{
    [Fact]
    public void Implementation_exposes_only_the_four_normative_order_operations_through_ORD002()
    {
        var source = ReadRepositoryFile("src", "Modules", "Orders", "Orders.Endpoints", "OrderEndpoints.cs");
        Assert.Equal(2, Count(source, "endpoints.MapPost("));
        Assert.Equal(2, Count(source, "endpoints.MapGet("));
        Assert.Contains("MapPost(\"/api/v1/orders\"", source, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/api/v1/orders/{orderId:guid}/transitions\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/orders\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/api/v1/orders/{orderId:guid}\"", source, StringComparison.Ordinal);
        Assert.Equal(4, Count(source, ".RequireTenantContext(StatusCodes.Status403Forbidden)"));
        Assert.Contains("request.Headers[\"Idempotency-Key\"]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPut(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPatch(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_and_response_DTOs_match_AI05_without_internal_or_PII_fields()
    {
        AssertJsonProperties<CreateOrderRequest>(
            "acceptance", "cod_expected_cents", "payer_type", "quote_id", "restricted_goods_acknowledged");
        AssertJsonProperties<OrderAcceptanceRequest>(
            "acceptance_channel", "accepted_at", "privacy_version", "terms_version");
        AssertJsonProperties<TransitionOrderRequest>(
            "client_occurred_at", "expected_version", "metadata", "reason", "target_status");
        AssertJsonProperties<OrderResponse>(
            "city_id", "claim_window_ends_at", "destination_location_id", "finalized_at", "id",
            "operator_org_id", "origin_location_id", "owner_org_id", "price_net", "pricing_tier", "public_id",
            "quote_id", "service_area_id", "service_type", "status", "total", "version");
        AssertJsonProperties<OrderTimelineResponse>("event_type", "occurred_at");
        AssertJsonProperties<OrderPageResponse>("items", "next_cursor");

        var responseProperties = typeof(OrderResponse).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain(responseProperties, name =>
            name.Contains("Acceptance", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Evidence", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Financial", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ClientAccount", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Idempotency", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Cipher", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Transition_request_and_responses_match_AI05()
    {
        var root = YamlNodes.LoadMapping(
            RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var transition = root.Mapping("components").Mapping("schemas").Mapping("TransitionRequest");
        Assert.Equal(
            ["expected_version", "reason", "target_status"],
            RequiredPropertyNames(transition));
        Assert.Equal(
            ["client_occurred_at", "expected_version", "metadata", "reason", "target_status"],
            JsonPropertyNames<TransitionOrderRequest>());
        Assert.Equal(
            JsonPropertyNames<TransitionOrderRequest>(),
            transition.Mapping("properties").Children.Keys
                .Select(key => Assert.IsType<YamlScalarNode>(key).Value!)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(500, OrderTransitionInputPolicy.MaximumReasonLength);
        Assert.Equal(2, OrderTransitionInputPolicy.MaximumMetadataDepth);
        Assert.Equal(4_096, OrderTransitionInputPolicy.DefaultMaximumMetadataUtf8Bytes);
    }

    /// <summary>
    /// OPS-003-SERVER-72H-REJECTION: transitionOrder declares its 409 with the one code the endpoint
    /// adds, the optional client timestamp is a date-time, and the endpoint emits that exact code.
    /// </summary>
    [Fact]
    public void Transition_offline_age_rejection_matches_AI05()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var transition = root.Mapping("paths").Mapping("/orders/{orderId}/transitions").Mapping("post");
        Assert.Equal(
            "#/components/responses/TransitionConflict",
            transition.Mapping("responses").Mapping("409").Scalar("$ref"));
        var schemas = root.Mapping("components").Mapping("schemas");
        var problem = schemas.Mapping("TransitionConflictProblem");
        Assert.Equal(["status", "title", "type"], RequiredPropertyNames(problem));
        Assert.Equal(
            [OfflineOperationAgePolicy.ExpiredCode],
            EnumValues(problem.Mapping("properties").Mapping("code")));
        var clientOccurredAt = schemas.Mapping("TransitionRequest").Mapping("properties").Mapping("client_occurred_at");
        Assert.Equal("date-time", clientOccurredAt.Scalar("format"));
        Assert.DoesNotContain(
            "client_occurred_at",
            RequiredPropertyNames(schemas.Mapping("TransitionRequest")));

        var age = root.Mapping("x-offline-operation-age");
        Assert.Equal("PT72H", age.Scalar("maximum_age"));
        Assert.Equal("false", age.Scalar("maximum_age_configurable"));
        Assert.Equal("PT5M", age.Scalar("clock_tolerance_default"));
        Assert.Equal("PT0S..PT5M", age.Scalar("clock_tolerance_range"));
        Assert.Equal(TimeSpan.FromHours(72), OfflineOperationAgePolicy.MaximumAge);
        Assert.Equal(TimeSpan.FromMinutes(5), OfflineOperationAgePolicy.DefaultClockTolerance);
        Assert.Equal(TimeSpan.FromMinutes(5), OfflineOperationAgePolicy.MaximumClockTolerance);
        Assert.Equal(
            "client_occurred_at (optional)",
            age.Mapping("operations").Scalar("transitionOrder"));

        var source = ReadRepositoryFile("src", "Modules", "Orders", "Orders.Endpoints", "OrderEndpoints.cs");
        Assert.Contains("offlinePolicy.Evaluate(request.ClientOccurredAt, clock.UtcNow)", source, StringComparison.Ordinal);
        Assert.Contains("[\"code\"] = OfflineOperationAgePolicy.ExpiredCode", source, StringComparison.Ordinal);
    }

    [Fact]
    public void State_and_public_event_code_implementations_match_AI04_and_AI05()
    {
        var openApi = YamlNodes.LoadMapping(
            RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var domain = YamlNodes.LoadMapping(
            RepositoryPaths.Normative("specs", "AI-04_DOMAIN_MODEL.yaml"));
        var expectedStatuses = openApi.Mapping("components").Mapping("schemas")
            .Mapping("OrderStatus").Sequence("enum").Children
            .Cast<YamlScalarNode>().Select(node => node.Value!).ToArray();
        var implementationStatuses = Enum.GetValues<OrderStatus>()
            .Select(OrderContractValues.ToContractValue).ToArray();
        Assert.Equal(17, implementationStatuses.Length);
        Assert.Equal(expectedStatuses, implementationStatuses);

        var expectedTransitionCodes = domain.Mapping("public_tracking_contract")
            .Sequence("public_event_codes").Children.Cast<YamlScalarNode>()
            .Select(node => node.Value!)
            .Where(value => value != "ORDER_CREATED")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var implementationCodes = Enum.GetValues<OrderStatus>()
            .Select(OrderPublicEventCodePolicy.Map)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedTransitionCodes, implementationCodes);

        var schemaSql = ReadRepositoryFile(
            "docs", "normative", "v0.6", "database", "AI-06_SCHEMA.sql");
        var constraintStart = schemaSql.IndexOf(
            "public_event_code text CHECK",
            StringComparison.Ordinal);
        var constraintEnd = schemaSql.IndexOf(")),", constraintStart, StringComparison.Ordinal);
        Assert.True(constraintStart >= 0 && constraintEnd > constraintStart);
        var sqlCodes = Regex.Matches(
                schemaSql[constraintStart..constraintEnd],
                "'([A-Z_]+)'",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .Where(value => value != "ORDER_CREATED")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedTransitionCodes, sqlCodes);
    }

    /// <summary>
    /// CSV-001 lives in its own endpoint file so that ORD-002 keeps exactly four operations, which
    /// is also how it could have escaped AI-05 entirely. Its surface is pinned here, and the
    /// integration suite's HTTP surface coverage test proves no endpoint file can opt out at all.
    /// </summary>
    [Fact]
    public void The_CSV001_endpoints_are_two_posts_declared_in_AI05()
    {
        var source = ReadRepositoryFile(
            "src", "Modules", "Orders", "Orders.Endpoints", "CsvOrderImportEndpoints.cs");
        Assert.Equal(2, Count(source, "endpoints.MapPost("));
        Assert.Contains("\"/api/v1/orders/csv/preview\"", source, StringComparison.Ordinal);
        Assert.Contains("\"/api/v1/orders/csv/commit\"", source, StringComparison.Ordinal);
        Assert.Equal(2, Count(source, ".RequireTenantContext(StatusCodes.Status403Forbidden)"));
        Assert.Contains("request.Headers[\"Idempotency-Key\"]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapGet(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPut(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPatch(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MapDelete(", source, StringComparison.Ordinal);

        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var paths = root.Mapping("paths");
        Assert.Equal("previewOrderCsv", paths.Mapping("/orders/csv/preview").Mapping("post").Scalar("operationId"));
        Assert.Equal("commitOrderCsv", paths.Mapping("/orders/csv/commit").Mapping("post").Scalar("operationId"));
    }

    [Fact]
    public void The_CSV001_multipart_bodies_and_responses_match_AI05()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var schemas = root.Mapping("components").Mapping("schemas");
        var paths = root.Mapping("paths");
        var preview = paths.Mapping("/orders/csv/preview").Mapping("post");
        var commit = paths.Mapping("/orders/csv/commit").Mapping("post");

        // Both operations are multipart uploads; only commit echoes the previewed digest.
        Assert.Equal(
            ["file"],
            RequiredPropertyNames(schemas.Mapping("CsvImportPreviewRequest")));
        // ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02: commit also carries the dispatcher's confirmation.
        Assert.Equal(
            ["content_digest", "file", CsvOrderImportContract.FieldRestrictedGoodsAcknowledged],
            RequiredPropertyNames(schemas.Mapping("CsvImportCommitRequest")));
        Assert.Equal(
            [CsvOrderImportContract.RestrictedGoodsAcknowledgedValue],
            schemas.Mapping("CsvImportCommitRequest").Mapping("properties")
                .Mapping(CsvOrderImportContract.FieldRestrictedGoodsAcknowledged).Sequence("enum").Children
                .Select(node => ((YamlScalarNode)node).Value));
        Assert.Equal(
            "#/components/schemas/CsvImportPreviewRequest",
            MultipartSchemaRef(preview));
        Assert.Equal(
            "#/components/schemas/CsvImportCommitRequest",
            MultipartSchemaRef(commit));
        Assert.Equal(CsvOrderImportContract.ColumnFile, "file");

        // Only commit is idempotent, and its key is the required shared header parameter.
        Assert.DoesNotContain(
            "#/components/parameters/IdempotencyKey",
            ParameterRefs(preview));
        Assert.Contains(
            "#/components/parameters/IdempotencyKey",
            ParameterRefs(commit));
        Assert.True(root.Mapping("components").Mapping("parameters")
            .Mapping("IdempotencyKey").Scalar("required") == "true");

        // Exactly the statuses the two handlers can return, and no others.
        Assert.Equal(["200", "401", "403", "409", "503"], ResponseCodes(preview));
        Assert.Equal(["200", "401", "403", "409", "422", "503"], ResponseCodes(commit));

        AssertJsonProperties<CsvImportRowErrorResponse>("code", "column");
        AssertJsonProperties<CsvImportRowPreviewResponse>(
            "cod_expected_cents", "errors", "payer_type", "quote_id", "row_number", "valid");
        AssertJsonProperties<CsvImportPreviewResponse>(
            "content_digest", "file_errors", "invalid_rows", "rows", "total_rows", "valid_rows");
        AssertJsonProperties<CsvImportRowOutcomeResponse>(
            "error_code", "order_id", "public_id", "quote_id", "row_number", "status");
        AssertJsonProperties<CsvImportCommitResponse>(
            "content_digest", "created_rows", "failed_rows", "rows", "total_rows");

        Assert.Equal(
            JsonPropertyNames<CsvImportRowErrorResponse>(),
            RequiredPropertyNames(schemas.Mapping("CsvImportRowError")));
        // D6-COD-EXPECTED: cod_expected_cents is the one optional preview field; it is present exactly on valid rows.
        Assert.Equal(
            JsonPropertyNames<CsvImportRowPreviewResponse>().Where(name => name != "cod_expected_cents"),
            RequiredPropertyNames(schemas.Mapping("CsvImportRowPreview")));
        Assert.Equal(
            JsonPropertyNames<CsvImportRowPreviewResponse>(),
            PropertyNames(schemas.Mapping("CsvImportRowPreview")));
        Assert.Equal(
            JsonPropertyNames<CsvImportPreviewResponse>(),
            RequiredPropertyNames(schemas.Mapping("CsvImportPreview")));
        Assert.Equal(
            JsonPropertyNames<CsvImportRowOutcomeResponse>(),
            RequiredPropertyNames(schemas.Mapping("CsvImportRowOutcome")));
        Assert.Equal(
            JsonPropertyNames<CsvImportCommitResponse>(),
            RequiredPropertyNames(schemas.Mapping("CsvImportCommit")));

        // The declared vocabularies are the implementation's, not a parallel list.
        Assert.Equal(
            FileErrorCodes(),
            EnumValues(schemas.Mapping("CsvImportPreview").Mapping("properties")
                .Mapping("file_errors").Mapping("items")));
        Assert.Equal(
            RowErrorCodes(),
            EnumValues(schemas.Mapping("CsvImportRowError").Mapping("properties").Mapping("code")));
        Assert.Equal(
            ["CREATED", "FAILED"],
            EnumValues(schemas.Mapping("CsvImportRowOutcome").Mapping("properties").Mapping("status")));
        Assert.Equal(
            ["CONFLICT", CsvOrderImportRowFailureCodes.IdempotencyConflict],
            EnumValues(schemas.Mapping("CsvImportConflictProblem").Mapping("properties").Mapping("code")));
    }

    /// <summary>
    /// D6-COD-EXPECTED: the COD expectation is an optional int64 of cents with minimum 0 and maximum 2000000
    /// (COD-CAP-20000-2026-10-02) on createOrder and on the
    /// CSV-001 preview, the CSV column is named after the createOrder field and appended last, the row error is the
    /// implementation's, the delta is marked shipped, and Order never exposes the amount to its VIEWER readers.
    /// </summary>
    [Fact]
    public void The_COD_expectation_matches_AI05_and_stays_off_the_Order_schema()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var schemas = root.Mapping("components").Mapping("schemas");
        foreach (var cod in new[]
        {
            schemas.Mapping("CreateOrderRequest").Mapping("properties").Mapping("cod_expected_cents"),
            schemas.Mapping("CsvImportRowPreview").Mapping("properties").Mapping("cod_expected_cents"),
        })
        {
            Assert.Equal("integer", cod.Scalar("type"));
            Assert.Equal("int64", cod.Scalar("format"));
            Assert.Equal("0", cod.Scalar("minimum"));
            // COD-CAP-20000-2026-10-02: the AI-05 maximum is the implementation's inclusive cap.
            Assert.Equal(
                OrderCodExpectationPolicy.MaximumCents.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cod.Scalar("maximum"));
        }

        Assert.DoesNotContain("cod_expected_cents", PropertyNames(schemas.Mapping("Order")));
        AssertJsonProperties<OrderResponse>(
            "city_id", "claim_window_ends_at", "destination_location_id", "finalized_at", "id",
            "operator_org_id", "origin_location_id", "owner_org_id", "price_net", "pricing_tier", "public_id",
            "quote_id", "service_area_id", "service_type", "status", "total", "version");
        Assert.DoesNotContain(
            typeof(OrderDetailResponse).GetProperties(),
            property => property.Name.Contains("Cod", StringComparison.Ordinal));

        Assert.Equal("cod_expected_cents", CsvOrderImportContract.ColumnCodExpectedCents);
        Assert.Equal(
            [.. CsvOrderImportContract.Header, "cod_expected_cents"],
            CsvOrderImportContract.HeaderWithCod.ToArray());
        var fileDescription = schemas.Mapping("CsvImportPreviewRequest").Mapping("properties").Mapping("file")
            .Scalar("description");
        Assert.Contains(CsvOrderImportContract.HeaderLine, fileDescription, StringComparison.Ordinal);
        Assert.Contains(
            "optionally followed by cod_expected_cents",
            fileDescription,
            StringComparison.Ordinal);
        Assert.Contains(CsvOrderImportRowErrorCodes.CodExpectedCentsInvalid, RowErrorCodes());

        var delta = root.Mapping("x-pilot-contract-deltas").Sequence("entries").Children
            .Cast<YamlMappingNode>()
            .Single(entry => entry.Scalar("id") == "D6-COD-EXPECTED");
        Assert.Equal("DECIDED", delta.Scalar("status"));
        Assert.Contains("feature/ord-csv-cod-expected", delta.Scalar("implementation"), StringComparison.Ordinal);
    }

    [Fact]
    public void Restricted_goods_acknowledgement_is_a_required_true_on_createOrder_and_CSV_commit()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var schemas = root.Mapping("components").Mapping("schemas");
        var createOrder = schemas.Mapping("CreateOrderRequest");
        var acknowledged = createOrder.Mapping("properties").Mapping("restricted_goods_acknowledged");

        Assert.Contains("restricted_goods_acknowledged", RequiredPropertyNames(createOrder));
        Assert.Equal("boolean", acknowledged.Scalar("type"));
        Assert.Equal("true", acknowledged.Scalar("const"));
        Assert.Contains("OrderAcceptanceCanonicalForm v1 is unchanged", acknowledged.Scalar("description"), StringComparison.Ordinal);
        Assert.Equal("restricted_goods_acknowledged", CsvOrderImportContract.FieldRestrictedGoodsAcknowledged);

        // The confirmation is a commit form field, never a CSV column: both headers keep their meaning.
        Assert.DoesNotContain(CsvOrderImportContract.FieldRestrictedGoodsAcknowledged, CsvOrderImportContract.HeaderWithCod);

        var delta = root.Mapping("x-pilot-contract-deltas").Sequence("entries").Children
            .Cast<YamlMappingNode>()
            .Single(entry => entry.Scalar("id") == "ORD-PROHIBITED-GOODS-PHONE-MX");
        Assert.Equal("DECIDED", delta.Scalar("status"));
        Assert.Equal("ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02", delta.Scalar("decision"));
        Assert.Equal("Sí, ambas", delta.Scalar("literal"));
    }

    [Fact]
    public void Shared_idempotency_policy_matches_normative_limits()
    {
        Assert.Equal(16, IdempotencyKeyPolicy.MinimumLength);
        Assert.Equal(128, IdempotencyKeyPolicy.MaximumLength);
    }

    [Fact]
    public void AI05_required_order_fields_match_product_DTOs_and_acceptance_policy()
    {
        var root = YamlNodes.LoadMapping(
            RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var createOrder = root.Mapping("components").Mapping("schemas").Mapping("CreateOrderRequest");
        var acceptance = createOrder.Mapping("properties").Mapping("acceptance");

        // D6-COD-EXPECTED: cod_expected_cents is the one optional CreateOrderRequest field.
        Assert.Equal(
            JsonPropertyNames<CreateOrderRequest>().Where(name => name != "cod_expected_cents"),
            RequiredPropertyNames(createOrder));
        Assert.Equal(
            JsonPropertyNames<CreateOrderRequest>(),
            PropertyNames(createOrder));
        Assert.Equal(
            JsonPropertyNames<OrderAcceptanceRequest>(),
            RequiredPropertyNames(acceptance));
        Assert.False(OrderAcceptanceInputPolicy.IsValid(
            "terms-synthetic-v1",
            "privacy-synthetic-v1",
            default,
            "WEB"));
        Assert.True(OrderAcceptanceInputPolicy.IsValid(
            "terms-synthetic-v1",
            "privacy-synthetic-v1",
            DateTimeOffset.Parse(
                "2026-07-22T12:00:00.1234567Z",
                System.Globalization.CultureInfo.InvariantCulture),
            "WEB"));
    }

    private static void AssertJsonProperties<T>(params string[] expected)
    {
        var actual = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static string[] JsonPropertyNames<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string MultipartSchemaRef(YamlMappingNode operation) =>
        operation.Mapping("requestBody").Mapping("content")
            .Mapping("multipart/form-data").Mapping("schema").Scalar("$ref");

    private static string[] ParameterRefs(YamlMappingNode operation) =>
        operation.Sequence("parameters").Children
            .Cast<YamlMappingNode>()
            .Select(parameter => parameter.Scalar("$ref"))
            .ToArray();

    private static string[] ResponseCodes(YamlMappingNode operation) =>
        operation.Mapping("responses").Children.Keys
            .Select(key => Assert.IsType<YamlScalarNode>(key).Value!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] EnumValues(YamlMappingNode schema) =>
        schema.Sequence("enum").Children
            .Select(node => Assert.IsType<YamlScalarNode>(node).Value!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] FileErrorCodes() => PublicConstants(typeof(CsvOrderImportFileErrorCodes));

    private static string[] RowErrorCodes() => PublicConstants(typeof(CsvOrderImportRowErrorCodes));

    private static string[] PublicConstants(Type type) => type
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral)
        .Select(field => (string)field.GetRawConstantValue()!)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string[] PropertyNames(YamlMappingNode schema) =>
        schema.Mapping("properties").Children.Keys
            .Select(key => Assert.IsType<YamlScalarNode>(key).Value!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] RequiredPropertyNames(YamlMappingNode schema) =>
        schema.Sequence("required").Children
            .Select(node => Assert.IsType<YamlScalarNode>(node).Value!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;

    private static string ReadRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(path)) return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join('/', segments));
    }
}
