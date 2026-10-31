using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Notifications.Application.RecipientNotices;
using Notifications.Infrastructure.Persistence.Migrations;
using Notifications.Infrastructure.RecipientNotices;
using Orders.Application.RecipientNotices;
using Orders.Application.Tracking;
using Orders.Infrastructure.RecipientNotices;
using Orders.Infrastructure.Tracking;
using Paqueteria.Application.RecipientNotices;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Messaging;
using Paqueteria.Infrastructure.Security;

namespace Paqueteria.UnitTests.Notifications;

/// <summary>
/// NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 (AI-08 NTF-005): static guarantees of the count step of the recipient notice
/// caps (its behaviour, catalog, Up, Down and Up again are proven on PostgreSQL by
/// <c>RecipientNoticeDeliveryPostgreSqlContractTests</c> and <c>NotificationsMigrationGuardPostgreSqlTests</c>), and the
/// start validation of <c>Notifications:RecipientNotices</c>: real providers refused where the data is synthetic and
/// without the GATE-004 and GATE-007 decision ids, the AI-24 templates, and what an enabled lane needs.
/// </summary>
public sealed partial class RecipientNoticeCountsAndOptionsTests
{
    // ------------------------------------------------------------------ the count step

    [Fact]
    public void The_count_function_is_a_lease_bound_definer_that_returns_two_integers_and_reads_only()
    {
        var sql = AddRecipientNoticeCounts.CountsSql;
        var header = sql[..sql.IndexOf(" AS $fn$", StringComparison.Ordinal)];
        var body = sql[sql.IndexOf("$fn$", StringComparison.Ordinal)..];

        Assert.StartsWith(
            "CREATE FUNCTION security.read_recipient_notice_counts(p_outbox_id uuid,p_lease_token uuid)\n" +
            "RETURNS TABLE(order_channel_count integer,day_channel_count integer)\n",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("SECURITY DEFINER", header, StringComparison.Ordinal);
        Assert.EndsWith("SET search_path=pg_catalog,platform,notifications,pg_temp", header, StringComparison.Ordinal);
        Assert.DoesNotMatch(Keyword("EXECUTE"), body);
        Assert.DoesNotMatch(Keyword("RETURNING"), body);
        Assert.DoesNotMatch(Keyword("INSERT"), body);
        Assert.DoesNotMatch(Keyword("UPDATE"), body);
        Assert.DoesNotMatch(Keyword("DELETE"), body);
        Assert.DoesNotMatch(Keyword("PERFORM"), body);

        // L1: it answers only under the live lease of a send request whose recipient notice is PENDING.
        Assert.Contains(
            "WHERE id=p_outbox_id AND status='PROCESSING' AND lease_token=p_lease_token\n" +
            "    AND lease_expires_at>clock_timestamp() AND topic='notifications.send-requested';\n" +
            "  IF NOT FOUND THEN RETURN; END IF;",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHERE id=v_request.aggregate_id AND owner_org_id=v_request.owner_org_id\n" +
            "    AND recipient_kind='ORDER_DESTINATION' AND status='PENDING';\n" +
            "  IF NOT FOUND THEN RETURN; END IF;",
            sql,
            StringComparison.Ordinal);

        // L2: the per-order count is of the same owner, order and channel; the daily count is of the channel on the whole
        // platform (no owner filter), on the America/Mazatlan day of the last attempt; both only once the provider was
        // called, and neither counts the notice being sent.
        var counts = sql[sql.IndexOf("RETURN QUERY SELECT", StringComparison.Ordinal)..];
        var perOrder = counts[..counts.IndexOf("-- L2 \"Toda la plataforma\"", StringComparison.Ordinal)];
        var perDay = counts[counts.IndexOf("-- L2 \"Toda la plataforma\"", StringComparison.Ordinal)..];
        Assert.Contains("n.owner_org_id=v_notification.owner_org_id AND n.order_id=v_notification.order_id", perOrder, StringComparison.Ordinal);
        Assert.DoesNotContain("owner_org_id", perDay, StringComparison.Ordinal);
        foreach (var part in new[] { perOrder, perDay })
        {
            Assert.Contains("n.channel=v_notification.channel AND n.recipient_kind='ORDER_DESTINATION'", part, StringComparison.Ordinal);
            Assert.Contains("n.attempts>0 AND n.id<>v_notification.id", part, StringComparison.Ordinal);
        }

        Assert.Contains("n.last_attempt_at>=v_day_start AND n.last_attempt_at<v_day_end", perDay, StringComparison.Ordinal);
        Assert.Contains("v_day=(clock_timestamp() AT TIME ZONE 'America/Mazatlan')::date;", sql, StringComparison.Ordinal);
        Assert.Contains("v_day_start=v_day::timestamp AT TIME ZONE 'America/Mazatlan';", sql, StringComparison.Ordinal);
        Assert.Contains("v_day_end=(v_day+1)::timestamp AT TIME ZONE 'America/Mazatlan';", sql, StringComparison.Ordinal);
        // Only the two numbers leave: no identifier, recipient, code or time is selected into the result.
        Assert.Equal(2, Regex.Matches(counts, @"SELECT count\(\*\)::integer FROM notifications\.notifications n").Count);
    }

    [Fact]
    public void The_count_step_grants_only_the_worker_and_its_down_drops_only_what_it_adds()
    {
        var up = AddRecipientNoticeCounts.UpSql;
        var down = AddRecipientNoticeCounts.DownSql;
        Assert.Equal("NTF-RECIPIENT-NOTICE-CAPS-2026-10-23", AddRecipientNoticeCounts.DecisionId);
        Assert.Equal("security.read_recipient_notice_counts(uuid,uuid)", AddRecipientNoticeCounts.CountsSignature);
        Assert.Contains(AddRecipientNoticeCounts.CountsSql, up, StringComparison.Ordinal);
        Assert.Contains(AddRecipientNoticeCounts.OrderChannelIndexDdl, up, StringComparison.Ordinal);
        Assert.Contains(AddRecipientNoticeCounts.AttemptDayIndexDdl, up, StringComparison.Ordinal);

        // The function is created as its owner, revoked from PUBLIC and granted to the Worker only, once.
        Assert.True(
            up.IndexOf("SET ROLE paqueteria_outbox_executor;", StringComparison.Ordinal) <
            up.IndexOf("CREATE FUNCTION security.read_recipient_notice_counts", StringComparison.Ordinal));
        Assert.Contains("REVOKE ALL ON FUNCTION security.read_recipient_notice_counts(uuid,uuid) FROM PUBLIC;", up, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(up, "GRANT EXECUTE"));
        Assert.Contains("GRANT EXECUTE ON FUNCTION security.read_recipient_notice_counts(uuid,uuid) TO paqueteria_worker;", up, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(up, "CREATE FUNCTION"));
        // Two statements; the other two mentions are the definitions the step verifies with pg_get_indexdef.
        Assert.Equal(2, Regex.Matches(up, "^CREATE INDEX", RegexOptions.Multiline).Count);
        foreach (var sql in new[] { up, down })
        {
            Assert.DoesNotContain("TO paqueteria_app", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("OWNER TO", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("CREATE TABLE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("ALTER TABLE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("DELETE FROM", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("GRANT SELECT", sql, StringComparison.Ordinal);
            // The verification fails the step if the Worker could read the Notifications table.
            Assert.Contains("has_table_privilege('paqueteria_worker','notifications.notifications','SELECT')", sql, StringComparison.Ordinal);
            Assert.Contains("'paqueteria_outbox_executor:EXECUTE:false,paqueteria_worker:EXECUTE:false'", sql, StringComparison.Ordinal);
        }

        // The temporary CREATE on security never outlives the step.
        Assert.Contains("GRANT CREATE ON SCHEMA security TO paqueteria_outbox_executor;", up, StringComparison.Ordinal);
        Assert.Contains("REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;", up, StringComparison.Ordinal);

        // Down: exactly the function and the two indexes.
        Assert.Single(Regex.Matches(down, "DROP FUNCTION"));
        Assert.Contains("DROP FUNCTION security.read_recipient_notice_counts(uuid,uuid);", down, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(down, "DROP INDEX").Count);
        Assert.Contains("DROP INDEX notifications.notifications_recipient_attempt_day_idx;", down, StringComparison.Ordinal);
        Assert.Contains("DROP INDEX notifications.notifications_recipient_order_channel_idx;", down, StringComparison.Ordinal);

        // Up refuses to run without the NTF-003 lane step or a second time.
        Assert.Contains("requires the NTF-003 recipient notice lane step", up, StringComparison.Ordinal);
    }

    [Fact]
    public void AI06_AI18_AI24_and_AI08_declare_exactly_what_the_count_step_installs()
    {
        var schema = File.ReadAllText(TestPath("docs/normative/v0.6/database/AI-06_SCHEMA.sql"));
        var roles = File.ReadAllText(TestPath("docs/normative/v0.6/database/AI-18_DATABASE_ROLE_MODEL.sql"));
        var runtime = File.ReadAllText(TestPath("docs/normative/v0.6/specs/AI-24_RUNTIME_HARDENING_CONTRACT.yaml"));
        var backlog = File.ReadAllText(TestPath("docs/normative/v0.6/specs/AI-08_BACKLOG.yaml"));
        var decisions = File.ReadAllText(TestPath("docs/normative/v0.6/decision-log.md"));

        // AI-06 carries both index statements byte for byte.
        Assert.Contains($"--   {AddRecipientNoticeCounts.OrderChannelIndexDdl}\n", schema, StringComparison.Ordinal);
        Assert.Contains($"--   {AddRecipientNoticeCounts.AttemptDayIndexDdl}\n", schema, StringComparison.Ordinal);
        Assert.Contains(AddRecipientNoticeCounts.MigrationId, schema, StringComparison.Ordinal);

        // AI-18 declares the step and its deployment assertion.
        Assert.Contains($"6. Notifications lane step {AddRecipientNoticeCounts.MigrationId} of AI-08 NTF-005 (installed;", roles, StringComparison.Ordinal);
        Assert.Contains(
            $"-- 39. once the Notifications lane step {AddRecipientNoticeCounts.MigrationId} ({AddRecipientNoticeCounts.DecisionId}) is recorded, {AddRecipientNoticeCounts.CountsSignature} is owned by paqueteria_outbox_executor",
            roles,
            StringComparison.Ordinal);
        Assert.Contains("search_path pg_catalog, platform,\n--    notifications, pg_temp", roles, StringComparison.Ordinal);

        // AI-24 caps and the unusable contact name the decision; AI-08 NTF-005 records it and its contract test.
        Assert.Contains($"    decision: {AddRecipientNoticeCounts.DecisionId} (L1 \"Función de conteo (Recommended)\"", runtime, StringComparison.Ordinal);
        Assert.Contains("count: before any decryption or provider call the Worker asks security.read_recipient_notice_counts", runtime, StringComparison.Ordinal);
        Assert.Contains($"unusable: {AddRecipientNoticeCounts.DecisionId} (L3 \"Destinatario inválido (Recommended)\")", runtime, StringComparison.Ordinal);
        Assert.Contains($"'DECIDED {AddRecipientNoticeCounts.DecisionId} (L1", backlog, StringComparison.Ordinal);
        Assert.Contains("PostgreSQL contract - security.read_recipient_notice_counts", backlog, StringComparison.Ordinal);
        Assert.Contains($"| {AddRecipientNoticeCounts.DecisionId} |", decisions, StringComparison.Ordinal);
    }

    [Fact]
    public void The_count_step_is_the_tail_of_the_lane_and_the_routine_map_follows_it()
    {
        Assert.Equal(AddRecipientNoticeCounts.MigrationId, E002NotificationStateReader.RecipientNoticeCountsMigrationId);
        Assert.True(string.CompareOrdinal(AddRecipientNoticeCounts.MigrationId, AddRecipientNoticeLane.MigrationId) > 0);

        var before = E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true, true, true, true, true, true, true, true, true,
            recipientNoticeLaneApplied: true);
        var after = E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true, true, true, true, true, true, true, true, true,
            recipientNoticeLaneApplied: true, recipientNoticeCountsApplied: true);
        Assert.Empty(before.Select(static entry => entry.Signature).Except(after.Select(static entry => entry.Signature)));
        var added = Assert.Single(after, entry => before.All(previous => previous.Signature != entry.Signature));
        Assert.Equal(AddRecipientNoticeCounts.CountsSignature, added.Signature);
        Assert.Equal("paqueteria_outbox_executor", added.Owner);
        Assert.Equal(["paqueteria_worker"], added.Grantees);
        Assert.EndsWith("_PLUS_RTMAPIROLE_PLUS_NTFRECIPIENT_PLUS_NTFCAPS_V1",
            E002RoutineMap.Name(E002RoutineMapState.Applied, true, true, true, true, true, true, true, true, true, true, true,
                recipientNoticeLaneApplied: true, recipientNoticeCountsApplied: true),
            StringComparison.Ordinal);

        // The count step cannot be recorded without the lane step, nor on a pending map.
        Assert.Throws<InvalidOperationException>(() => E002RoutineMap.Select(
            E002RoutineMapState.Applied, true, true, true, true, true, true, true, true, true, true, true,
            recipientNoticeCountsApplied: true));
        Assert.Throws<InvalidOperationException>(() => E002RoutineMap.Select(
            E002RoutineMapState.Pending, false, recipientNoticeLaneApplied: true, recipientNoticeCountsApplied: true));
    }

    // ------------------------------------------------------------------ start validation (AI-24 recipient_notices.switches)

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("DevSynthetic")]
    public void A_real_provider_is_refused_where_the_data_is_synthetic_even_with_both_gate_ids(string environment)
    {
        foreach (var messaging in new[] { MetaWhatsApp(), AcsEmail() })
        {
            var failures = Validate(new RecipientNoticesOptions
            {
                Gate004DecisionId = "GATE-004-RESOLVED-2026-11-01",
                Gate007DecisionId = "GATE-007-RESOLVED-2026-11-01",
            }, messaging, environment);

            Assert.Contains(failures, failure => failure.Contains("refused in Development, Testing and DEV_SYNTHETIC", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("GATE-004-RESOLVED-2026-11-01", "")]
    [InlineData("", "GATE-007-RESOLVED-2026-11-01")]
    [InlineData("GATE-004", "GATE-007")]
    [InlineData("gate-004-resolved-2026-11-01", "GATE-007-RESOLVED-2026-11-01")]
    [InlineData("GATE-007-RESOLVED-2026-11-01", "GATE-004-RESOLVED-2026-11-01")]
    [InlineData("GATE-004-RESOLVED 2026", "GATE-007-RESOLVED-2026-11-01")]
    public void A_real_provider_is_refused_without_both_gate_decision_ids(string gate004, string gate007)
    {
        var failures = Validate(new RecipientNoticesOptions
        {
            Gate004DecisionId = gate004,
            Gate007DecisionId = gate007,
        }, MetaWhatsApp(), "Production");

        var failure = Assert.Single(failures);
        Assert.Contains("Gate004DecisionId and Gate007DecisionId", failure, StringComparison.Ordinal);
        // No failure message carries a configured value.
        Assert.DoesNotContain("RESOLVED", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Off_with_both_gate_ids_or_without_a_real_provider_starts()
    {
        Assert.Empty(Validate(new RecipientNoticesOptions
        {
            Gate004DecisionId = "GATE-004-RESOLVED-2026-11-01",
            Gate007DecisionId = "GATE-007-RESOLVED-2026-11-01",
        }, MetaWhatsApp(), "Production"));
        Assert.Empty(Validate(new RecipientNoticesOptions(), new MessagingOptions(), "Production"));
        Assert.Empty(Validate(new RecipientNoticesOptions(), Synthetic(withTemplates: false), "Testing"));
    }

    [Fact]
    public void Enabled_needs_the_ai24_templates_of_every_channel_that_sends()
    {
        // Every template is required while the channel sends.
        var missing = Validate(new RecipientNoticesOptions { Enabled = true }, Synthetic(withTemplates: false), "Testing");
        Assert.Equal(8, missing.Count(failure => failure.Contains("is required while recipient notices are enabled", StringComparison.Ordinal)));

        // The exact AI-24 templates start.
        Assert.Empty(Validate(new RecipientNoticesOptions { Enabled = true }, Synthetic(withTemplates: true), "Testing"));

        // A different text, language or parameter count is refused even while off.
        var altered = Synthetic(withTemplates: true);
        altered.Email.Templates["recipient.delivered"].PlainText += " ";
        altered.WhatsApp.Templates["recipient.in-transit"].LanguageCode = "es_ES";
        altered.Email.Templates["recipient.tracking-link"].Subject = "Tu envío";
        altered.WhatsApp.Templates["recipient.delivery-failed"].ParameterCount = 2;
        var failures = Validate(new RecipientNoticesOptions(), altered, "Testing");
        Assert.Equal(4, failures.Count);
        Assert.Contains("Messaging:Email:Templates:recipient.delivered must be the AI-24", failures[0] + failures[1] + failures[2] + failures[3], StringComparison.Ordinal);

        // A channel whose provider is Disabled needs no template.
        var whatsAppOnly = Synthetic(withTemplates: true);
        whatsAppOnly.Email = new() { Provider = EmailProviderKind.Disabled };
        Assert.Empty(Validate(new RecipientNoticesOptions { Enabled = true }, whatsAppOnly, "Testing"));
    }

    [Fact]
    public void Enabled_needs_the_readers_the_contact_lookup_key_and_a_tracking_link()
    {
        var enabled = new RecipientNoticesOptions { Enabled = true };
        Assert.Contains(
            Validate(enabled, Synthetic(withTemplates: true), "Testing", readers: false),
            failure => failure.Contains("requires the Orders and Locations recipient notice readers", StringComparison.Ordinal));
        Assert.Contains(
            Validate(enabled, Synthetic(withTemplates: true), "Testing", lookupKey: false),
            failure => failure.Contains("requires ContactLookup:Keys", StringComparison.Ordinal));
        Assert.Contains(
            Validate(enabled, Synthetic(withTemplates: true), "Testing", link: RecipientNoticeLinkReadiness.Unavailable),
            failure => failure.Contains("requires PublicTracking:PublicBaseUrl", StringComparison.Ordinal));

        // An e-mail channel needs an https origin for its one-click unsubscribe URL (RFC 8058), even in Testing.
        Assert.Contains(
            Validate(enabled, Synthetic(withTemplates: true), "Testing", publicBaseUrl: "http://127.0.0.1:3000"),
            failure => failure.Contains("requires an https PublicTracking:PublicBaseUrl", StringComparison.Ordinal));
        var whatsAppOnly = Synthetic(withTemplates: true);
        whatsAppOnly.Email = new() { Provider = EmailProviderKind.Disabled };
        Assert.Empty(Validate(enabled, whatsAppOnly, "Testing", publicBaseUrl: "http://127.0.0.1:3000"));

        // The synthetic link key is enough with synthetic providers, never with a real one.
        Assert.Empty(Validate(enabled, Synthetic(withTemplates: true), "Testing", link: RecipientNoticeLinkReadiness.Synthetic));
        var real = MetaWhatsApp();
        real.WhatsApp.Templates = Synthetic(withTemplates: true).WhatsApp.Templates;
        Assert.Contains(
            Validate(new RecipientNoticesOptions
            {
                Enabled = true,
                Gate004DecisionId = "GATE-004-RESOLVED-2026-11-01",
                Gate007DecisionId = "GATE-007-RESOLVED-2026-11-01",
            }, real, "Production", link: RecipientNoticeLinkReadiness.Synthetic),
            failure => failure.Contains("a configured key with a real provider", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("08:00", "21:00", 12, 8, 500, true)]
    [InlineData("08:00:00", "21:00:00", 1, 1, 1, true)]
    [InlineData("21:00", "08:00", 12, 8, 500, false)]
    [InlineData("08:00", "08:00", 12, 8, 500, false)]
    [InlineData("8", "21:00", 12, 8, 500, false)]
    [InlineData("08:00", "21:00", 0, 8, 500, false)]
    [InlineData("08:00", "21:00", 25, 8, 500, false)]
    [InlineData("08:00", "21:00", 12, 0, 500, false)]
    [InlineData("08:00", "21:00", 12, 8, 0, false)]
    [InlineData("08:00", "21:00", 12, 8, 100_001, false)]
    public void Window_stale_bound_and_caps_are_validated_on_start(
        string start,
        string end,
        int maximumDelayHours,
        int perOrder,
        int perDay,
        bool valid)
    {
        var failures = Validate(new RecipientNoticesOptions
        {
            QuietHours = new() { Start = start, End = end },
            MaximumNoticeDelayHours = maximumDelayHours,
            MaximumPerOrderAndChannel = perOrder,
            DailyLimitPerChannel = perDay,
        }, new MessagingOptions(), "Production");

        Assert.Equal(valid, failures.Count == 0);
    }

    [Fact]
    public void The_defaults_are_the_ai24_ones_and_off()
    {
        var options = new RecipientNoticesOptions();
        Assert.False(options.Enabled);
        Assert.Equal(("08:00", "21:00"), (options.QuietHours.Start, options.QuietHours.End));
        Assert.Equal(12, options.MaximumNoticeDelayHours);
        Assert.Equal(8, options.MaximumPerOrderAndChannel);
        Assert.Equal(500, options.DailyLimitPerChannel);
        Assert.Equal(string.Empty, options.Gate004DecisionId);
        Assert.Equal(string.Empty, options.Gate007DecisionId);
        Assert.Equal("Notifications:RecipientNotices", RecipientNoticesOptions.SectionName);
    }

    // ------------------------------------------------------------------ helpers

    private static List<string> Validate(
        RecipientNoticesOptions options,
        MessagingOptions messaging,
        string environment,
        bool readers = true,
        bool lookupKey = true,
        RecipientNoticeLinkReadiness link = RecipientNoticeLinkReadiness.Configured,
        string? publicBaseUrl = "https://paquetenvia.test")
    {
        var unavailable = new UnavailableRecipientNoticeReaders();
        var keys = lookupKey
            ? new Dictionary<int, string> { [1] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray()) }
            : [];
        var validation = new RecipientNoticesOptionsValidation(
            Options.Create(messaging),
            Options.Create(new PublicTrackingOptions { PublicBaseUrl = publicBaseUrl }),
            new Environment(environment),
            new HmacRecipientContactLookup(new ContactLookupKeyRing(Options.Create(new ContactLookupOptions { Keys = keys }))),
            readers ? new Links(link) : unavailable,
            readers ? new Readers() : unavailable,
            readers ? new Readers() : unavailable);
        var result = validation.Validate(null, options);
        return result.Succeeded ? [] : result.Failures!.ToList();
    }

    private static MessagingOptions MetaWhatsApp() => new()
    {
        WhatsApp = new() { Provider = WhatsAppProviderKind.MetaCloudApi },
    };

    private static MessagingOptions AcsEmail() => new()
    {
        Email = new() { Provider = EmailProviderKind.AzureCommunicationServices },
    };

    private static MessagingOptions Synthetic(bool withTemplates)
    {
        var options = new MessagingOptions
        {
            WhatsApp = new() { Provider = WhatsAppProviderKind.Synthetic },
            Email = new() { Provider = EmailProviderKind.Synthetic },
        };
        if (withTemplates)
        {
            foreach (var kind in RecipientNoticeCatalog.All)
            {
                var key = RecipientNoticeCatalog.TemplateKey(kind);
                options.WhatsApp.Templates[key] = new()
                {
                    Name = "ntf005_" + key.Replace("recipient.", string.Empty, StringComparison.Ordinal).Replace('-', '_'),
                    LanguageCode = RecipientNoticeTexts.WhatsAppLanguage,
                    ParameterCount = RecipientNoticeTexts.WhatsAppParameterCount,
                };
                options.Email.Templates[key] = new()
                {
                    Subject = RecipientNoticeTexts.EmailSubject(kind),
                    PlainText = RecipientNoticeTexts.EmailPlainText(kind),
                    ParameterCount = RecipientNoticeTexts.EmailParameterCount,
                };
            }
        }

        return options;
    }

    private static string TestPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Paqueteria.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException(), relative);
    }

    private static Regex Keyword(string keyword) =>
        new($"(^|[^A-Za-z_]){keyword}([^A-Za-z_]|$)", RegexOptions.CultureInvariant);

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Paqueteria.Worker";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Links(RecipientNoticeLinkReadiness readiness) : IRecipientNoticeTrackingLinks
    {
        public RecipientNoticeLinkReadiness Readiness { get; } = readiness;

        public RecipientNoticeTrackingLink? TryBuild(Guid orderId, RecipientNoticeLinkRecord link) => null;
    }

    private sealed class Readers : IRecipientNoticeOrderReader, IRecipientNoticeContactReader
    {
        public Task<RecipientNoticeOrderSnapshot?> ReadAsync(Guid ownerOrganizationId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<RecipientNoticeOrderSnapshot?>(null);

        Task<RecipientNoticeContactCopies?> IRecipientNoticeContactReader.ReadAsync(
            Guid ownerOrganizationId,
            Guid locationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<RecipientNoticeContactCopies?>(null);
    }
}
