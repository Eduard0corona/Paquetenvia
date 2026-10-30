using System.Text.RegularExpressions;
using Paqueteria.ContractTests.Support;
using Paqueteria.Infrastructure.Observability;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests;

/// <summary>
/// OBS-003-W3C-CORRELATION-2026-10-12 (phase 4 PR 1): AI-05 documents the request's W3C trace id as the optional
/// <c>Problem.traceId</c>, an additive change. No problem schema is closed or requires it, so the public tracking
/// bodies that stay uniform without it remain valid, and the implementation writes exactly the documented shape.
/// </summary>
public sealed class Obs003ProblemTraceIdContractTests
{
    private static readonly YamlMappingNode Contract =
        YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));

    [Fact]
    public void Problem_documents_an_optional_32_hex_trace_id()
    {
        var problem = Contract.Mapping("components").Mapping("schemas").Mapping("Problem");
        var traceId = problem.Mapping("properties").Mapping(TraceCorrelation.ProblemMember);

        Assert.Equal("string", traceId.Scalar("type"));
        Assert.Equal("^[0-9a-f]{32}$", traceId.Scalar("pattern"));
        Assert.StartsWith("OBS-003-W3C-CORRELATION-2026-10-12.", traceId.Scalar("description"), StringComparison.Ordinal);
        Assert.Equal(
            ["status", "title", "type"],
            problem.Sequence("required").Children.Select(Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void No_problem_schema_is_closed_or_requires_the_trace_id()
    {
        var schemas = Contract.Mapping("components").Mapping("schemas").Children
            .Where(entry => Value(entry.Key).EndsWith("Problem", StringComparison.Ordinal))
            .Select(entry => (Name: Value(entry.Key), Schema: (YamlMappingNode)entry.Value))
            .ToArray();

        Assert.Equal(17, schemas.Length);
        foreach (var (name, schema) in schemas)
        {
            Assert.False(schema.Children.ContainsKey(new YamlScalarNode("additionalProperties")), name);
            Assert.False(schema.Children.ContainsKey(new YamlScalarNode("allOf")), name);
            Assert.DoesNotContain(
                TraceCorrelation.ProblemMember,
                schema.Sequence("required").Children.Select(Value));
        }

        // The answers public tracking keeps uniform (404, 429, 503) are Problem, where traceId is optional.
        var responses = Contract.Mapping("components").Mapping("responses");
        foreach (var response in new[] { "UniformNotFound", "TooManyRequests", "ServiceUnavailable" })
        {
            Assert.Equal(
                "#/components/schemas/Problem",
                responses.Mapping(response).Mapping("content").Mapping("application/problem+json").Mapping("schema").Scalar("$ref"));
        }
    }

    [Theory]
    [InlineData("4bf92f3577b34da6a3ce929d0e0e4736")]
    [InlineData("4BF92F3577B34DA6A3CE929D0E0E4736")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("0HN7SYNTHETIC0000:00000001")]
    public void The_implementation_writes_only_values_the_contract_pattern_accepts(string value)
    {
        var pattern = Contract.Mapping("components").Mapping("schemas").Mapping("Problem")
            .Mapping("properties").Mapping(TraceCorrelation.ProblemMember).Scalar("pattern");

        var matches = Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant);
        if (TraceCorrelation.IsTraceId(value))
        {
            Assert.True(matches, value);
        }
        else
        {
            // W3C forbids the all-zero trace id; the pattern admits it, the implementation never writes it.
            Assert.True(!matches || value.All(character => character == '0'), value);
        }
    }

    [Fact]
    public void The_contract_delta_records_the_applied_phase_4_decision()
    {
        var delta = Contract.Mapping("x-pilot-contract-deltas").Sequence("entries").Children
            .Cast<YamlMappingNode>()
            .Single(entry => entry.Scalar("id") == "OBS-003-W3C-CORRELATION");

        Assert.Equal("DECIDED", delta.Scalar("status"));
        Assert.StartsWith("OBS-003-W3C-CORRELATION-2026-10-12", delta.Scalar("decision"), StringComparison.Ordinal);
        Assert.Contains("PHASE4-PLATFORM-APPROVAL-2026-10-12", delta.Scalar("decision"), StringComparison.Ordinal);
        Assert.StartsWith("\"Sí, avanza con la fase 4 (Recommended)\"", delta.Scalar("literal"), StringComparison.Ordinal);
        Assert.StartsWith("purely additive", delta.Scalar("delta"), StringComparison.Ordinal);
    }

    /// <summary>
    /// OBS-003-WORKER-AUDIT-TRACE-2026-10-14 (project owner: "Sí, en un PR aparte (Recommended)", then "Se quedan vacías
    /// (Recommended)" for proof validation): the traceId text states the current rule for the audit rows the Worker
    /// writes, and the contract delta records it as a description-only change.
    /// </summary>
    [Fact]
    public void The_trace_id_text_states_the_worker_audit_rule_of_the_phase_4_follow_up()
    {
        var description = Contract.Mapping("components").Mapping("schemas").Mapping("Problem")
            .Mapping("properties").Mapping(TraceCorrelation.ProblemMember).Scalar("description");
        Assert.Contains(
            "since OBS-003-WORKER-AUDIT-TRACE-2026-10-14 the assignment lifecycle row (ASSIGNMENT_CLOSED) carries the trace id of the request or job that wrote the outbox message it processes",
            description,
            StringComparison.Ordinal);
        Assert.Contains("proof validation rows and the scheduled order auto-close rows keep request_id NULL", description, StringComparison.Ordinal);
        Assert.DoesNotContain("until trace context crosses the outbox", description, StringComparison.Ordinal);

        var delta = Contract.Mapping("x-pilot-contract-deltas").Sequence("entries").Children
            .Cast<YamlMappingNode>()
            .Single(entry => entry.Scalar("id") == "OBS-003-WORKER-AUDIT-TRACE");
        Assert.Equal("DECIDED", delta.Scalar("status"));
        Assert.StartsWith("OBS-003-WORKER-AUDIT-TRACE-2026-10-14", delta.Scalar("decision"), StringComparison.Ordinal);
        Assert.StartsWith("\"Sí, en un PR aparte (Recommended)\"", delta.Scalar("literal"), StringComparison.Ordinal);
        Assert.Contains("\"Se quedan vacías (Recommended)\"", delta.Scalar("literal"), StringComparison.Ordinal);
        Assert.StartsWith("description only", delta.Scalar("delta"), StringComparison.Ordinal);
    }

    private static string Value(YamlNode node) => ((YamlScalarNode)node).Value!;
}
