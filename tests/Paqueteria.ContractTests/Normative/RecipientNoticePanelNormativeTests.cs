using Notifications.Application.RecipientNotices;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests.Normative;

/// <summary>
/// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007; round T, T1 to T6): the contracts that record the answers - the
/// AI-05 part NTF-007 as published (no scheduled time, the channels recorded, the 409 without a usable contact, no-store
/// on both operations, the explicit code set) and its capability section, the AI-07 panel with its texts, the AI-08
/// criterion, the AI-24 dispatcher view, the decision-log row (which records I4 through T1), the CHANGELOG and the
/// legal draft's review notes - and that the web helper holds exactly the AI-07 texts.
/// </summary>
public sealed class RecipientNoticePanelNormativeTests
{
    private static readonly string[] Answers =
    [
        "T1 \"Así, como I4 (Recommended)\"",
        "T2 \"Sin hora, con nota (Recommended)\"",
        "T3 \"Mensaje y botón siempre (Recommended)\"",
        "T4 \"Panel con una línea (Recommended)\"",
        "T5 \"Aprobar estos textos (Recommended)\"",
        "T6 \"Aprobarlas todas (Recommended)\"",
    ];

    [Fact]
    public void AI05_publishes_part_NTF007_with_the_round_T_answers_and_its_capability_section()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var delta = root.Mapping("x-pilot-contract-deltas").Sequence("entries").Children
            .Cast<YamlMappingNode>()
            .Single(entry => entry.Scalar("id") == "NTF-RECIPIENT-NOTICES");
        var implementation = delta.Scalar("implementation");
        Assert.Contains("part NTF-007 shipped by feat/ntf-007-recipient-notice-panel", implementation, StringComparison.Ordinal);
        Assert.Contains(RecipientNoticePanelContract.DecisionId, implementation, StringComparison.Ordinal);
        Assert.DoesNotContain("every other part is pending", implementation, StringComparison.Ordinal);
        var parts = delta.Sequence("parts").Children.Cast<YamlMappingNode>().Where(part => part.Scalar("item") == "NTF-007").ToArray();
        Assert.Equal(
            ["listOrderRecipientNotices", "stopOrderRecipientNotices", "x-capability-matrix"],
            parts.Select(part => part.Scalar("operation")));
        var list = parts[0].Scalar("change");
        var stop = parts[1].Scalar("change");
        Assert.DoesNotContain("scheduled_for", list, StringComparison.Ordinal);
        Assert.Contains("no scheduled time, T2 \"Sin hora, con nota (Recommended)\"", list, StringComparison.Ordinal);
        Assert.Contains("MAX_ATTEMPTS_EXHAUSTED", list, StringComparison.Ordinal);
        Assert.Contains("Cache-Control no-store", list, StringComparison.Ordinal);
        Assert.Contains("the channels recorded - the channels whose contact is usable, named also when their opt-out was already on", stop, StringComparison.Ordinal);
        Assert.Contains("RECIPIENT_CONTACT_UNAVAILABLE when no contact is usable", stop, StringComparison.Ordinal);
        Assert.Contains("Cache-Control no-store", stop, StringComparison.Ordinal);

