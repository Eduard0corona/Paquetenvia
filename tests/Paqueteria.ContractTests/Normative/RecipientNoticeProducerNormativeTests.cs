using System.Text.RegularExpressions;
using Orders.Application.Orders;
using Orders.Domain;
using Orders.Infrastructure.Persistence.Migrations;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests.Normative;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-004) and NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23 (N1 to N3): the code of
/// the recipient notice producer and of the WhatsApp consent box against the contracts that declare them - the AI-04
/// state machine and the AI-24 triggers, the AI-05 request shapes of part NTF-004, the AI-06 and AI-18 declarations
/// of the Orders lane step (byte for byte), the AI-07 wording of both boxes, and the corrected count of AI-13 section 4
/// flows (six, no new flow) in AI-08, AI-13 section 7.2 and AI-24.
/// </summary>
public sealed partial class RecipientNoticeProducerNormativeTests
{
    [Fact]
    public void The_policy_covers_the_29_AI04_transitions_and_writes_exactly_the_AI24_triggers()
    {
        var domain = YamlNodes.LoadMapping(RepositoryPaths.Normative("specs", "AI-04_DOMAIN_MODEL.yaml"));
        var ai04 = domain.Mapping("order_state_machine").Mapping("transitions").Children
            .SelectMany(pair => ((YamlSequenceNode)pair.Value).Children
                .Cast<YamlScalarNode>()
                .Select(target => (((YamlScalarNode)pair.Key).Value!, target.Value!)))
            .ToHashSet();
        Assert.Equal(29, ai04.Count);

        // AI-24 recipient_notices.notices: kind -> the transitions its trigger names (never the ones after "never").
        var runtime = YamlNodes.LoadMapping(RepositoryPaths.Normative("specs", "AI-24_RUNTIME_HARDENING_CONTRACT.yaml"));
        var expected = new Dictionary<(string, string), string>();
        foreach (var notice in runtime.Mapping("recipient_notices").Sequence("notices").Children.Cast<YamlMappingNode>())
        {
            var trigger = notice.Scalar("trigger");
            var named = trigger.Split("never", StringSplitOptions.None)[0];
            foreach (Match match in TransitionPattern().Matches(named))
            {
                expected.Add((match.Groups[1].Value, match.Groups[2].Value), notice.Scalar("kind"));
            }
        }

        Assert.Equal(5, expected.Count);
        Assert.Contains("never AT_PICKUP -> FAILED_ATTEMPT", runtime.Mapping("recipient_notices").Sequence("notices")
            .Children.Cast<YamlMappingNode>().Single(notice => notice.Scalar("kind") == "DELIVERY_FAILED").Scalar("trigger"),
            StringComparison.Ordinal);
        foreach (var (source, target) in ai04)
        {
            Assert.True(OrderContractValues.TryParseOrderStatus(source, out var from));
            Assert.True(OrderContractValues.TryParseOrderStatus(target, out var to));
            Assert.Equal(
                expected.GetValueOrDefault((source, target)),
                RecipientNoticeRequestPolicy.NoticeFor(from, to));
        }

        Assert.Equal(
            RecipientNoticeRequestPolicy.Notices,
            runtime.Mapping("recipient_notices").Sequence("notices").Children.Cast<YamlMappingNode>()
                .Select(notice => notice.Scalar("kind")));
        Assert.Contains(
            "recipient-notice-requested-v1 with schema_version, order_event_id, order_id, notice and occurred_at",
            runtime.Mapping("recipient_notices").Mapping("producer").Scalar("payload"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AI05_publishes_part_NTF004_exactly_as_the_endpoints_read_it()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var schemas = root.Mapping("components").Mapping("schemas");

        var box = schemas.Mapping("CreateOrderRequest").Mapping("properties").Mapping("recipient_notices");
        Assert.Equal(["null", "object"], Values(box.Sequence("type")).Order(StringComparer.Ordinal));
        Assert.Equal("false", box.Scalar("additionalProperties"));
        Assert.Equal(["whatsapp_consent_attested"], Values(box.Sequence("required")));
        Assert.Equal(
            ["whatsapp_consent_attested", "whatsapp_consent_version"],
            box.Mapping("properties").Children.Keys.Select(key => ((YamlScalarNode)key).Value!).Order(StringComparer.Ordinal));
        Assert.Equal("boolean", box.Mapping("properties").Mapping("whatsapp_consent_attested").Scalar("type"));
        Assert.Equal(RecipientNoticeConsentPolicy.Version, box.Mapping("properties").Mapping("whatsapp_consent_version").Scalar("const"));
        var description = box.Scalar("description");
        Assert.Contains(RecipientNoticeConsentPolicy.Text, description, StringComparison.Ordinal);
        Assert.Contains("uniform 409 INVALID_REQUEST before any order is created", description, StringComparison.Ordinal);
        Assert.Contains("records only the boolean whatsapp_consent_attested", description, StringComparison.Ordinal);
        Assert.Contains("unticked, the hash stays byte-identical", description, StringComparison.Ordinal);
        Assert.DoesNotContain("recipient_notices", Values(schemas.Mapping("CreateOrderRequest").Sequence("required")));

        var commit = schemas.Mapping("CsvImportCommitRequest");
        Assert.DoesNotContain(RecipientNoticeConsentPolicy.CsvFieldAttested, Values(commit.Sequence("required")));
        Assert.DoesNotContain(RecipientNoticeConsentPolicy.CsvFieldVersion, Values(commit.Sequence("required")));
        Assert.Equal(
            ["false", "true"],
            Values(commit.Mapping("properties").Mapping(RecipientNoticeConsentPolicy.CsvFieldAttested).Sequence("enum"))
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            [RecipientNoticeConsentPolicy.Version],
            Values(commit.Mapping("properties").Mapping(RecipientNoticeConsentPolicy.CsvFieldVersion).Sequence("enum")));
        Assert.Contains(
            "uniform 409 CONFLICT before the batch is reserved",
            commit.Mapping("properties").Mapping(RecipientNoticeConsentPolicy.CsvFieldAttested).Scalar("description"),
            StringComparison.Ordinal);

        var delta = root.Mapping("x-pilot-contract-deltas").Sequence("entries").Children
            .Cast<YamlMappingNode>()
            .Single(entry => entry.Scalar("id") == "NTF-RECIPIENT-NOTICES");
        var implementation = delta.Scalar("implementation");
        Assert.Contains("part NTF-004 shipped by feat/ntf-004-recipient-notice-producer", implementation, StringComparison.Ordinal);
        Assert.Contains(AddRecipientNoticeConsents.MigrationId, implementation, StringComparison.Ordinal);
        Assert.Contains(AddRecipientNoticeConsents.ProducerDecisionId, implementation, StringComparison.Ordinal);
    }

    [Fact]
    public void AI06_and_AI18_declare_the_consent_step_byte_for_byte()
    {
        var schema = File.ReadAllText(RepositoryPaths.Normative("database", "AI-06_SCHEMA.sql"));
        var roles = File.ReadAllText(RepositoryPaths.Normative("database", "AI-18_DATABASE_ROLE_MODEL.sql"));

        Assert.Contains(Prefixed(AddRecipientNoticeConsents.TableDdl, "--   "), schema, StringComparison.Ordinal);
        Assert.Contains($"Installed by the Orders lane step {AddRecipientNoticeConsents.MigrationId} of AI-08", schema, StringComparison.Ordinal);
        // Comment only: the baseline itself creates no such table (the lane step holds the DDL).
        Assert.DoesNotMatch(new Regex("^CREATE TABLE orders\\.recipient_notice_consents", RegexOptions.Multiline), schema);

        Assert.Contains(Prefixed(AddRecipientNoticeConsents.GrantsDdl, "--      "), roles, StringComparison.Ordinal);
        Assert.Contains(
            $"-- 2. Orders lane step {AddRecipientNoticeConsents.MigrationId} of AI-08 NTF-004 (installed;",
            roles,
            StringComparison.Ordinal);
        Assert.Contains("Steps 1, 2 and 6 are installed; step 3 is\n-- declared contract", roles, StringComparison.Ordinal);
        Assert.Contains(
            $"-- 40. once the Orders lane step {AddRecipientNoticeConsents.MigrationId} ({AddRecipientNoticeConsents.DecisionId}) is recorded, orders.recipient_notice_consents",
            roles,
            StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("^GRANT [^\\n]*recipient_notice_consents", RegexOptions.Multiline), roles);
    }

    [Fact]
    public void AI07_AI24_AI08_and_the_decision_log_record_the_round_N_answers()
    {
        var ui = File.ReadAllText(RepositoryPaths.Normative("specs", "AI-07_UI_CONTRACTS.yaml"));
        var screens = YamlNodes.LoadMapping(RepositoryPaths.Normative("specs", "AI-07_UI_CONTRACTS.yaml")).Mapping("screen_contracts");
        var runtime = File.ReadAllText(RepositoryPaths.Normative("specs", "AI-24_RUNTIME_HARDENING_CONTRACT.yaml"));
        var backlog = File.ReadAllText(RepositoryPaths.Normative("specs", "AI-08_BACKLOG.yaml"));
        var blueprint = File.ReadAllText(RepositoryPaths.Normative("specs", "AI-13_DOTNET_SOLUTION_BLUEPRINT.md"));
        var decisions = File.ReadAllText(RepositoryPaths.Normative("decision-log.md"));

        // N2: the CSV box is the AI-24 text with the fixed note; the open "reviewed by the owner" wording is gone.
        var csvBox = screens.Mapping("csv_order_import").Mapping("recipient_notices");
        Assert.Contains($"\"{RecipientNoticeConsentPolicy.Text}\", version {RecipientNoticeConsentPolicy.Version}", csvBox.Scalar("wording"), StringComparison.Ordinal);
        Assert.Contains(
            "\"Aplica a todas las filas de este archivo. Márcala solo si cada destinatario lo aceptó.\"",
            csvBox.Scalar("wording"),
            StringComparison.Ordinal);
        Assert.Contains(AddRecipientNoticeConsents.ProducerDecisionId, csvBox.Scalar("wording"), StringComparison.Ordinal);
        Assert.Equal("DECIDED, implemented (AI-08 NTF-004)", csvBox.Scalar("status"));
        Assert.Contains(RecipientNoticeConsentPolicy.Text, screens.Mapping("create_order").Mapping("recipient_notices").Scalar("consent"), StringComparison.Ordinal);
        Assert.DoesNotContain("reviewed by the owner in the NTF-004", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("consent version from configuration", ui, StringComparison.Ordinal);
        Assert.Contains($"    text: {RecipientNoticeConsentPolicy.Text}\n", runtime, StringComparison.Ordinal);

        // N1: six flows, no new one, in AI-08 NTF-004, AI-13 section 7.2 and AI-24 producer.pattern.
        var ntf004 = backlog[backlog.IndexOf("- id: NTF-004", StringComparison.Ordinal)..];
        ntf004 = ntf004[..ntf004.IndexOf("- id: NTF-005", StringComparison.Ordinal)];
        Assert.DoesNotContain("five", ntf004, StringComparison.Ordinal);
        Assert.Contains("still the six AI-13 section 4 flows, with no new flow or coordinator", ntf004, StringComparison.Ordinal);
        Assert.Contains("architecture - six coordinators (AI-13 section 4)", ntf004, StringComparison.Ordinal);
        Assert.Contains($"'DECIDED {AddRecipientNoticeConsents.ProducerDecisionId} (N1", ntf004, StringComparison.Ordinal);
        var addendum = blueprint[blueprint.IndexOf("### 7.2", StringComparison.Ordinal)..];
        addendum = addendum[..addendum.IndexOf("## 8.", StringComparison.Ordinal)];
        Assert.DoesNotContain("sexto", addendum, StringComparison.Ordinal);
        Assert.Contains("siguen los seis flujos de §4", addendum, StringComparison.Ordinal);
        Assert.DoesNotContain("sexto", blueprint, StringComparison.Ordinal);
        Assert.DoesNotContain("sixth", runtime, StringComparison.Ordinal);
        Assert.Contains("the six AI-13 section 4 flows stay, with no new cross-module", runtime, StringComparison.Ordinal);

        Assert.Contains($"| {AddRecipientNoticeConsents.ProducerDecisionId} |", decisions, StringComparison.Ordinal);
    }

    private static string Prefixed(string ddl, string prefix) =>
        string.Join('\n', ddl.TrimEnd('\n').Split('\n').Select(line => prefix + line)) + "\n";

    private static string[] Values(YamlSequenceNode sequence) =>
        sequence.Children.Select(node => ((YamlScalarNode)node).Value!).ToArray();

    [GeneratedRegex(@"\b([A-Z_]+) -> ([A-Z_]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TransitionPattern();
}
