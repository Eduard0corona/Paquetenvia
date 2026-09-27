using System.Text.RegularExpressions;
using Paqueteria.Application.Idempotency;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// POD-001 request DTOs and the OPS-003-SERVER-72H-REJECTION conflict vocabulary against AI-05.
/// </summary>
public sealed class CustodyOpenApiImplementationTests
{
    private static readonly string[] ProofConflictCodes =
    [
        "CONFLICT",
        "IDEMPOTENCY_CONFLICT",
        "INVALID_REQUEST",
        "OFFLINE_OPERATION_EXPIRED",
        "ORDER_STATE_NOT_ALLOWED",
        "PII_PROTECTION_UNAVAILABLE",
        "PROOF_OBJECT_NOT_READY",
        "UPLOAD_SESSION_NOT_READY",
    ];

    [Fact]
    public void Proof_request_DTOs_match_AI05_properties()
    {
        var schemas = Schemas();
        Assert.Equal(
            PropertyNames(schemas.Mapping("CreateProofUploadSessionRequest")),
            JsonPropertyNames("CreateProofUploadSessionRequest"));
        Assert.Equal(
            PropertyNames(schemas.Mapping("FinalizeProofRequest")),
            JsonPropertyNames("FinalizeProofRequest"));
        Assert.Contains("client_occurred_at", JsonPropertyNames("CreateProofUploadSessionRequest"));
        Assert.DoesNotContain(
            "client_occurred_at",
            RequiredPropertyNames(schemas.Mapping("CreateProofUploadSessionRequest")));
        Assert.Contains("captured_at", RequiredPropertyNames(schemas.Mapping("FinalizeProofRequest")));
    }

    [Fact]
    public void Both_proof_operations_declare_the_coded_409_the_endpoints_emit()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        foreach (var path in new[] { "/orders/{orderId}/proof-upload-sessions", "/orders/{orderId}/proofs" })
        {
            Assert.Equal(
                "#/components/responses/ProofConflict",
                root.Mapping("paths").Mapping(path).Mapping("post").Mapping("responses").Mapping("409").Scalar("$ref"));
        }

        var problem = Schemas().Mapping("ProofConflictProblem");
        Assert.Equal(["code", "status", "title", "type"], RequiredPropertyNames(problem));
        Assert.Equal(
            ProofConflictCodes,
            problem.Mapping("properties").Mapping("code").Sequence("enum").Children
                .Select(node => Assert.IsType<YamlScalarNode>(node).Value!)
                .Order(StringComparer.Ordinal)
                .ToArray());

        var source = EndpointSource();
        foreach (var code in ProofConflictCodes.Except(["CONFLICT", OfflineOperationAgePolicy.ExpiredCode]))
        {
            Assert.Contains($"\"{code}\" => code", source, StringComparison.Ordinal);
        }

        Assert.Contains("_ => \"CONFLICT\"", source, StringComparison.Ordinal);
        Assert.Contains("Conflict(OfflineOperationAgePolicy.ExpiredCode)", source, StringComparison.Ordinal);
        Assert.Contains("offlinePolicy.Evaluate(request.ClientOccurredAt, clock.UtcNow)", source, StringComparison.Ordinal);
        Assert.Contains("offlinePolicy.Evaluate(capturedAt, clock.UtcNow)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_offline_age_rule_names_exactly_the_three_driver_replay_operations()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var operations = root.Mapping("x-offline-operation-age").Mapping("operations");
        Assert.Equal(
            ["createProofUploadSession", "finalizeProof", "transitionOrder"],
            operations.Children.Keys.Select(key => Assert.IsType<YamlScalarNode>(key).Value!).Order(StringComparer.Ordinal));
        Assert.Equal("captured_at (required)", operations.Scalar("finalizeProof"));
        Assert.Equal("client_occurred_at (optional)", operations.Scalar("createProofUploadSession"));
        Assert.Equal(
            ["OPS-003-OFFLINE-72H", "OPS-003-SERVER-72H-REJECTION"],
            root.Mapping("x-offline-operation-age").Sequence("decisions").Children
                .Select(node => Assert.IsType<YamlScalarNode>(node).Value!));
    }

    private static YamlMappingNode Schemas() =>
        YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"))
            .Mapping("components").Mapping("schemas");

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

    private static string EndpointSource() =>
        File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "src", "Modules", "Custody", "Custody.Endpoints", "ProofEndpoints.cs"));

    /// <summary>
    /// The JSON names of one positional request record in ProofEndpoints.cs. The test project does not
    /// reference Custody.Endpoints, so the declaration is read from source, from the record header to
    /// the end of its parameter list.
    /// </summary>
    private static string[] JsonPropertyNames(string record)
    {
        var source = EndpointSource();
        var start = source.IndexOf($"public sealed record {record}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{record} is not declared in ProofEndpoints.cs");
        var terminator = Regex.Match(source[start..], "\\)\\r?\\n\\{", RegexOptions.CultureInvariant);
        Assert.True(terminator.Success, $"{record} parameter list is not terminated");
        var end = start + terminator.Index;
        return Regex.Matches(source[start..end], "JsonPropertyName\\(\"([a-z0-9_]+)\"\\)", RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
