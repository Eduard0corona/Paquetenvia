using Drivers.Application.Documents;
using Drivers.Infrastructure.Persistence.Migrations;
using Paqueteria.ContractTests.Support;
using Paqueteria.Infrastructure.Database.Baseline;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// DRV-DOC-001-IN-PERSON-2026-11-02: AI-05 publishes the four document operations exactly as the Drivers module serves
/// them - shape, statuses, schemas without a key, a hash or a number, the conflict codes - registerMyDriverDocument stays
/// reserved, AI-06 carries the lane's canonical DDL byte for byte and the baseline assertions pin the same catalog.
/// </summary>
public sealed class DriverDocumentsContractTests
{
    private static readonly YamlMappingNode Contract =
        YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));

    private static YamlMappingNode Schemas => Contract.Mapping("components").Mapping("schemas");

    [Theory]
    [InlineData("/drivers/documents", "get", "listDriverDocuments", "200,401,403,409,503")]
    [InlineData("/drivers/{driverId}/documents", "post", "recordDriverDocument", "201,401,403,404,409,503")]
    [InlineData("/drivers/{driverId}/documents/{documentId}/revoke", "post", "revokeDriverDocument", "200,401,403,404,409,503")]
    [InlineData("/driver/me/documents", "get", "listMyDriverDocuments", "200,401,403,503")]
    public void Each_operation_has_its_exact_status_matrix_and_no_store(
        string path,
        string method,
        string operationId,
        string statuses)
    {
        var operation = Contract.Mapping("paths").Mapping(path).Mapping(method);
        Assert.Equal(operationId, operation.Scalar("operationId"));
        Assert.Equal("Drivers", ((YamlScalarNode)operation.Sequence("tags").Children[0]).Value);
        Assert.Contains("DRV-DOC-001-IN-PERSON-2026-11-02", operation.Scalar("description"), StringComparison.Ordinal);
        var responses = operation.Mapping("responses");
        Assert.Equal(
            statuses.Split(','),
            responses.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value!).Order(StringComparer.Ordinal));
        var success = responses.Mapping(statuses.Split(',')[0]);
        Assert.Equal("no-store", success.Mapping("headers").Mapping("Cache-Control").Mapping("schema").Scalar("const"));
        Assert.Equal("#/components/responses/Forbidden", responses.Mapping("403").Scalar("$ref"));
        if (responses.Children.ContainsKey(new YamlScalarNode("409")))
        {
            Assert.Equal("#/components/responses/DriverDocumentConflict", responses.Mapping("409").Scalar("$ref"));
            Assert.Equal(
                "shape-validation-then-capability-before-persisted-state",
                operation.Scalar("x-authorization-precedence"));
        }
    }

    [Fact]
    public void Recording_requires_an_idempotency_key_and_the_exact_request()
    {
        var record = Contract.Mapping("paths").Mapping("/drivers/{driverId}/documents").Mapping("post");
        var parameters = record.Sequence("parameters").Children.Cast<YamlMappingNode>().Select(node => node.Scalar("$ref"));
        Assert.Equal(
            ["#/components/parameters/OrganizationContext", "#/components/parameters/DriverId",
                "#/components/parameters/IdempotencyKey"],
            parameters);
        var request = Schemas.Mapping("RecordDriverDocumentRequest");
        Assert.Equal("false", request.Scalar("additionalProperties"));
        Assert.Equal(["document_type", "decision"], Strings(request.Sequence("required")));
        var properties = request.Mapping("properties");
        Assert.Equal(
            ["document_type", "decision", "expiry_basis", "expires_on", "rejection_code"],
            properties.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value!));
        Assert.Equal(DriverDocumentVocabulary.RecordableTypes.ToArray(), Strings(properties.Mapping("document_type").Sequence("enum")));
        Assert.Equal(DriverDocumentVocabulary.Decisions.ToArray(), Strings(properties.Mapping("decision").Sequence("enum")));
        Assert.Equal(DriverDocumentVocabulary.ExpiryBases.ToArray(), Strings(properties.Mapping("expiry_basis").Sequence("enum")));
        Assert.Equal(DriverDocumentVocabulary.RejectionCodes.ToArray(), Strings(properties.Mapping("rejection_code").Sequence("enum")));
        Assert.Equal("date", properties.Mapping("expires_on").Scalar("format"));
    }

    [Fact]
    public void A_document_never_carries_a_key_a_hash_a_number_or_the_reviewer()
    {
        var document = Schemas.Mapping("DriverDocument");
        Assert.Equal("false", document.Scalar("additionalProperties"));
        var expected = new[]
        {
            "id", "document_type", "status", "rejection_code", "expires_at", "valid_through", "expiry_basis",
            "recorded_at", "revoked_at",
        };
        Assert.Equal(expected, Strings(document.Sequence("required")));
        Assert.Equal(expected, document.Mapping("properties").Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value!));
        foreach (var name in new[] { "DriverDocument", "DriverDocumentSlot", "DriverDocumentSet", "DriverDocumentPage",
                     "MyDriverDocuments", "RecordDriverDocumentRequest", "RegisterDriverDocumentRequest" })
        {
            var properties = Schemas.Mapping(name).Mapping("properties").Children.Keys
                .Cast<YamlScalarNode>().Select(key => key.Value!).ToArray();
            Assert.DoesNotContain(properties, property =>
                property.Contains("object_key", StringComparison.Ordinal) ||
                property.Contains("sha256", StringComparison.Ordinal) ||
                property.Contains("number", StringComparison.Ordinal) ||
                property.Contains("photo", StringComparison.Ordinal) ||
                property.Contains("reviewed_by", StringComparison.Ordinal));
        }

        Assert.Equal(
            DriverDocumentVocabulary.Statuses.ToArray(),
            Strings(document.Mapping("properties").Mapping("status").Sequence("enum")));
        Assert.Equal(
            ["INVALID_REQUEST", "IDEMPOTENCY_CONFLICT", "DOCUMENT_TYPE_NOT_REQUIRED", "EXPIRY_DATE_OUT_OF_RANGE",
                "DOCUMENT_NOT_REVOCABLE"],
            Strings(Schemas.Mapping("DriverDocumentConflictProblem").Mapping("properties").Mapping("code").Sequence("enum")));
        Assert.Equal(
            DriverDocumentConflictCodes.Record.Union(DriverDocumentConflictCodes.Revoke).Order(StringComparer.Ordinal),
            Strings(Schemas.Mapping("DriverDocumentConflictProblem").Mapping("properties").Mapping("code").Sequence("enum"))
                .Order(StringComparer.Ordinal));
        Assert.Equal("100", Schemas.Mapping("DriverDocumentPage").Mapping("properties").Mapping("items").Scalar("maxItems"));
    }

    [Fact]
    public void Driver_upload_stays_reserved_and_cannot_choose_a_key_or_a_hash()
    {
        var register = Contract.Mapping("paths").Mapping("/driver/me/documents").Mapping("post");
        Assert.Equal("registerMyDriverDocument", register.Scalar("operationId"));
        Assert.Equal("RESERVED_NOT_IMPLEMENTED", register.Scalar("x-implementation-status"));
        Assert.StartsWith("RESERVED, NOT IMPLEMENTED", register.Scalar("description"), StringComparison.Ordinal);
        var request = Schemas.Mapping("RegisterDriverDocumentRequest");
        Assert.Equal("false", request.Scalar("additionalProperties"));
        Assert.Equal(["upload_session_id", "document_type", "expires_on"], Strings(request.Sequence("required")));
    }

    [Fact]
    public void The_pilot_delta_records_Y1_and_the_gate()
    {
        var entry = Contract.Mapping("x-pilot-contract-deltas").Sequence("entries").Children.Cast<YamlMappingNode>()
            .Single(node => node.Scalar("id") == "DRV-DOC-001-IN-PERSON");
        Assert.Equal("DECIDED", entry.Scalar("status"));
        Assert.StartsWith("DRV-DOC-001-IN-PERSON-2026-11-02", entry.Scalar("decision"), StringComparison.Ordinal);
        Assert.EndsWith("- Despacho en persona (Recommended)", entry.Scalar("literal"), StringComparison.Ordinal);
        Assert.Contains("Drivers:Documents:Gate007DecisionId", entry.Scalar("gate"), StringComparison.Ordinal);
        Assert.Contains("PILOT_REAL_PEOPLE", entry.Scalar("gate"), StringComparison.Ordinal);
    }

    [Fact]
    public void AI06_carries_the_lane_DDL_byte_for_byte_and_the_baseline_pins_the_same_catalog()
    {
        var schema = File.ReadAllText(RepositoryPaths.Normative("database", "AI-06_SCHEMA.sql"));
        var ddl = AddInPersonDriverDocuments.CanonicalDdl;
        Assert.Equal(1, Count(schema, ddl));
        Assert.True(
            schema.IndexOf("CREATE TABLE drivers.driver_documents", StringComparison.Ordinal) <
            schema.IndexOf(ddl, StringComparison.Ordinal));
        Assert.True(
            schema.IndexOf(ddl, StringComparison.Ordinal) <
            schema.IndexOf("CREATE POLICY driver_documents_tenant", StringComparison.Ordinal));
        Assert.Contains(ddl.TrimEnd('\n'), AddInPersonDriverDocuments.RenderUpSql(), StringComparison.Ordinal);

        Assert.Equal(AddInPersonDriverDocuments.MigrationId, DatabaseBaselineAssertions.InPersonDriverDocumentsMigrationId);
        Assert.Equal(AddInPersonDriverDocuments.ContractColumns, DatabaseBaselineAssertions.InPersonDriverDocumentColumns);
        Assert.Equal(AddInPersonDriverDocuments.ContractChecks, DatabaseBaselineAssertions.InPersonDriverDocumentChecks);
        Assert.Equal(7, AddInPersonDriverDocuments.NewColumns.Count);
        foreach (var column in AddInPersonDriverDocuments.NewColumns)
        {
            Assert.Contains($"ADD COLUMN {column} ", ddl, StringComparison.Ordinal);
        }

        // No column can hold a number, a photo or the content of a document.
        Assert.DoesNotContain("number", ddl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("photo", ddl, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] Strings(YamlSequenceNode sequence) =>
        sequence.Children.Cast<YamlScalarNode>().Select(node => node.Value!).ToArray();

    private static int Count(string source, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = source.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }
}
