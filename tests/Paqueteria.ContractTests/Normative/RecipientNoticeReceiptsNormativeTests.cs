using System.Text.RegularExpressions;
using Notifications.Application.RecipientNotices;
using Notifications.Infrastructure.Persistence.Migrations;
using Paqueteria.Application.Messaging;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests.Normative;

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-08 NTF-006; round O, O2 to O11): the code of receipts, opt-outs and the
/// automatic reply against the contracts that declare them - the AI-06 store and the AI-18 recipient notices step 3
/// (byte for byte, as amended by O5), the AI-05 operations of part NTF-006, the AI-24 fixed texts (O11 choice 3), status
/// map (O6), budgets (O3), switches (O4), automatic-reply rules (O7) and pilot wiring (O8, O11 choice 11), the AI-07 page
/// texts (O9), and the AI-08 criterion and the decision-log row that record the answers.
/// </summary>
public sealed partial class RecipientNoticeReceiptsNormativeTests
{
    private static readonly string[] Decisions =
    [
        "O2 \"Secreto en un encabezado (Recommended)\"",
        "O3 \"Los propuestos (Recommended)\"",
        "O4 \"404 siempre; baja sin interruptor (Recommended)\"",
        "O5 \"Ampliar AI-18 (Recommended)\"",
        "O6 \"Cuarentena = bloqueado; Expanded se ignora (Recommended)\"",
        "O7 \"Aprobar lo propuesto (Recommended)\"",
        "O8 \"Registrar al cerrar las compuertas (Recommended)\"",
        "O9 \"Aprobar estos textos (Recommended)\"",
        "O10 \"Sí, el registro de la baja basta (Recommended)\"",
        "O11 \"Aprobarlas todas (Recommended)\"",
    ];

