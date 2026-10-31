using System.Text.RegularExpressions;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// AI-08 NTF-004 required architecture test, as NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23 (N1 "Seis, y corregir los textos
/// (Recommended)") decided it: the AI-13 §4 flows and the AI-04 <c>transaction_coordinator_allowlist</c> stay the six
/// <see cref="Flows"/>, and the recipient notice producer and the WhatsApp consent record add no flow, no coordinator and
/// no write into another module's schema - <see cref="Catalog"/> keeps its entries. The producer row is one more INSERT
/// into the shared outbox in the ORD-002 transaction (D8-OUTBOX-LANE-DISPATCH, AI-13 §7.2) and the consent row an Orders
/// table written in the quote_snapshot_to_order transaction (flow 1, its own module).
/// </summary>
public sealed partial class CrossSchemaWriteCatalogTests
{
    private const string TransitionService =
        "src/Modules/Orders/Orders.Infrastructure/Orders/PostgreSqlOrderTransitionService.cs";

    [Fact]
    public void The_recipient_notice_producer_and_consent_add_no_flow_coordinator_or_cross_module_write()
    {
        // Six flows, as AI-13 §4 numbers them and AI-04 lists them (The_catalogue_allows_exactly_the_six_ai04_flows...).
        Assert.Equal(6, Flows.Length);
        Assert.Equal(8, Catalog.Length);
        Assert.Empty(Violations(Scan(RuntimeSources()), Catalog));

        var sources = RuntimeSources().ToArray();
        // Every write Orders makes into another module's schema is still the one flow 1 quote write.
        var orders = sources.Where(source => source.File.StartsWith("src/Modules/Orders/", StringComparison.Ordinal));
        Assert.Equal(
            [(QuoteCoordinator, "UPDATE", "pricing.quotes")],
            Scan(orders).Select(write => write.Key).ToArray());

        // The producer row: written by the ORD-002 transition service only, into the shared outbox, under the topic of
        // RecipientNoticeRequestPolicy; no other runtime source names the topic in a write.
        var transition = Assert.Single(sources, source => source.File == TransitionService);
        var transitionCode = WithoutCommentLines(transition.Code);
        Assert.Contains("RecipientNoticeRequestPolicy.Topic", transitionCode, StringComparison.Ordinal);
        Assert.Contains("InsertRecipientNoticeOutboxAsync(", transitionCode, StringComparison.Ordinal);
        Assert.Empty(sources
            .Where(source => source.File != TransitionService &&
                             WithoutCommentLines(source.Code).Contains("RecipientNoticeRequestPolicy.Topic", StringComparison.Ordinal))
            .Select(source => source.File));

        // The consent row: inserted by the quote_snapshot_to_order coordinator only, and never updated or deleted.
        Assert.Equal(
            [QuoteCoordinator],
            sources.Where(source => ConsentInsertPattern().IsMatch(WithoutCommentLines(source.Code)))
                .Select(source => source.File)
                .ToArray());
        Assert.Empty(sources
            .Where(source => ConsentMutationPattern().IsMatch(WithoutCommentLines(source.Code)))
            .Select(source => source.File));

        // Notifications reads the consent through the Orders reader; it never writes an Orders table.
        var notifications = sources.Where(source => source.File.StartsWith("src/Modules/Notifications/", StringComparison.Ordinal));
        Assert.DoesNotContain(Scan(notifications), write => write.Target.StartsWith("orders.", StringComparison.Ordinal));
    }

    /// <summary>
    /// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007; AI-13 §7.2 "las bajas escriben solo filas de Notifications más el
    /// outbox y la auditoría compartidos, y leen Orders y Locations bajo RLS"): the dispatcher's panel and "Detener avisos" add
    /// no flow and no cross-module write. The stop writes the opt-outs only through the receipt executor's
    /// security.record_recipient_opt_out, the audit row only through the general writer and its stored answer in the shared
    /// platform.idempotency_keys; Orders and Locations rows are only read.
    /// </summary>
    [Fact]
    public void The_recipient_notice_panel_adds_no_flow_and_writes_only_its_own_and_the_shared_schemas()
    {
        Assert.Equal(6, Flows.Length);
        Assert.Equal(8, Catalog.Length);
        Assert.Empty(Violations(Scan(RuntimeSources()), Catalog));

        var panel = RuntimeSources()
            .Where(source => source.File.StartsWith(
                "src/Modules/Notifications/Notifications.Infrastructure/RecipientNotices/Panel/", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, panel.Length);
        Assert.Empty(Scan(panel));
        var code = string.Join('\n', panel.Select(source => WithoutCommentLines(source.Code)));
        Assert.Equal(
            ["platform.idempotency_keys"],
            PanelWritePattern().Matches(code).Select(match => match.Groups["target"].Value.ToLowerInvariant()).Distinct().ToArray());
        Assert.Contains("SELECT security.record_recipient_opt_out(", code, StringComparison.Ordinal);
        Assert.Contains("auditWriter.WriteAsync(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("notice_phone_ciphertext", code, StringComparison.Ordinal);
        Assert.DoesNotContain("recipient_opt_outs", code, StringComparison.Ordinal);
        Assert.DoesNotContain("outbox_events", code, StringComparison.Ordinal);
    }

    [GeneratedRegex(
        @"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO)\s+(?:ONLY\s+)?(?<target>[A-Za-z_]+\s*\.\s*[A-Za-z_]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PanelWritePattern();

    [GeneratedRegex(@"\bINSERT\s+INTO\s+orders\s*\.\s*recipient_notice_consents\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsentInsertPattern();

    [GeneratedRegex(
        @"\b(?:UPDATE|DELETE\s+FROM|MERGE\s+INTO|TRUNCATE)\s+(?:ONLY\s+|TABLE\s+)?orders\s*\.\s*recipient_notice_consents\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsentMutationPattern();
}
