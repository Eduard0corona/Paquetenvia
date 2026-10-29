using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// SEC-004 in AI-05: every limited operation declares 429, the <c>TooManyRequests</c> response declares the
/// <c>Retry-After</c> header every 429 sends (SEC-004-RETRY-AFTER-2026-10-13), and the sign-in start declares its two
/// answers, the page a browser navigation sees and the problem (SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12).
/// </summary>
public sealed class Sec004RateLimitContractTests
{
    private const string TooManyRequests = "#/components/responses/TooManyRequests";

    private static readonly YamlMappingNode Contract =
        YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));

    /// <summary>Every AI-05 operation that answers 429 TooManyRequests, PR 2's, the decided list of PR 3 and NTF-006's four.</summary>
    private static readonly string[] Limited =
    [
        "publicTracking",
        "publishDriverLocation",
        "requestRecipientCall",
        "previewOrderCsv",
        "commitOrderCsv",
        "exportSettlementCsv",
        "createLocation",
        "createProofUploadSession",
        "finalizeProof",
        "getOperationsQueueCounts",
        "listOrders",
        "createQuote",
        "listAssignableDrivers",
        // NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (O3): the four anonymous recipient notice operations.
        "verifyMetaWhatsAppWebhook",
        "receiveMetaWhatsAppWebhook",
        "receiveAcsEmailEvents",
        "unsubscribeRecipientNotices",
    ];

    [Fact]
    public void Every_limited_operation_and_only_those_declare_TooManyRequests()
    {
        var declaring = Operations()
            .Where(operation => operation.Responses.Children.ContainsKey(new YamlScalarNode("429")))
            .ToArray();

        Assert.Equal(
            Limited.Append("startAuthCenterSignIn").Order(StringComparer.Ordinal),
            declaring.Select(operation => operation.Id).Order(StringComparer.Ordinal));
        Assert.All(
            declaring.Where(operation => operation.Id != "startAuthCenterSignIn"),
            operation => Assert.Equal(TooManyRequests, operation.Responses.Mapping("429").Scalar("$ref")));
        // Owner answer Q5, "Solo /auth/login": the callback is not limited.
        Assert.DoesNotContain(declaring, operation => operation.Id == "completeAuthCenterSignIn");
    }

    [Fact]
    public void TooManyRequests_declares_the_retry_after_every_429_sends()
    {
        var response = Contract.Mapping("components").Mapping("responses").Mapping("TooManyRequests");
        AssertRetryAfter(response);
        Assert.Contains("SEC-004-RETRY-AFTER-2026-10-13", response.Scalar("description"), StringComparison.Ordinal);
        Assert.Equal(
            "#/components/schemas/Problem",
            response.Mapping("content").Mapping("application/problem+json").Mapping("schema").Scalar("$ref"));
    }

    [Fact]
    public void Sign_in_start_declares_the_page_and_the_problem()
    {
        var operation = Assert.Single(Operations(), operation => operation.Id == "startAuthCenterSignIn");
        var response = operation.Responses.Mapping("429");

        AssertRetryAfter(response);
        var content = response.Mapping("content");
        Assert.Equal(
            ["text/html", "application/problem+json"],
            content.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value));
        Assert.Equal("#/components/schemas/Problem", content.Mapping("application/problem+json").Mapping("schema").Scalar("$ref"));
        Assert.Contains(
            "\"Demasiados intentos de inicio de sesión desde esta red. Espera un minuto e intenta de nuevo.\"",
            response.Scalar("description"),
            StringComparison.Ordinal);
        Assert.Contains("20 sign-in starts per minute per client network", operation.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pilot_delta_records_the_decided_list()
    {
        var delta = Assert.Single(
            Contract.Mapping("x-pilot-contract-deltas").Sequence("entries").Children.Cast<YamlMappingNode>(),
            entry => entry.Scalar("id") == "SEC-004-HEAVY-ENDPOINT-LIMITS");
        Assert.Equal("DECIDED", delta.Scalar("status"));
        Assert.Contains("SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12", delta.Scalar("decision"), StringComparison.Ordinal);
        Assert.Contains("inicio de sesión 20/min por red", delta.Scalar("literal"), StringComparison.Ordinal);
        Assert.Contains("ADR-OBS-001", delta.Scalar("delta"), StringComparison.Ordinal);
    }

    private static void AssertRetryAfter(YamlMappingNode response)
    {
        var header = response.Mapping("headers").Mapping("Retry-After");
        Assert.Equal("true", header.Scalar("required"));
        Assert.Equal("integer", header.Mapping("schema").Scalar("type"));
        Assert.Equal("1", header.Mapping("schema").Scalar("minimum"));
    }

    private static IEnumerable<(string Id, string Description, YamlMappingNode Responses)> Operations() =>
        Contract.Mapping("paths").Children.Values.Cast<YamlMappingNode>()
            .SelectMany(path => path.Children.Values.OfType<YamlMappingNode>())
            .Where(operation => operation.Children.ContainsKey(new YamlScalarNode("operationId")))
            .Select(operation => (
                operation.Scalar("operationId"),
                operation.Children.ContainsKey(new YamlScalarNode("description")) ? operation.Scalar("description") : string.Empty,
                operation.Mapping("responses")));
}
