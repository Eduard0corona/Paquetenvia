using System.Text.RegularExpressions;
using Notifications.Application.Dispatching;
using Notifications.Infrastructure.Persistence.Migrations;
using Paqueteria.Application.Messaging;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.UnitTests.Notifications;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-003): static guarantees of the recipient notice lane step. Its
/// behaviour, the catalog, Up, Down and Up again are proven on PostgreSQL by
/// <c>RecipientNoticeLanePostgreSqlContractTests</c> and <c>NotificationsMigrationGuardPostgreSqlTests</c>.
/// </summary>
public sealed partial class RecipientNoticeLaneMigrationTests
{
    [Fact]
    public void Replaced_bodies_differ_from_the_published_ones_only_by_the_reviewed_edits()
    {
        foreach (var (published, installed, edits) in new[]
                 {
                     (AddRecipientNoticeLane.PublishedResolverSql, AddRecipientNoticeLane.ResolverSql, AddRecipientNoticeLane.ResolverEdits),
                     (AddRecipientNoticeLane.PublishedProvisionSql, AddRecipientNoticeLane.ProvisionSql, AddRecipientNoticeLane.ProvisionEdits),
                     (AddRecipientNoticeLane.PublishedEmitSql, AddRecipientNoticeLane.EmitSql, AddRecipientNoticeLane.EmitEdits),
                 })
        {
            Assert.StartsWith("CREATE OR REPLACE FUNCTION ", published, StringComparison.Ordinal);
            Assert.Equal(Header(published), Header(installed));
            Assert.Equal(
                published,
                edits.Aggregate(installed, (sql, edit) => sql.Replace(edit.Replacement, edit.Published, StringComparison.Ordinal)));
            Assert.DoesNotMatch(ExecuteKeyword(), installed);
            Assert.DoesNotMatch(ReturningKeyword(), installed);
            Assert.Contains(installed, AddRecipientNoticeLane.UpSql, StringComparison.Ordinal);
            Assert.DoesNotContain(installed, AddRecipientNoticeLane.DownSql, StringComparison.Ordinal);
            Assert.Contains(published, AddRecipientNoticeLane.DownSql, StringComparison.Ordinal);
        }

        // The published bodies are the latest of their lane: the D8 router, the NTF-001 provisioning and the OBS-003
        // emitter.
        Assert.Contains(AddRecipientNoticeLane.PublishedResolverSql, AddDispatchOutboxLane.UpSql, StringComparison.Ordinal);
        Assert.Contains(
            AddRecipientNoticeLane.PublishedProvisionSql.Replace("CREATE OR REPLACE FUNCTION ", "CREATE FUNCTION ", StringComparison.Ordinal),
            AddTenantSafeOutboxNotifications.UpSql,
            StringComparison.Ordinal);
        Assert.Equal(CarryOutboxTraceContext.EmitSql, AddRecipientNoticeLane.PublishedEmitSql);

        // The router gains exactly one topic for the NOTIFICATIONS lane; orders.status-changed stays REALTIME.
        Assert.Contains("'orders.recipient-notice-requested') THEN 'NOTIFICATIONS'", AddRecipientNoticeLane.ResolverSql, StringComparison.Ordinal);
        Assert.Equal(NotificationOutboxTopics.RecipientNoticeRequested, AddRecipientNoticeLane.RequestTopic);
        Assert.Equal(OutboxConsumer.Notifications, NotificationOutboxTopics.Resolve(AddRecipientNoticeLane.RequestTopic));
    }