    [Fact]
    public void AI06_and_AI18_declare_the_receipt_step_byte_for_byte()
    {
        var schema = File.ReadAllText(RepositoryPaths.Normative("database", "AI-06_SCHEMA.sql"));
        var roles = File.ReadAllText(RepositoryPaths.Normative("database", "AI-18_DATABASE_ROLE_MODEL.sql"));

        Assert.Contains(Prefixed(AddRecipientNoticeReceipts.TableDdl, "--   "), schema, StringComparison.Ordinal);
        Assert.Contains(
            string.Concat(AddRecipientNoticeReceipts.TableStatements.Select(statement => "--   " + statement + "\n")),
            schema,
            StringComparison.Ordinal);
        Assert.Contains($"Installed by the Notifications lane step\n-- {AddRecipientNoticeReceipts.MigrationId} of AI-08 NTF-006", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("Declared contract, not installed yet (Notifications lane step of AI-08 NTF-006)", schema, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("^CREATE TABLE notifications\\.recipient_auto_replies", RegexOptions.Multiline), schema);

        Assert.Contains(
            string.Concat(AddRecipientNoticeReceipts.GrantStatements.Select(statement => "--      " + statement + "\n")),
            roles,
            StringComparison.Ordinal);
        Assert.Contains("--      " + AddRecipientNoticeReceipts.EmitGrantStatement + "\n", roles, StringComparison.Ordinal);
        Assert.Contains(
            $"-- 3. Notifications lane step {AddRecipientNoticeReceipts.MigrationId} of AI-08 NTF-006 (installed;\n--    {AddRecipientNoticeReceipts.DecisionId}",
            roles,
            StringComparison.Ordinal);
        Assert.Contains("Steps 1, 2, 3 and 6 are installed.\n", roles, StringComparison.Ordinal);
        Assert.Contains(
            $"-- 41. {AddRecipientNoticeReceipts.Role} is NOLOGIN BYPASSRLS, inherits no role and is granted to no runtime or bootstrap role; once the Notifications lane step {AddRecipientNoticeReceipts.MigrationId} ({AddRecipientNoticeReceipts.DecisionId}) is recorded",
            roles,
            StringComparison.Ordinal);
        Assert.Contains($"the {AddRecipientNoticeReceipts.ColumnGrants.Count} column privileges of recipient notices step 3", roles, StringComparison.Ordinal);
        Assert.Matches(new Regex($"^DO \\$\\$ BEGIN CREATE ROLE {AddRecipientNoticeReceipts.Role} NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END \\$\\$;$", RegexOptions.Multiline), roles);
        Assert.Matches(new Regex($"^REVOKE {AddRecipientNoticeReceipts.Role} FROM paqueteria_app, paqueteria_worker, paqueteria_realtime;$", RegexOptions.Multiline), roles);
        // AI-18 grants the role nothing itself: every privilege is the lane step's, declared above as comments.
        Assert.DoesNotMatch(new Regex($"^GRANT [^\\n]*{AddRecipientNoticeReceipts.Role}", RegexOptions.Multiline), roles);
        Assert.DoesNotMatch(new Regex($"^GRANT {AddRecipientNoticeReceipts.Role} TO", RegexOptions.Multiline), roles);
        foreach (var signature in AddRecipientNoticeReceipts.ApiFunctions.Concat(AddRecipientNoticeReceipts.WorkerFunctions))
        {
            Assert.Contains(signature.Replace("timestamp with time zone", "timestamptz", StringComparison.Ordinal), roles, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AI24_records_the_fixed_texts_the_status_map_the_budgets_and_the_binding_the_code_uses()
    {
        var notices = YamlNodes.LoadMapping(RepositoryPaths.Normative("specs", "AI-24_RUNTIME_HARDENING_CONTRACT.yaml"))
            .Mapping("recipient_notices");
        Assert.Contains(AddRecipientNoticeReceipts.DecisionId, Values(notices.Sequence("decisions")));

        // O11 choice 3: the two fixed WhatsApp texts are exactly the AI-24 ones.
        var autoReply = notices.Mapping("auto_reply");
        Assert.Equal(RecipientNoticeTexts.AutoReplyText, autoReply.Scalar("text"));
        Assert.Equal(RecipientNoticeTexts.OptOutConfirmationText, notices.Mapping("opt_out").Mapping("confirmation").Scalar("text"));
        var whatsAppReply = notices.Mapping("opt_out").Scalar("whatsapp_reply");
        Assert.Contains(
            string.Join(", ", RecipientNoticeTexts.OptOutKeywords.Take(3)) + " or " + RecipientNoticeTexts.OptOutKeywords[3],
            whatsAppReply,
            StringComparison.Ordinal);
        Assert.Contains($"quick reply {RecipientNoticeTexts.WhatsAppQuickReplyButton}, recognized by its fixed payload {RecipientNoticeTexts.WhatsAppQuickReplyPayload}", whatsAppReply, StringComparison.Ordinal);

        // O11 choice 10 and O7: the store, its binding and the three automatic-reply rules.
        var store = autoReply.Scalar("store");
        Assert.Contains(RecipientAutoReplyNumbers.PlatformBindingId.ToString("D"), store, StringComparison.Ordinal);
        Assert.Contains($"purpose {RecipientAutoReplyNumbers.Purpose}", store, StringComparison.Ordinal);
        Assert.Contains(AddRecipientNoticeReceipts.MigrationId, store, StringComparison.Ordinal);
        Assert.Contains("AMBIGUOUS_TIMEOUT", store, StringComparison.Ordinal);
        Assert.Contains("ignores the 08:00 to 21:00 window", autoReply.Scalar("quiet_hours"), StringComparison.Ordinal);
        Assert.Contains("DROPPED with MESSAGING_CHANNEL_DISABLED, its number erased", autoReply.Scalar("switches_off"), StringComparison.Ordinal);
        Assert.StartsWith("O7 point 3 - none of its own", autoReply.Scalar("daily_cap"), StringComparison.Ordinal);

        // O6: the e-mail status map, exactly the parser's.
        var email = notices.Mapping("receipts").Scalar("email");
        foreach (var rule in new[]
                 {
                     "Delivered sets DELIVERED",
                     $"Bounced and Suppressed set FAILED with {MessagingResultCodes.RecipientUndeliverable}",
                     $"FilteredSpam and Quarantined set FAILED with {MessagingResultCodes.PolicyBlocked}",
                     $"Failed sets FAILED with {MessagingResultCodes.Rejected}",
                     "Expanded is ignored because it is not final",
                 })
        {
            Assert.Contains(rule, email, StringComparison.Ordinal);
        }

        foreach (var (status, expected) in new (string, string?)[]
                 {
                     ("Delivered", null), ("Bounced", MessagingResultCodes.RecipientUndeliverable),
                     ("Suppressed", MessagingResultCodes.RecipientUndeliverable), ("FilteredSpam", MessagingResultCodes.PolicyBlocked),
                     ("Quarantined", MessagingResultCodes.PolicyBlocked), ("Failed", MessagingResultCodes.Rejected),
                 })
        {
            Assert.Equal(expected, AcsEmailEventsParser.Map(status)!.Value.Code);
        }

        Assert.Null(AcsEmailEventsParser.Map("Expanded"));
        var receiptFunction = notices.Mapping("receipts").Scalar("function");
        Assert.Contains("records its code in last_provider_attempt_code (O5", receiptFunction, StringComparison.Ordinal);
        Assert.Contains("security.emit_notification_status_changed", receiptFunction, StringComparison.Ordinal);

        // O2, O3, O4 and O11 choices 2, 5, 7 and 9.
        var webhooks = notices.Mapping("webhooks");
        Assert.Contains("X-Paquetenvia-Event-Secret", webhooks.Scalar("email"), StringComparison.Ordinal);
        Assert.Contains("64 KiB", webhooks.Scalar("email"), StringComparison.Ordinal);
        Assert.Contains("256 KiB", webhooks.Scalar("whatsapp"), StringComparison.Ordinal);
        Assert.Contains("1 KiB", webhooks.Scalar("unsubscribe"), StringComparison.Ordinal);
        Assert.Contains(
            "verifyMetaWhatsAppWebhook 10, receiveMetaWhatsAppWebhook 300, receiveAcsEmailEvents 120 and unsubscribeRecipientNotices 20",
            webhooks.Scalar("common"),
            StringComparison.Ordinal);
        Assert.Contains("always 404 and never 429 then", webhooks.Scalar("common"), StringComparison.Ordinal);
        Assert.Contains("the unsubscribe has no switch", webhooks.Scalar("common"), StringComparison.Ordinal);
        Assert.Contains("unsubscribeRecipientNotices has no switch (O4)", notices.Mapping("switches").Scalar("webhooks"), StringComparison.Ordinal);
        Assert.Contains("hub.verify_token", webhooks.Scalar("redaction"), StringComparison.Ordinal);

        // O8 and O11 choice 11.
        var wiring = notices.Scalar("pilot_wiring");
        Assert.Contains("the Event Grid delivery secret (with its previous slot), the WhatsApp phone_number_id and the Azure resource id", wiring, StringComparison.Ordinal);
        Assert.Contains("behind a phase parameter", wiring, StringComparison.Ordinal);
        Assert.Contains("registers the Meta callback as a step after the gates close", wiring, StringComparison.Ordinal);

        // O10 and O11 choice 8.
        Assert.StartsWith(
            $"{AddRecipientNoticeReceipts.DecisionId} (O10 \"Sí, el registro de la baja basta (Recommended)\") - an opt-out",
            notices.Mapping("opt_out").Scalar("audit"),
            StringComparison.Ordinal);
        Assert.Contains("at most 8", notices.Mapping("opt_out").Scalar("key_versions"), StringComparison.Ordinal);
    }

    [Fact]
    public void AI05_publishes_part_NTF006_with_its_operations_and_their_answers()
    {
        var root = YamlNodes.LoadMapping(RepositoryPaths.Normative("contracts", "AI-05_OPENAPI.yaml"));
        var paths = root.Mapping("paths");
        var expected = new (string Path, string Method, string Operation, string[] Statuses)[]
        {
            ("/messaging/whatsapp/webhook", "get", "verifyMetaWhatsAppWebhook", ["200", "403", "404", "429"]),
            ("/messaging/whatsapp/webhook", "post", "receiveMetaWhatsAppWebhook", ["200", "400", "403", "404", "413", "429", "503"]),
            ("/messaging/email/events", "post", "receiveAcsEmailEvents", ["200", "400", "401", "403", "404", "413", "429", "503"]),
            ("/recipient-notices/unsubscribe/{unsubscribeToken}", "post", "unsubscribeRecipientNotices", ["204", "400", "404", "413", "429", "503"]),
        };
        foreach (var (path, method, operation, statuses) in expected)
        {
            var node = paths.Mapping(path).Mapping(method);
            Assert.Equal(operation, node.Scalar("operationId"));
            Assert.Equal(["Messaging"], Values(node.Sequence("tags")));
            Assert.Empty(node.Sequence("security").Children);
            Assert.Equal(statuses, node.Mapping("responses").Children.Keys.Select(key => ((YamlScalarNode)key).Value!));
            Assert.Contains(AddRecipientNoticeReceipts.DecisionId, node.Scalar("description"), StringComparison.Ordinal);
        }

        Assert.Contains("Messaging", root.Sequence("tags").Children.Cast<YamlMappingNode>().Select(tag => tag.Scalar("name")));
        var unsubscribe = paths.Mapping("/recipient-notices/unsubscribe/{unsubscribeToken}");
        Assert.Equal(["post"], unsubscribe.Children.Keys.Select(key => ((YamlScalarNode)key).Value!));

        var delta = root.Mapping("x-pilot-contract-deltas").Sequence("entries").Children
            .Cast<YamlMappingNode>()
            .Single(entry => entry.Scalar("id") == "NTF-RECIPIENT-NOTICES");
        var implementation = delta.Scalar("implementation");
        Assert.Contains("part NTF-006 shipped by feat/ntf-006-receipts-optouts-autoreply", implementation, StringComparison.Ordinal);
        Assert.Contains(AddRecipientNoticeReceipts.MigrationId, implementation, StringComparison.Ordinal);
        Assert.Contains(AddRecipientNoticeReceipts.DecisionId, implementation, StringComparison.Ordinal);
        var parts = delta.Sequence("parts").Children.Cast<YamlMappingNode>().Where(part => part.Scalar("item") == "NTF-006").ToArray();
        Assert.Equal(
            ["verifyMetaWhatsAppWebhook", "receiveMetaWhatsAppWebhook", "receiveAcsEmailEvents", "unsubscribeRecipientNotices"],
            parts.Select(part => part.Scalar("operation")));
        Assert.DoesNotContain(parts, part => part.Scalar("change").Contains("chosen and recorded by NTF-006", StringComparison.Ordinal));
        Assert.DoesNotContain(parts, part => part.Scalar("change").Contains("asked in NTF-006", StringComparison.Ordinal));
    }

    [Fact]
    public void AI07_AI08_the_ADR_and_the_decision_log_record_the_round_O_answers()
    {
        var ui = YamlNodes.LoadMapping(RepositoryPaths.Normative("specs", "AI-07_UI_CONTRACTS.yaml"));
        var backlog = File.ReadAllText(RepositoryPaths.Normative("specs", "AI-08_BACKLOG.yaml"));
        var decisions = File.ReadAllText(RepositoryPaths.Normative("decision-log.md"));
        var changelog = File.ReadAllText(RepositoryPaths.Normative("CHANGELOG.md"));
        var adr = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "adr", "ADR-NTF-002-RECIPIENT-NOTICES.md"));

        var page = FindMapping(ui, "/avisos/baja/:token");
        Assert.Equal("DECIDED, implemented (AI-08 NTF-006)", page.Scalar("status"));
        var texts = page.Mapping("texts");
        Assert.Equal("Seguimiento de envío", texts.Scalar("heading"));
        Assert.Equal("Dejar de recibir correos", texts.Scalar("button"));
        Assert.Equal("Listo. Ya no recibirás correos de avisos en esta dirección.", texts.Scalar("success"));
        Assert.Equal("Este enlace no es válido o ya no está disponible.", texts.Scalar("invalid_link"));
        Assert.Equal("Demasiadas solicitudes. Espera N segundos e intenta de nuevo.", texts.Scalar("wait"));
        Assert.Equal("No pudimos procesar tu solicitud. Intenta más tarde.", texts.Scalar("failure"));

        var ntf006 = backlog[backlog.IndexOf("- id: NTF-006", StringComparison.Ordinal)..];
        ntf006 = ntf006[..ntf006.IndexOf("- id: NTF-007", StringComparison.Ordinal)];
        var criterion = ntf006.Split('\n').Single(line => line.Contains($"'DECIDED {AddRecipientNoticeReceipts.DecisionId} (", StringComparison.Ordinal));
        Assert.All(Decisions, decision => Assert.Contains(decision, criterion, StringComparison.Ordinal));
        Assert.DoesNotContain("(Entra or a secret header) is chosen", ntf006, StringComparison.Ordinal);
        Assert.Contains("a validly signed duplicate that changes nothing - the \"replayed\" vector, O11 choice 11", ntf006, StringComparison.Ordinal);
        var ntf008 = backlog[backlog.IndexOf("- id: NTF-008", StringComparison.Ordinal)..];
        Assert.Contains("the Event Grid subscription prepared behind a phase parameter", ntf008, StringComparison.Ordinal);
        Assert.Contains("the Event Grid delivery secret with its previous slot, the WhatsApp phone_number_id and the Azure resource id", ntf008, StringComparison.Ordinal);
        Assert.Contains("the registration of the Meta callback as a step after the gate-closing change (O8)", ntf008, StringComparison.Ordinal);

        Assert.Contains("las\n   de tomar y cerrar las respuestas automáticas solo el Worker", adr, StringComparison.Ordinal);
        Assert.Contains($"| {AddRecipientNoticeReceipts.DecisionId} |", decisions, StringComparison.Ordinal);
        Assert.Contains(AddRecipientNoticeReceipts.DecisionId, changelog, StringComparison.Ordinal);
    }

    private static YamlMappingNode FindMapping(YamlNode node, string key)
    {
        if (node is YamlMappingNode mapping)
        {
            foreach (var (childKey, child) in mapping.Children)
            {
                if (childKey is YamlScalarNode scalar && scalar.Value == key && child is YamlMappingNode found)
                {
                    return found;
                }

                if (TryFind(child, key) is { } nested)
                {
                    return nested;
                }
            }
        }

        throw new InvalidOperationException($"{key} is not in the document.");
    }

    private static YamlMappingNode? TryFind(YamlNode node, string key)
    {
        try
        {
            return node is YamlMappingNode ? FindMapping(node, key) : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string Prefixed(string ddl, string prefix) =>
        string.Join('\n', ddl.TrimEnd('\n').Split('\n').Select(line => prefix + line)) + "\n";

    private static string[] Values(YamlSequenceNode sequence) =>
        sequence.Children.Select(node => ((YamlScalarNode)node).Value!).ToArray();
}