        var matrix = root.Mapping("x-capability-matrix");
        var decision = matrix.Scalar("recipient_notice_operations_decision");
        Assert.StartsWith("NTF-RECIPIENT-NOTICES-2026-10-20", decision, StringComparison.Ordinal);
        Assert.Contains(RecipientNoticePanelContract.DecisionId, decision, StringComparison.Ordinal);
        Assert.Contains("the tracking_link_operations rule", decision, StringComparison.Ordinal);
        Assert.Contains("decided before any order is read", decision, StringComparison.Ordinal);
        var section = matrix.Mapping("recipient_notice_operations");
        Assert.Equal(
            ["listOrderRecipientNotices", "stopOrderRecipientNotices"],
            section.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value!));
    }

    [Fact]
    public void AI07_records_the_panel_its_texts_the_off_line_and_the_behaviour_after_a_stop()
    {
        var ui = File.ReadAllText(RepositoryPaths.Normative("specs", "AI-07_UI_CONTRACTS.yaml"));
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("specs", "AI-07_UI_CONTRACTS.yaml"));
        var panel = root.Mapping("routes").Mapping("/ops/orders/:id").Mapping("recipient_notices");
        Assert.Equal("DECIDED, implemented (AI-08 NTF-007)", panel.Scalar("status"));
        Assert.Contains(RecipientNoticePanelContract.DecisionId, panel.Scalar("decision"), StringComparison.Ordinal);
        Assert.DoesNotContain("decided, not built yet", ui, StringComparison.Ordinal);
        Assert.StartsWith("T4 \"Panel con una línea (Recommended)\"", panel.Scalar("off"), StringComparison.Ordinal);
        Assert.StartsWith("T3 \"Mensaje y botón siempre (Recommended)\"", panel.Scalar("after_stop"), StringComparison.Ordinal);
        Assert.Contains("T6 choice 9", panel.Scalar("realtime"), StringComparison.Ordinal);

        var texts = panel.Mapping("texts");
        Assert.Equal("Avisos al destinatario", texts.Scalar("title"));
        Assert.Equal("Los avisos al destinatario todavía no están activos.", texts.Scalar("not_active_yet"));
        Assert.Equal("Los avisos solo salen de 8:00 a 21:00, hora de Mazatlán", texts.Scalar("quiet_hours"));
        Assert.Equal("Esta orden todavía no tiene avisos.", texts.Scalar("empty"));
        Assert.Equal("No pudimos detener los avisos. Intenta de nuevo.", texts.Scalar("stop_failure"));
        Assert.Equal("No fue posible leer los avisos.", texts.Scalar("read_failure"));
        Assert.Equal(
            "T1 - title \"¿Detener avisos?\", text \"Orden {guía}. El destinatario dejará de recibir avisos de todas las empresas. Durante el piloto no se puede deshacer.\", buttons \"Cancelar\" and \"Detener avisos\", focus starting on \"Cancelar\" (ConfirmDialog)",
            texts.Scalar("dialog"));
        foreach (var answer in Answers.Where(answer =>
                     !answer.StartsWith("T3 ", StringComparison.Ordinal) && !answer.StartsWith("T6 ", StringComparison.Ordinal)))
        {
            Assert.Contains(answer, texts.Scalar("decision"), StringComparison.Ordinal);
        }

        // The web helper holds every AI-07 text verbatim (the Vitest test pins them one by one).
        var helper = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "apps", "web", "src", "operations", "contracts", "recipient-notices.ts"));
        Assert.Equal("apps/web/src/operations/contracts/recipient-notices.ts", texts.Scalar("helper"));
        foreach (var key in new[] { "title", "not_active_yet", "quiet_hours", "empty", "stop_failure", "read_failure" })
        {
            Assert.Contains($"\"{texts.Scalar(key)}\"", helper, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AI08_AI24_the_decision_log_the_changelog_and_the_legal_review_notes_record_round_T()
    {
        var backlog = File.ReadAllText(RepositoryPaths.Normative("specs", "AI-08_BACKLOG.yaml"));
        var ntf007 = backlog[backlog.IndexOf("- id: NTF-007", StringComparison.Ordinal)..];
        ntf007 = ntf007[..ntf007.IndexOf("- id: NTF-008", StringComparison.Ordinal)];
        var criterion = ntf007.Split('\n').Single(line => line.Contains($"'DECIDED {RecipientNoticePanelContract.DecisionId} (", StringComparison.Ordinal));
        Assert.All(Answers, answer => Assert.Contains(answer, criterion, StringComparison.Ordinal));
        Assert.Contains("T1 records I4", criterion, StringComparison.Ordinal);

        var notices = YamlNodes.LoadMapping(RepositoryPaths.Normative("specs", "AI-24_RUNTIME_HARDENING_CONTRACT.yaml"))
            .Mapping("recipient_notices");
        Assert.Contains(
            RecipientNoticePanelContract.DecisionId,
            notices.Sequence("decisions").Children.Cast<YamlScalarNode>().Select(node => node.Value));
        Assert.Contains("and AI-08 NTF-007 (the dispatcher's list and \"Detener avisos\"", notices.Scalar("status"), StringComparison.Ordinal);
        // NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31: NTF-008 is recorded next to NTF-007 and only the gate-closing change
        // turns the notices on (RecipientNoticePilotWiringNormativeTests pins the rest).
        Assert.Contains("and AI-08 NTF-008 (the pilot wiring", notices.Scalar("status"), StringComparison.Ordinal);
        Assert.Contains("Only a later change, the gate-closing change", notices.Scalar("status"), StringComparison.Ordinal);
        var view = notices.Mapping("dispatcher_view");
        Assert.Contains("no scheduled time (T2)", view.Scalar("list"), StringComparison.Ordinal);
        Assert.StartsWith("T6 choice 5", view.Scalar("stop_steps"), StringComparison.Ordinal);
        Assert.Contains("409 RECIPIENT_CONTACT_UNAVAILABLE", view.Scalar("stop_steps"), StringComparison.Ordinal);
        Assert.StartsWith("T6 choice 6", view.Scalar("stop_answer"), StringComparison.Ordinal);
        Assert.StartsWith("T6 choice 11", view.Scalar("synthetic"), StringComparison.Ordinal);
        Assert.Contains("+520000000002 and", view.Scalar("synthetic"), StringComparison.Ordinal);
        Assert.Contains("destinatario@example.invalid", view.Scalar("synthetic"), StringComparison.Ordinal);

        var log = File.ReadAllText(RepositoryPaths.Normative("decision-log.md"));
        var row = log.Split('\n').Single(line => line.StartsWith($"| 2026-10-30 | {RecipientNoticePanelContract.DecisionId} |", StringComparison.Ordinal));
        Assert.Equal(7, row.Replace("\\|", string.Empty, StringComparison.Ordinal).Count(character => character == '|'));
        foreach (var answer in Answers)
        {
            var id = answer[..2];
            Assert.Contains($"Answer: {answer[3..]}", row, StringComparison.Ordinal);
            Assert.Contains($"{id} (NTF-007 ", row, StringComparison.Ordinal);
        }

        Assert.Contains("T1 records I4", row, StringComparison.Ordinal);
        Assert.Contains("Round T (asked and answered 2026-10-30, NTF-007), context verified before asking, verbatim:", row, StringComparison.Ordinal);
        Assert.Contains("{WhatsApp\\|Correo}", row, StringComparison.Ordinal);

        Assert.Contains(RecipientNoticePanelContract.DecisionId, File.ReadAllText(RepositoryPaths.Normative("CHANGELOG.md")), StringComparison.Ordinal);
        var adr = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "adr", "ADR-NTF-002-RECIPIENT-NOTICES.md"));
        // NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 extends the ADR status to NTF-008.
        Assert.Contains("(AI-08\n  NTF-002 a NTF-008;", adr, StringComparison.Ordinal);
        Assert.Contains("NTF-RECIPIENT-NOTICE-PANEL-2026-10-30", adr, StringComparison.Ordinal);
        var legal = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "legal", "terminos-y-condiciones-borrador.md"));
        Assert.DoesNotContain("todavía no está en el decision log", legal, StringComparison.Ordinal);
        Assert.DoesNotContain("está solo en las respuestas literales del owner", legal, StringComparison.Ordinal);
        Assert.Contains("NTF-RECIPIENT-NOTICE-PANEL-2026-10-30", legal, StringComparison.Ordinal);
    }
}