    [Fact]
    public void The_emitter_writes_v2_with_order_and_notice_only_for_a_recipient_notice()
    {
        var emit = AddRecipientNoticeLane.EmitSql;
        Assert.Contains("'schema_version','notification-status-changed-v2'", emit, StringComparison.Ordinal);
        Assert.DoesNotContain("notification-status-changed-v1", emit, StringComparison.Ordinal);
        Assert.Contains("IF v_notification.recipient_kind='ORDER_DESTINATION' THEN", emit, StringComparison.Ordinal);
        Assert.Contains("jsonb_build_object('order_id',v_notification.order_id,'notice',v_notice)", emit, StringComparison.Ordinal);
        // The OBS-003 trace context is still copied from the originating event.
        Assert.Contains("p_occurred_at,p_occurred_at,v_trace_parent);", emit, StringComparison.Ordinal);
        // Nothing personal can enter the payload: only these members are ever built.
        Assert.DoesNotContain("recipient_ciphertext", emit, StringComparison.Ordinal);
        Assert.DoesNotContain("variables_snapshot", emit, StringComparison.Ordinal);
        Assert.DoesNotContain("provider_reference", emit, StringComparison.Ordinal);
        Assert.DoesNotContain("last_provider_attempt_code", emit, StringComparison.Ordinal);

        // Every recipient template key maps to exactly one notice, in both directions (emitter and expansion).
        var notices = new[] { "TRACKING_LINK", "IN_TRANSIT", "DELIVERY_FAILED", "DELIVERED" };
        foreach (var (key, notice) in AddRecipientNoticeLane.RecipientTemplateKeys.Zip(notices))
        {
            Assert.Contains($"WHEN '{key}' THEN '{notice}'", emit, StringComparison.Ordinal);
            Assert.Contains($"WHEN '{notice}' THEN '{key}'", AddRecipientNoticeLane.ExpandSql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void New_functions_are_definers_without_dynamic_sql_or_returning_and_only_the_worker_executes_them()
    {
        var functions = new[]
        {
            ("security.expand_recipient_notice_request(uuid,uuid,boolean,interval,time,time,text,text[])", AddRecipientNoticeLane.ExpandSql),
            ("security.read_notification_delivery(uuid,uuid)", AddRecipientNoticeLane.ReadDeliverySql),
            ("security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,text,timestamptz,timestamptz)", AddRecipientNoticeLane.ApplySql),
            ("security.cancel_notification(uuid,uuid,uuid,integer,text,timestamptz)", AddRecipientNoticeLane.CancelSql),
            ("security.is_recipient_opted_out(text,bytea[])", AddRecipientNoticeLane.OptOutSql),
        };
        foreach (var (signature, sql) in functions)
        {
            Assert.StartsWith("CREATE FUNCTION ", sql, StringComparison.Ordinal);
            Assert.Contains("SECURITY DEFINER", Header(sql), StringComparison.Ordinal);
            Assert.Matches(PinnedSearchPath(), Header(sql));
            Assert.DoesNotMatch(ExecuteKeyword(), Body(sql));
            Assert.DoesNotMatch(ReturningKeyword(), Body(sql));
            Assert.Contains(sql, AddRecipientNoticeLane.UpSql, StringComparison.Ordinal);
            Assert.Contains($"REVOKE ALL ON FUNCTION {signature} FROM PUBLIC;", AddRecipientNoticeLane.UpSql, StringComparison.Ordinal);
        }

        Assert.Equal(
            functions.Select(static function => function.Item1).Order(StringComparer.Ordinal),
            Regex.Match(AddRecipientNoticeLane.UpSql, @"GRANT EXECUTE ON FUNCTION (?<list>[\s\S]*?)\s+TO paqueteria_worker;")
                .Groups["list"].Value.Split(",\n", StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal));
        Assert.Single(Regex.Matches(AddRecipientNoticeLane.UpSql, "GRANT EXECUTE"));
        Assert.DoesNotContain("TO paqueteria_app", AddRecipientNoticeLane.UpSql, StringComparison.Ordinal);
        Assert.DoesNotContain("TO paqueteria_app", AddRecipientNoticeLane.DownSql, StringComparison.Ordinal);
        Assert.DoesNotContain("OWNER TO", AddRecipientNoticeLane.UpSql, StringComparison.Ordinal);

        // The opt-out store: forced row security, no policy, the runtime defaults removed, the executor reads only.
        Assert.Contains("ALTER TABLE notifications.recipient_opt_outs FORCE ROW LEVEL SECURITY;", AddRecipientNoticeLane.UpSql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE POLICY", AddRecipientNoticeLane.UpSql, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL ON notifications.recipient_opt_outs FROM PUBLIC,paqueteria_app,paqueteria_worker;", AddRecipientNoticeLane.UpSql, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON notifications.recipient_opt_outs TO paqueteria_outbox_executor;", AddRecipientNoticeLane.UpSql, StringComparison.Ordinal);
        Assert.Contains("FOR EACH ROW EXECUTE FUNCTION platform.reject_runtime_mutation();", AddRecipientNoticeLane.UpSql, StringComparison.Ordinal);

        // Temporary privileges never outlive the step.
        foreach (var sql in new[] { AddRecipientNoticeLane.UpSql, AddRecipientNoticeLane.DownSql })
        {
            Assert.Contains("REVOKE CREATE ON SCHEMA security,notifications FROM paqueteria_outbox_executor;", sql, StringComparison.Ordinal);
        }

        Assert.Contains("REVOKE DELETE ON notifications.notification_templates FROM paqueteria_outbox_executor;", AddRecipientNoticeLane.DownSql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_v2_settle_accepts_only_the_closed_code_allowlist()
    {
        // Every messaging result code of the external channels is accepted, nothing else beyond the lane's own reasons.
        Assert.Subset(AddRecipientNoticeLane.ExternalCodes.ToHashSet(StringComparer.Ordinal), MessagingResultCodes.All.ToHashSet(StringComparer.Ordinal));
        Assert.Equal(
            MessagingResultCodes.All.Concat(
                [NotificationErrorCodes.AmbiguousTimeout, "TRACKING_LINK_UNAVAILABLE"]).Concat(AddRecipientNoticeLane.CancellationCodes)
                .Order(StringComparer.Ordinal),
            AddRecipientNoticeLane.ExternalCodes.Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                NotificationErrorCodes.ProviderAccepted,
                NotificationErrorCodes.ProviderTransient,
                NotificationErrorCodes.ProviderPermanent,
                NotificationErrorCodes.ProviderAmbiguous,
            ],
            AddRecipientNoticeLane.InAppCodes);
        foreach (var code in AddRecipientNoticeLane.InAppCodes.Concat(AddRecipientNoticeLane.ExternalCodes))
        {
            Assert.Matches("^[A-Z_]+$", code);
            Assert.Contains($"'{code}'", AddRecipientNoticeLane.ApplySql, StringComparison.Ordinal);
        }

        foreach (var code in AddRecipientNoticeLane.CancellationCodes)
        {
            Assert.Contains($"'{code}'", AddRecipientNoticeLane.CancelSql, StringComparison.Ordinal);
        }

        var apply = AddRecipientNoticeLane.ApplySql;
        Assert.Contains("(p_outcome='AMBIGUOUS_FAILED')<>(p_code='AMBIGUOUS_TIMEOUT')", apply, StringComparison.Ordinal);
        Assert.Contains("v_outcome=CASE WHEN p_outcome='AMBIGUOUS' AND v_notification.channel='WHATSAPP'", apply, StringComparison.Ordinal);
        Assert.Contains("v_attempts=CASE WHEN v_outcome='CANCELLED' THEN v_notification.attempts ELSE v_notification.attempts+1 END;", apply, StringComparison.Ordinal);
        Assert.Contains("v_outbox_status=CASE WHEN v_outcome IN ('SUCCESS','CANCELLED') THEN 'PROCESSED'", apply, StringComparison.Ordinal);
        Assert.Contains("AND lease_expires_at>clock_timestamp() AND topic='notifications.send-requested' FOR UPDATE;", apply, StringComparison.Ordinal);
        Assert.Contains("'CANCELLED',p_code,NULL,p_occurred_at,NULL);", AddRecipientNoticeLane.CancelSql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rollback_refuses_while_rows_need_the_new_shape_and_restores_the_published_shape()
    {
        var down = AddRecipientNoticeLane.DownSql;
        Assert.Contains(AddRecipientNoticeLane.DowngradeBlockedMessage, down, StringComparison.Ordinal);
        // The guard runs before anything is dropped or deleted.
        var guard = down.IndexOf(AddRecipientNoticeLane.DowngradeBlockedMessage, StringComparison.Ordinal);
        Assert.True(guard < down.IndexOf("DELETE FROM", StringComparison.Ordinal));
        Assert.True(guard < down.IndexOf("DROP ", StringComparison.Ordinal));
        foreach (var condition in new[]
                 {
                     "recipient_kind<>'OWNER_ORG_USER'",
                     "status IN ('DELIVERED','CANCELLED')",
                     "provider_reference IS NOT NULL",
                     "FROM notifications.recipient_opt_outs",
                     "topic='orders.recipient-notice-requested' AND status IN ('PENDING','RETRY','PROCESSING')",
                     "payload->>'schema_version'='notification-status-changed-v2'",
                 })
        {
            Assert.Contains(condition, down[..guard], StringComparison.Ordinal);
        }

        // The published 8-argument settle and delivery read come back byte for byte.
        Assert.Contains(AddRecipientNoticeLane.PublishedApplySql, down, StringComparison.Ordinal);
        Assert.Contains(AddRecipientNoticeLane.PublishedReadDeliverySql, down, StringComparison.Ordinal);
        Assert.Contains(
            AddRecipientNoticeLane.PublishedApplySql.Replace("CREATE FUNCTION ", "CREATE OR REPLACE FUNCTION ", StringComparison.Ordinal),
            FailAmbiguousWhatsAppNotifications.UpSql,
            StringComparison.Ordinal);
        Assert.Contains(AddRecipientNoticeLane.PublishedReadDeliverySql, AddTenantSafeOutboxNotifications.UpSql, StringComparison.Ordinal);
        Assert.Contains("DROP FUNCTION security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamptz,timestamptz);", AddRecipientNoticeLane.UpSql, StringComparison.Ordinal);

        // The published constraints come back with their names.
        foreach (var constraint in new[]
                 {
                     "ADD CONSTRAINT notification_templates_channel_check CHECK (channel='IN_APP')",
                     "ADD CONSTRAINT notification_templates_allowed_variables_check",
                     "ADD CONSTRAINT notifications_in_app_recipient_required CHECK (channel <> 'IN_APP' OR recipient_user_id IS NOT NULL)",
                     "ADD CONSTRAINT notification_status_events_status_check CHECK (status IN ('PENDING','SENT','FAILED'))",
                 })
        {
            Assert.Contains(constraint, down, StringComparison.Ordinal);
        }

        Assert.Single(Regex.Matches(down, "DROP TABLE"));
        Assert.Contains("DROP TABLE notifications.recipient_opt_outs;", down, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(down, "DELETE FROM"));
        Assert.DoesNotContain("DELETE FROM", AddRecipientNoticeLane.UpSql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", AddRecipientNoticeLane.UpSql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_step_is_the_recorded_tail_of_the_lane_and_the_routine_map_follows_it()
    {
        Assert.Equal(AddRecipientNoticeLane.MigrationId, E002NotificationStateReader.RecipientNoticeLaneMigrationId);
        Assert.Equal(AddRecipientNoticeLane.PublishedApplySignature, E002RoutineMap.PublishedApplyOutcomeSignature);
        Assert.True(string.CompareOrdinal(AddRecipientNoticeLane.MigrationId, CarryOutboxTraceContext.MigrationId) > 0);

        var before = E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true, true, true, true, true, true, true, true, true);
        var after = E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true, true, true, true, true, true, true, true, true,
            recipientNoticeLaneApplied: true);
        Assert.Equal(
            [AddRecipientNoticeLane.PublishedApplySignature],
            before.Select(static entry => entry.Signature).Except(after.Select(static entry => entry.Signature)));
        Assert.Equal(
            new[]
            {
                AddRecipientNoticeLane.ApplySignature,
                AddRecipientNoticeLane.CancelSignature,
                AddRecipientNoticeLane.ExpandSignature,
                AddRecipientNoticeLane.OptOutSignature,
            }.Order(StringComparer.Ordinal),
            after.Select(static entry => entry.Signature).Except(before.Select(static entry => entry.Signature)).Order(StringComparer.Ordinal));
        Assert.All(
            after.Where(entry => entry.Signature.Contains("recipient", StringComparison.Ordinal) ||
                                 entry.Signature.Contains("cancel_notification", StringComparison.Ordinal)),
            entry =>
            {
                Assert.Equal("paqueteria_outbox_executor", entry.Owner);
                Assert.Equal(["paqueteria_worker"], entry.Grantees);
            });
        Assert.EndsWith("_PLUS_RTMAPIROLE_PLUS_NTFRECIPIENT_V1",
            E002RoutineMap.Name(E002RoutineMapState.Applied, true, true, true, true, true, true, true, true, true, true, true,
                recipientNoticeLaneApplied: true),
            StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => E002RoutineMap.Select(
            E002RoutineMapState.Pending, false, recipientNoticeLaneApplied: true));
    }

    [Fact]
    public void The_recipient_templates_are_the_AI24_notices_and_carry_no_text()
    {
        var contract = File.ReadAllText(TestPath("docs/normative/v0.6/specs/AI-24_RUNTIME_HARDENING_CONTRACT.yaml"));
        foreach (var key in AddRecipientNoticeLane.RecipientTemplateKeys)
        {
            Assert.Contains($"template_key: {key}", contract, StringComparison.Ordinal);
            Assert.Contains($"('{key}')", AddRecipientNoticeLane.ProvisionSql, StringComparison.Ordinal);
        }

        Assert.Equal(4, AddRecipientNoticeLane.RecipientTemplateKeys.Count);
        // WhatsApp text lives in WhatsApp Manager, e-mail text in configuration: the row has no body and one variable.
        Assert.Contains("SELECT NEW.id,k.template_key,1,c.channel,NULL,'[\"order_public_id\"]'::jsonb", AddRecipientNoticeLane.ProvisionSql, StringComparison.Ordinal);
        Assert.Contains("CROSS JOIN (VALUES ('WHATSAPP'),('EMAIL')) c(channel);", AddRecipientNoticeLane.ProvisionSql, StringComparison.Ordinal);
        Assert.DoesNotContain("Ver seguimiento", AddRecipientNoticeLane.UpSql, StringComparison.Ordinal);
    }

    private static string Header(string definition) =>
        definition[..(definition.IndexOf("AS $fn$", StringComparison.Ordinal) + "AS $fn$".Length)];

    private static string Body(string definition) =>
        definition[(definition.IndexOf("AS $fn$", StringComparison.Ordinal) + "AS $fn$".Length)..];

    [GeneratedRegex(@"(^|[^A-Za-z_])EXECUTE([^A-Za-z_]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExecuteKeyword();

    [GeneratedRegex(@"(^|[^A-Za-z_])RETURNING([^A-Za-z_]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ReturningKeyword();

    [GeneratedRegex(@"SET search_path=pg_catalog,[a-z,]*pg_temp AS \$fn\$")]
    private static partial Regex PinnedSearchPath();

    private static string TestPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Paqueteria.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException(), relative);
    }
}
