using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests.Normative;

/// <summary>
/// NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 (AI-08 NTF-008; rounds V and W, V1 and W1 to W11): the contracts that record
/// the answers - the AI-08 criterion and wording, the AI-24 pilot wiring (secret names, roles, PILOT_ACS_NAME,
/// emailEventsPhase), the corrected AI-05 NTF-002 sentence (W4), the decision-log row and the CHANGELOG - and that the
/// committed pilot templates keep every recipient-notice switch off.
/// </summary>
public sealed class RecipientNoticePilotWiringNormativeTests
{
    private const string DecisionId = "NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31";

    private static readonly string[] Answers =
    [
        "V1 \"Preparar el PR y esperar (Recommended)\"",
        "W1 \"No todavía\"",
        "W2 \"Sí, los dos\"",
        "W3 \"Esperar a todo (Recommended)\"",
        "W4 \"Al cerrar las compuertas (Recommended)\"",
        "W5 \"Un rol propio y uno de Azure (Recommended)\"",
        "W6 \"Rol propio mínimo (Recommended)\"",
        "W7 \"El workflow (Recommended)\"",
        "W8 \"Tú en Cloud Shell antes de fusionar (Recommended)\"",
        "W9 \"Cuarentena (Recommended)\"",
        "W10 \"Después del abogado (Recommended)\"",
        "W11 \"Aprobarlas todas (Recommended)\"",
    ];

    private static readonly string[] SecretNames =
    [
        "public-tracking-link-key",
        "paquetenvia-contact-lookup-key-1",
        "whatsapp-cloud-api-token",
        "whatsapp-phone-number-id",
        "whatsapp-app-secret and whatsapp-app-secret-previous",
        "whatsapp-webhook-verify-token and whatsapp-webhook-verify-token-previous",
        "acs-email-events-secret and acs-email-events-secret-previous",
        "acs-communication-resource-id",
    ];

    [Fact]
    public void AI08_NTF008_records_rounds_V_and_W_and_follows_W4_W5_W7_and_W8()
    {
        var backlog = File.ReadAllText(RepositoryPaths.Normative("specs", "AI-08_BACKLOG.yaml"));
        var ntf008 = backlog[backlog.IndexOf("- id: NTF-008", StringComparison.Ordinal)..];
        var next = ntf008.IndexOf("\n- id: ", 1, StringComparison.Ordinal);
        ntf008 = next < 0 ? ntf008 : ntf008[..next];
        Assert.Contains($"and {DecisionId} (project owner; V1 and W1 to W11)", ntf008, StringComparison.Ordinal);
        var criterion = ntf008.Split('\n').Single(line => line.Contains($"'DECIDED {DecisionId} (", StringComparison.Ordinal));
        Assert.All(Answers, answer => Assert.Contains(answer, criterion, StringComparison.Ordinal));
        Assert.Contains("the thirteen technical choices of W11", criterion, StringComparison.Ordinal);

        // W5: no wrap-only role for the API any more; W7: the workflow writes the seven secrets; W4: no notice copy yet.
        Assert.DoesNotContain("a wrap-only role for the API", ntf008, StringComparison.Ordinal);
        Assert.Contains("the built-in Key Vault Reader", ntf008, StringComparison.Ordinal);
        Assert.DoesNotContain("only after the owner created the secrets, the key and the resources", ntf008, StringComparison.Ordinal);
        Assert.Contains("W7 \"El workflow (Recommended)\" - \"Los genera una sola vez si faltan", ntf008, StringComparison.Ordinal);
        Assert.Contains("every switch stays off and Locations:NoticeKeyProtector stays Disabled", ntf008, StringComparison.Ordinal);
        Assert.Contains("the what-if run by the owner in Cloud Shell before the merge", ntf008, StringComparison.Ordinal);
    }

