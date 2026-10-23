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
        Assert.Empty(Scan(notifications).Where(write => write.Target.StartsWith("orders.", StringComparison.Ordinal)));
    }

    [GeneratedRegex(@"\bINSERT\s+INTO\s+orders\s*\.\s*recipient_notice_consents\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsentInsertPattern();

    [GeneratedRegex(
        @"\b(?:UPDATE|DELETE\s+FROM|MERGE\s+INTO|TRUNCATE)\s+(?:ONLY\s+|TABLE\s+)?orders\s*\.\s*recipient_notice_consents\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsentMutationPattern();
}