    [Fact]
    public void AI24_records_the_pilot_wiring_names_roles_and_phase()
    {
        var notices = YamlNodes.LoadMapping(RepositoryPaths.Normative("specs", "AI-24_RUNTIME_HARDENING_CONTRACT.yaml"))
            .Mapping("recipient_notices");
        Assert.Contains(DecisionId, notices.Sequence("decisions").Children.Cast<YamlScalarNode>().Select(node => node.Value));
        var wiring = notices.Scalar("pilot_wiring");
        Assert.All(SecretNames, name => Assert.Contains(name, wiring, StringComparison.Ordinal));
        foreach (var fragment in new[]
                 {
                     "Paquetenvia Pilot Email Sender",
                     "Microsoft.Communication/CommunicationServices/Read",
                     "never listKeys or regenerateKey",
                     "PILOT_ACS_NAME",
                     "emailEventsPhase none or subscribe, committed as none",
                     "https://paquetenvia.com/api/v1/messaging/email/events",
                     "https://paquetenvia.com/api/v1/messaging/whatsapp/webhook",
                     "docs/operations/env-001-pilot/guia-owner-avisos.md",
                 })
        {
            Assert.Contains(fragment, wiring, StringComparison.Ordinal);
        }

        var key = notices.Mapping("contact").Scalar("notice_key");
        Assert.Contains("Paquetenvia Pilot Recipient Notice Unwrap", key, StringComparison.Ordinal);
        Assert.Contains("Key Vault Reader", key, StringComparison.Ordinal);
        Assert.Contains("Locations:NoticeKeyProtector stays Disabled until the gate-closing change", key, StringComparison.Ordinal);
        Assert.Contains("PublicTracking__ too", notices.Mapping("switches").Scalar("pilot"), StringComparison.Ordinal);
    }

    [Fact]
    public void AI05_the_decision_log_and_the_changelog_record_W4_and_the_round()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var implementation = root.Mapping("x-pilot-contract-deltas").Sequence("entries").Children
            .Cast<YamlMappingNode>()
            .Single(entry => entry.Scalar("id") == "NTF-RECIPIENT-NOTICES")
            .Scalar("implementation");
        Assert.DoesNotContain("until AI-08 NTF-008 wires the key", implementation, StringComparison.Ordinal);
        Assert.Contains("no notice copy is written until the gate-closing change turns it on", implementation, StringComparison.Ordinal);

        var log = File.ReadAllText(RepositoryPaths.Normative("decision-log.md"));
        var row = log.Split('\n').Single(line => line.StartsWith($"| 2026-10-31 | {DecisionId} |", StringComparison.Ordinal));
        Assert.Equal(7, row.Replace("\\|", string.Empty, StringComparison.Ordinal).Count(character => character == '|'));
        Assert.Contains("Round V (asked and answered 2026-10-30, NTF-008), context verified before asking, verbatim:", row, StringComparison.Ordinal);
        Assert.Contains("Round W (asked and answered 2026-10-30, NTF-008), context verified before asking, verbatim:", row, StringComparison.Ordinal);
        var changelog = File.ReadAllText(RepositoryPaths.Normative("CHANGELOG.md"));
        Assert.StartsWith($"# Changelog\n\n## Avisos al destinatario: conexión en el piloto ({DecisionId}, AI-08 NTF-008)", changelog, StringComparison.Ordinal);
        Assert.DoesNotContain("AI-08 NTF-008 la conecta en el piloto.", changelog, StringComparison.Ordinal);
    }

    [Fact]
    public void The_committed_pilot_templates_keep_every_recipient_notice_switch_off()
    {
        var pilot = Path.Combine(RepositoryPaths.Root, "deploy", "azure", "pilot");
        var apps = File.ReadAllText(Path.Combine(pilot, "apps.bicep"));
        foreach (var setting in new[] { "Notifications__RecipientNotices__", "Messaging__", "Locations__NoticeKeyProtector" })
        {
            Assert.DoesNotContain($"name: '{setting}", apps, StringComparison.Ordinal);
        }

        Assert.Contains("param emailEventsPhase string = 'none'", File.ReadAllText(Path.Combine(pilot, "events.bicep")), StringComparison.Ordinal);
    }
}
