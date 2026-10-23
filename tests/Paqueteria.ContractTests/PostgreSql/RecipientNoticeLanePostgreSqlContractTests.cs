using System.Text.Json;
using Notifications.Infrastructure.Persistence.Migrations;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.ContractTests.PostgreSql.Fixtures;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-003, AI-06 and AI-18 declared contract, AI-24 <c>recipient_notices</c>):
/// the Notifications lane step for WhatsApp and e-mail recipient notices on PostgreSQL - catalog, grants, FORCE RLS,
/// cross-tenant isolation, one Notification per order event, lease loss, the v2 code allowlist and the CANCELLED path.
/// No producer writes <c>orders.recipient-notice-requested</c> yet (AI-08 NTF-004), so each request row here is
/// written by the test administrator exactly as AI-24 <c>recipient_notices.producer</c> declares it.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class RecipientNoticeLanePostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly TimeSpan Mazatlan = TimeSpan.FromHours(-7);

    [PostgreSqlContractFact]
    public async Task Lane_functions_store_and_templates_match_the_declared_contract()
    {
        // Every function: owned by the outbox executor, SECURITY DEFINER, pinned search_path, EXECUTE for the Worker only.
        foreach (var signature in new[]
                 {
                     AddRecipientNoticeLane.ExpandSignature,
                     AddRecipientNoticeLane.ApplySignature,
                     AddRecipientNoticeLane.CancelSignature,
                     AddRecipientNoticeLane.OptOutSignature,
                     AddRecipientNoticeLane.ReadDeliverySignature,
                 })
        {
            Assert.Equal(
                "paqueteria_outbox_executor|true|paqueteria_outbox_executor:EXECUTE,paqueteria_worker:EXECUTE",
                await AdminScalarAsync<string>(
                    """
                    SELECT pg_get_userbyid(p.proowner) || '|' || p.prosecdef::text || '|' ||
                      (SELECT string_agg(pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type, ','
                         ORDER BY pg_get_userbyid(acl.grantee) COLLATE "C") FROM aclexplode(p.proacl) acl)
                    FROM pg_proc p WHERE p.oid=to_regprocedure(@signature)
                    """,
                    new NpgsqlParameter("signature", signature)));
            Assert.False(await AdminScalarAsync<bool>(
                "SELECT has_function_privilege('paqueteria_app',@signature,'EXECUTE') OR has_function_privilege('public',@signature,'EXECUTE') OR has_function_privilege('paqueteria_realtime',@signature,'EXECUTE')",
                new NpgsqlParameter("signature", signature)));
            Assert.Matches(
                "^search_path=pg_catalog, .*pg_temp$",
                await AdminScalarAsync<string>(
                    "SELECT proconfig[1] FROM pg_proc WHERE oid=to_regprocedure(@signature)",
                    new NpgsqlParameter("signature", signature)));
        }

        // The published 8-argument settle is gone (the Worker moved to v2 with this step).
        Assert.True(await AdminScalarAsync<bool>(
            $"SELECT to_regprocedure('{AddRecipientNoticeLane.PublishedApplySignature}') IS NULL"));

        // The opt-out store: forced row security without any policy; no runtime role holds any privilege; the outbox
        // executor reads it only.
        Assert.True(await AdminScalarAsync<bool>(
            """
            SELECT c.relrowsecurity AND c.relforcerowsecurity
              AND NOT EXISTS (SELECT 1 FROM pg_policy WHERE polrelid=c.oid)
              AND NOT has_table_privilege('paqueteria_app',c.oid,'SELECT')
              AND NOT has_table_privilege('paqueteria_app',c.oid,'INSERT')
              AND NOT has_table_privilege('paqueteria_worker',c.oid,'SELECT')
              AND NOT has_table_privilege('paqueteria_worker',c.oid,'INSERT')
              AND NOT has_table_privilege('paqueteria_worker',c.oid,'UPDATE')
              AND NOT has_table_privilege('paqueteria_worker',c.oid,'DELETE')
              AND NOT has_table_privilege('paqueteria_realtime',c.oid,'SELECT')
              AND has_table_privilege('paqueteria_outbox_executor',c.oid,'SELECT')
              AND NOT has_table_privilege('paqueteria_outbox_executor',c.oid,'INSERT')
              AND NOT has_table_privilege('paqueteria_outbox_executor',c.oid,'DELETE')
            FROM pg_class c WHERE c.oid='notifications.recipient_opt_outs'::regclass
            """));
        await using (var worker = await fixture.WorkerDataSource.OpenConnectionAsync())
        await using (var transaction = await worker.BeginTransactionAsync())
        {
            await ExecuteAsync(worker, transaction, "SET LOCAL ROLE paqueteria_worker");
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(worker, transaction, "SELECT count(*) FROM notifications.recipient_opt_outs"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        // The request topic belongs to the NOTIFICATIONS lane; nothing else moved.
        Assert.True(await AdminScalarAsync<bool>(
            """
            SELECT security.resolve_outbox_consumer('orders.recipient-notice-requested')='NOTIFICATIONS'
              AND security.resolve_outbox_consumer('orders.created')='NOTIFICATIONS'
              AND security.resolve_outbox_consumer('notifications.send-requested')='NOTIFICATIONS'
              AND security.resolve_outbox_consumer('notifications.status-changed')='REALTIME'
              AND security.resolve_outbox_consumer('orders.status-changed')='REALTIME'
              AND security.resolve_outbox_consumer('dispatch.order-status-reaction-requested')='DISPATCH'
            """));

        // A new organization gets its four recipient notice templates on both channels, without text, provisioned like
        // the NTF-001 one.
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-003 Templates");
        Assert.Equal(
            "orders.created.operations:IN_APP:true:[\"occurred_at\", \"order_public_id\", \"order_status\"]," +
            string.Join(',', AddRecipientNoticeLane.RecipientTemplateKeys.Order(StringComparer.Ordinal).SelectMany(key => new[]
            {
                $"{key}:EMAIL:false:[\"order_public_id\"]",
                $"{key}:WHATSAPP:false:[\"order_public_id\"]",
            })),
            await AdminScalarAsync<string>(
                """
                SELECT string_agg(template_key || ':' || channel || ':' || (body IS NOT NULL)::text || ':' || allowed_variables::text,
                  ',' ORDER BY template_key COLLATE "C", channel COLLATE "C")
                FROM notifications.notification_templates WHERE owner_org_id=@owner AND version=1
                """,
                new NpgsqlParameter("owner", owner)));

        // The closed catalog: a text for WhatsApp, a recipient key on IN_APP, an unknown key or variable are refused, and
        // template versions stay immutable.
        foreach (var insert in new[]
                 {
                     "('recipient.delivered',2,'WHATSAPP','Hola','[\"order_public_id\"]')",
                     "('recipient.delivered',2,'IN_APP',NULL,'[\"order_public_id\"]')",
                     "('recipient.returned',1,'EMAIL',NULL,'[\"order_public_id\"]')",
                     "('recipient.delivered',2,'EMAIL',NULL,'[\"order_public_id\",\"recipient_phone\"]')",
                     "('recipient.delivered',2,'SMS',NULL,'[\"order_public_id\"]')",
                     "('orders.created.operations',2,'IN_APP',NULL,'[\"occurred_at\",\"order_public_id\",\"order_status\"]')",
                 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAdminAsync(
                "INSERT INTO notifications.notification_templates(template_key,version,channel,body,allowed_variables,owner_org_id) " +
                $"SELECT k,v,c,b,a::jsonb,@owner FROM (VALUES {insert}) t(k,v,c,b,a)",
                new NpgsqlParameter("owner", owner)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
        }

        var immutable = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAdminAsync(
            "UPDATE notifications.notification_templates SET allowed_variables='[\"order_public_id\"]' WHERE owner_org_id=@owner AND channel='WHATSAPP'",
            new NpgsqlParameter("owner", owner)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, immutable.SqlState);

        // The opt-out store is append-only even for the deployment principal.
        await ExecuteAdminAsync(
            "INSERT INTO notifications.recipient_opt_outs(contact_hash,channel,key_version,source,recorded_at) VALUES (@hash,'EMAIL',1,'EMAIL_LINK',clock_timestamp())",
            new NpgsqlParameter("hash", Hash(0x11)));
        foreach (var mutation in new[]
                 {
                     "UPDATE notifications.recipient_opt_outs SET key_version=2 WHERE contact_hash=@hash",
                     "DELETE FROM notifications.recipient_opt_outs WHERE contact_hash=@hash",
                 })
        {
            var appendOnly = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAdminAsync(
                mutation, new NpgsqlParameter("hash", Hash(0x11))));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, appendOnly.SqlState);
        }

        // Its shape: a 32-byte hash only, the declared channels and sources, one row per hash and channel.
        foreach (var (hash, channel, source) in new[]
                 {
                     (new byte[31], "EMAIL", "EMAIL_LINK"),
                     (Hash(0x12), "SMS", "DISPATCHER"),
                     (Hash(0x13), "EMAIL", "RECIPIENT_REPLY"),
                     (Hash(0x14), "WHATSAPP", "EMAIL_LINK"),
                     (Hash(0x15), "WHATSAPP", "OPERATOR"),
                 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAdminAsync(
                "INSERT INTO notifications.recipient_opt_outs(contact_hash,channel,key_version,source,recorded_at) VALUES (@hash,@channel,1,@source,clock_timestamp())",
                new NpgsqlParameter("hash", hash), new NpgsqlParameter("channel", channel), new NpgsqlParameter("source", source)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
        }

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAdminAsync(
            "INSERT INTO notifications.recipient_opt_outs(contact_hash,channel,key_version,source,recorded_at) VALUES (@hash,'EMAIL',2,'DISPATCHER',clock_timestamp())",
            new NpgsqlParameter("hash", Hash(0x11))));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [PostgreSqlContractFact]
    public async Task Expansion_creates_one_notification_per_channel_without_recipient_data_and_only_once_per_order_event()
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-003 Expansion");
        var (order, orderEvent, publicId) = await RecipientNoticeSeed.InsertOrderAsync(admin, owner);
        var occurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        const string traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var source = await RecipientNoticeSeed.InsertRequestAsync(admin, owner, order, orderEvent, "IN_TRANSIT", occurredAt, traceParent);
        var lease = await LeaseAsync(source);

        // A foreign lease changes nothing.
        Assert.Equal("LEASE_LOST", await ExpandAsync(source, Guid.NewGuid(), publicId, ["EMAIL", "WHATSAPP"]));
        Assert.Equal(0, await CountAsync("SELECT count(*)::integer FROM notifications.notifications WHERE source_event_id=@id", source));

        Assert.Equal("SOURCE_EXPANDED", await ExpandAsync(source, lease, publicId, ["EMAIL", "WHATSAPP"]));
        Assert.Equal("PROCESSED:SOURCE_EXPANDED:true", await OutboxStateAsync(source));

        // WhatsApp first, then e-mail; each a PENDING ORDER_DESTINATION recipient notice of the order event, with only
        // the número de guía as a variable and no recipient value, its status event, its v2 status-changed row and its
        // send request carrying the producing trace.
        await using (var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT n.channel,n.recipient_kind,n.status,n.attempts,n.version,n.template_key,n.template_version,
              n.variables_snapshot::text,n.recipient_user_id IS NULL AND n.recipient_ciphertext IS NULL AND n.pii_key_version IS NULL,
              n.order_id,n.order_event_id,
              (SELECT string_agg(h.status || ':' || h.attempts,',') FROM notifications.notification_status_events h WHERE h.notification_id=n.id),
              (SELECT e.payload::text FROM platform.outbox_events e WHERE e.topic='notifications.status-changed' AND e.aggregate_id=n.id),
              (SELECT e.payload::text || '|' || e.trace_parent || '|' || e.priority FROM platform.outbox_events e
                 WHERE e.topic='notifications.send-requested' AND e.aggregate_id=n.id)
            FROM notifications.notifications n WHERE n.source_event_id=@source ORDER BY n.created_at,
              CASE n.channel WHEN 'WHATSAPP' THEN 0 ELSE 1 END
            """))
        {
            command.Parameters.AddWithValue("source", source);
            await using var reader = await command.ExecuteReaderAsync();
            foreach (var channel in new[] { "WHATSAPP", "EMAIL" })
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(channel, reader.GetString(0));
                Assert.Equal("ORDER_DESTINATION", reader.GetString(1));
                Assert.Equal("PENDING", reader.GetString(2));
                Assert.Equal(0, reader.GetInt32(3));
                Assert.Equal(1, reader.GetInt32(4));
                Assert.Equal("recipient.in-transit", reader.GetString(5));
                Assert.Equal(1, reader.GetInt32(6));
                Assert.Equal($"{{\"order_public_id\": \"{publicId}\"}}", reader.GetString(7));
                Assert.True(reader.GetBoolean(8));
                Assert.Equal(order, reader.GetGuid(9));
                Assert.Equal(orderEvent, reader.GetGuid(10));
                Assert.Equal("PENDING:0", reader.GetString(11));
                using (var payload = JsonDocument.Parse(reader.GetString(12)))
                {
                    Assert.Equal(
                        ["attempts", "channel", "notice", "notification_id", "occurred_at", "order_id", "schema_version", "status"],
                        payload.RootElement.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
                    Assert.Equal("notification-status-changed-v2", payload.RootElement.GetProperty("schema_version").GetString());
                    Assert.Equal("IN_TRANSIT", payload.RootElement.GetProperty("notice").GetString());
                    Assert.Equal(order, payload.RootElement.GetProperty("order_id").GetGuid());
                    Assert.Equal(channel, payload.RootElement.GetProperty("channel").GetString());
                }

                var send = reader.GetString(13).Split('|');
                Assert.Contains("notification-send-requested-v1", send[0], StringComparison.Ordinal);
                Assert.Equal(traceParent, send[1]);
                Assert.Equal("50", send[2]);
            }

            Assert.False(await reader.ReadAsync());
        }

        // A second request for the same order event (a redelivery) adds nothing: one Notification per owner, order
        // event, channel and template key.
        var replay = await RecipientNoticeSeed.InsertRequestAsync(admin, owner, order, orderEvent, "IN_TRANSIT", occurredAt);
        Assert.Equal("SOURCE_EXPANDED", await ExpandAsync(replay, await LeaseAsync(replay), publicId, ["WHATSAPP", "EMAIL"]));
        Assert.Equal(2, await CountAsync(
            "SELECT count(*)::integer FROM notifications.notifications WHERE order_event_id=@id", orderEvent));
        Assert.Equal(2, await CountAsync(
            "SELECT count(*)::integer FROM platform.outbox_events WHERE topic='notifications.send-requested' AND aggregate_id IN (SELECT id FROM notifications.notifications WHERE order_event_id=@id)",
            orderEvent));
        var unique = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAdminAsync(
            """
            INSERT INTO notifications.notifications(
              id,owner_org_id,order_id,order_event_id,recipient_kind,channel,status,attempts,version,
              template_key,template_version,variables_snapshot,source_event_id,created_at,updated_at)
            SELECT gen_random_uuid(),owner_org_id,order_id,order_event_id,recipient_kind,channel,status,attempts,version,
              template_key,template_version,variables_snapshot,gen_random_uuid(),created_at,updated_at
            FROM notifications.notifications WHERE order_event_id=@id AND channel='EMAIL'
            """,
            new NpgsqlParameter("id", orderEvent)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, unique.SqlState);

        // A recipient notice never holds a recipient value or a user, and an IN_APP one always has its user.
        foreach (var shape in new[]
                 {
                     "UPDATE notifications.notifications SET recipient_ciphertext=decode('00','hex'),pii_key_version='x' WHERE order_event_id=@id",
                     "UPDATE notifications.notifications SET recipient_user_id=gen_random_uuid() WHERE order_event_id=@id",
                     "UPDATE notifications.notifications SET channel='IN_APP' WHERE order_event_id=@id",
                     "UPDATE notifications.notifications SET status='DELIVERED' WHERE order_event_id=@id",
                     "UPDATE notifications.notifications SET provider_reference='a b' WHERE order_event_id=@id",
                 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAdminAsync(shape, new NpgsqlParameter("id", orderEvent)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
        }

        // Cross-tenant isolation: the owner organization reads its recipient notices under FORCE RLS; another reads none.
        Assert.Equal(2, await TenantCountAsync(owner, orderEvent));
        var foreign = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-003 Foreign");
        Assert.Equal(0, await TenantCountAsync(foreign, orderEvent));

        // The v2 delivery read: no template body for WhatsApp or e-mail, the order, its event and the event time.
        var whatsapp = await AdminScalarAsync<Guid>(
            "SELECT id FROM notifications.notifications WHERE order_event_id=@id AND channel='WHATSAPP'",
            new NpgsqlParameter("id", orderEvent));
        Assert.Equal(
            $"WHATSAPP|PENDING|recipient.in-transit|true|ORDER_DESTINATION|{order}|{orderEvent}|true",
            await WorkerScalarAsync<string>(
                """
                SELECT channel || '|' || status || '|' || template_key || '|' || (template_body IS NULL)::text || '|' ||
                  recipient_kind || '|' || order_id::text || '|' || order_event_id::text || '|' ||
                  (abs(extract(epoch FROM created_at - @occurred_at)) < 0.001)::text
                FROM security.read_notification_delivery(@id,@owner)
                """,
                new NpgsqlParameter("id", whatsapp), new NpgsqlParameter("owner", owner),
                new NpgsqlParameter("occurred_at", occurredAt)));
        Assert.Equal(0, await WorkerScalarAsync<long>(
            "SELECT count(*) FROM security.read_notification_delivery(@id,@owner)",
            new NpgsqlParameter("id", whatsapp), new NpgsqlParameter("owner", foreign)));

        await RetireAsync(owner);
    }

    [Theory]
    [InlineData("disabled", "RECIPIENT_NOTICES_DISABLED")]
    [InlineData("stale", "NOTICE_STALE")]
    [InlineData("no-channel", "NO_ELIGIBLE_CHANNEL")]
    public async Task Expansion_settles_without_creating_anything_when_notices_are_off_stale_or_without_a_channel(
        string scenario,
        string expected)
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-003 Settle " + scenario);
        var (order, orderEvent, publicId) = await RecipientNoticeSeed.InsertOrderAsync(admin, owner);
        var occurredAt = scenario == "stale" ? DateTimeOffset.UtcNow.AddHours(-13) : DateTimeOffset.UtcNow.AddMinutes(-1);
        var source = await RecipientNoticeSeed.InsertRequestAsync(admin, owner, order, orderEvent, "DELIVERED", occurredAt);
        var lease = await LeaseAsync(source);

        // With notices off nothing is read: the Worker passes no public id, channel or setting.
        var result = scenario == "disabled"
            ? await WorkerScalarAsync<string>(
                "SELECT security.expand_recipient_notice_request(@source,@lease,false,NULL,NULL,NULL,NULL,NULL)",
                new NpgsqlParameter("source", source), new NpgsqlParameter("lease", lease))
            : await ExpandAsync(source, lease, publicId, scenario == "no-channel" ? [] : ["WHATSAPP"]);

        Assert.Equal(expected, result);
        Assert.Equal($"PROCESSED:{expected}:true", await OutboxStateAsync(source));
        Assert.Equal(0, await CountAsync("SELECT count(*)::integer FROM notifications.notifications WHERE source_event_id=@id", source));
        Assert.Equal(0, await CountAsync(
            "SELECT count(*)::integer FROM platform.outbox_events WHERE owner_org_id=@id AND topic<>'orders.recipient-notice-requested'",
            owner));
    }

    [Fact]
    public async Task Expansion_refuses_a_request_outside_the_declared_payload_or_settings()
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-003 Invalid");
        var (order, orderEvent, publicId) = await RecipientNoticeSeed.InsertOrderAsync(admin, owner);
        var source = await RecipientNoticeSeed.InsertRequestAsync(admin, owner, order, orderEvent, "DELIVERED", DateTimeOffset.UtcNow);
        var lease = await LeaseAsync(source);
        foreach (var (channels, publicIdValue, maximumDelay, start, end) in new (string[], string?, string, string, string)[]
                 {
                     (["SMS"], publicId, "12 hours", "08:00", "21:00"),
                     (["EMAIL", "EMAIL"], publicId, "12 hours", "08:00", "21:00"),
                     (["EMAIL"], null, "12 hours", "08:00", "21:00"),
                     (["EMAIL"], "ORD 1", "12 hours", "08:00", "21:00"),
                     (["EMAIL"], publicId, "25 hours", "08:00", "21:00"),
                     (["EMAIL"], publicId, "12 hours", "21:00", "08:00"),
                 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => WorkerScalarAsync<string>(
                "SELECT security.expand_recipient_notice_request(@source,@lease,true,@delay::interval,@start::time,@end::time,@public_id,@channels)",
                new NpgsqlParameter("source", source), new NpgsqlParameter("lease", lease),
                new NpgsqlParameter("delay", maximumDelay), new NpgsqlParameter("start", start), new NpgsqlParameter("end", end),
                new NpgsqlParameter("public_id", NpgsqlDbType.Text) { Value = (object?)publicIdValue ?? DBNull.Value },
                new NpgsqlParameter<string[]>("channels", channels)));
            Assert.Equal("22023", refused.SqlState);
        }

        // A payload that is not the recipient-notice-requested-v1 contract is refused even with notices off.
        foreach (var payload in new[]
                 {
                     $$"""{"schema_version":"recipient-notice-requested-v1","order_event_id":"{{orderEvent}}","order_id":"{{order}}","notice":"RETURNED","occurred_at":"2026-10-20T10:00:00Z"}""",
                     $$"""{"schema_version":"recipient-notice-requested-v1","order_event_id":"{{orderEvent}}","order_id":"{{Guid.NewGuid()}}","notice":"DELIVERED","occurred_at":"2026-10-20T10:00:00Z"}""",
                     $$"""{"schema_version":"recipient-notice-requested-v1","order_event_id":"{{orderEvent}}","order_id":"{{order}}","notice":"DELIVERED","occurred_at":"2026-10-20T10:00:00Z","recipient_phone":"x"}""",
                     $$"""{"schema_version":"recipient-notice-requested-v2","order_event_id":"{{orderEvent}}","order_id":"{{order}}","notice":"DELIVERED","occurred_at":"2026-10-20T10:00:00Z"}""",
                     $$"""{"schema_version":"recipient-notice-requested-v1","order_event_id":"{{orderEvent}}","order_id":"{{order}}","notice":"DELIVERED","occurred_at":"2026-13-45T10:00:00Z"}""",
                 })
        {
            var id = Guid.NewGuid();
            await ExecuteAdminAsync(
                """
                INSERT INTO platform.outbox_events(
                  id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
                  payload,priority,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,created_at)
                VALUES(@id,@owner,jsonb_build_object('organization_ids',jsonb_build_array(@owner::text)),
                  'orders.recipient-notice-requested','Order',@order,2,@payload::jsonb,50,'PROCESSING',1,clock_timestamp(),
                  clock_timestamp(),'ntf003-contract',@lease,clock_timestamp()+interval '1 minute',clock_timestamp())
                """,
                new NpgsqlParameter("id", id), new NpgsqlParameter("owner", owner), new NpgsqlParameter("order", order),
                new NpgsqlParameter("payload", payload), new NpgsqlParameter("lease", lease));
            var refused = await Assert.ThrowsAsync<PostgresException>(() => WorkerScalarAsync<string>(
                "SELECT security.expand_recipient_notice_request(@source,@lease,false,NULL,NULL,NULL,NULL,NULL)",
                new NpgsqlParameter("source", id), new NpgsqlParameter("lease", lease)));
            Assert.Equal("22023", refused.SqlState);
        }

        await RetireAsync(owner);
    }

    [Theory]
    // AI-24 recipient_notices.quiet_hours (E4-P10, 08:00 to 21:00 America/Mazatlan, UTC-7 all year since 2022): a
    // request at 21:30 waits for 08:00 the next day, one at 06:00 for 08:00 the same day, one at 10:00 leaves at once.
    [InlineData(21, 30, 1)]
    [InlineData(6, 0, 0)]
    [InlineData(10, 0, -1)]
    // The two boundaries. AI-24 recipient_notices.quiet_hours.window: "messages leave only from 08:00 (inclusive) to
    // 21:00 (exclusive) local time". 08:00:00 is inside the window, so the notice leaves at once; 21:00:00 is outside
    // it, so the notice waits for 08:00 the next day.
    [InlineData(8, 0, -1)]
    [InlineData(21, 0, 1)]
    public async Task Expansion_holds_a_notice_outside_the_quiet_hours_window_until_its_next_start(int hour, int minute, int dayOffset)
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, $"NTF-003 Window {hour:D2}{minute:D2}");
        var (order, orderEvent, publicId) = await RecipientNoticeSeed.InsertOrderAsync(admin, owner);

        // The next instant at that local time, so the request is fresh (never past the stale bound) and the window is
        // computed from it rather than from the test's own clock.
        var nowLocal = DateTimeOffset.UtcNow.ToOffset(Mazatlan);
        var local = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, hour, minute, 0, Mazatlan);
        if (local <= nowLocal.AddMinutes(5))
        {
            local = local.AddDays(1);
        }

        var source = await RecipientNoticeSeed.InsertRequestAsync(admin, owner, order, orderEvent, "TRACKING_LINK", local.ToUniversalTime());
        Assert.Equal("SOURCE_EXPANDED", await ExpandAsync(source, await LeaseAsync(source), publicId, ["EMAIL"]));

        var expected = dayOffset < 0
            ? local
            : new DateTimeOffset(local.Year, local.Month, local.Day, 8, 0, 0, Mazatlan).AddDays(dayOffset);
        var availableAt = await AdminScalarAsync<DateTime>(
            """
            SELECT e.available_at FROM platform.outbox_events e
            JOIN notifications.notifications n ON n.id=e.aggregate_id
            WHERE e.topic='notifications.send-requested' AND n.source_event_id=@source
            """,
            new NpgsqlParameter("source", source));
        Assert.Equal(expected.UtcDateTime, DateTime.SpecifyKind(availableAt, DateTimeKind.Utc));
        await RetireAsync(owner);
    }

    [PostgreSqlContractFact]
    public async Task Settle_v2_keeps_the_lease_and_whatsapp_rules_and_accepts_only_the_closed_code_allowlist()
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-003 Settle v2");
        var (whatsapp, whatsappSend, _) = await ExpandOneAsync(admin, owner, "WHATSAPP");
        var (email, emailSend, _) = await ExpandOneAsync(admin, owner, "EMAIL");

        // Lease loss: a foreign lease or an expired one settles nothing.
        var lease = await LeaseAsync(whatsappSend);
        Assert.False(await ApplyAsync(whatsappSend, Guid.NewGuid(), whatsapp, 1, "SUCCESS", "MESSAGING_ACCEPTED", "wamid.A1"));
        await ExecuteAdminAsync(
            "UPDATE platform.outbox_events SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id=@id",
            new NpgsqlParameter("id", whatsappSend));
        Assert.False(await ApplyAsync(whatsappSend, lease, whatsapp, 1, "SUCCESS", "MESSAGING_ACCEPTED", "wamid.A1"));
        Assert.False(await CancelAsync(whatsappSend, lease, whatsapp, 1, "NOTICE_SUPERSEDED"));
        Assert.Equal("PENDING:0:1", await NotificationStateAsync(whatsapp));
        lease = await LeaseAsync(whatsappSend);

        // The closed allowlist: every combination outside it is refused before anything changes.
        foreach (var (outcome, code, reference) in new (string, string, string?)[]
                 {
                     ("SUCCESS", "MESSAGING_UNKNOWN", null),
                     ("SUCCESS", "SYNTHETIC_ACCEPTED", null),
                     ("SUCCESS", "MESSAGING_PROVIDER_REJECTED", null),
                     ("PERMANENT", "MESSAGING_ACCEPTED", null),
                     ("PERMANENT", "AMBIGUOUS_TIMEOUT", null),
                     ("TRANSIENT", "TRACKING_LINK_UNAVAILABLE", null),
                     ("CANCELLED", "MESSAGING_PROVIDER_REJECTED", null),
                     ("PERMANENT", "RECIPIENT_OPTED_OUT", null),
                     ("CANCELLED", "RECIPIENT_OPTED_OUT", "wamid.A1"),
                     ("SUCCESS", "MESSAGING_ACCEPTED", "+52 667 000 0000"),
                     ("SUCCESS", "MESSAGING_ACCEPTED", new string('a', 257)),
                     ("DELIVERED", "MESSAGING_ACCEPTED", null),
                 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                ApplyAsync(whatsappSend, lease, whatsapp, 1, outcome, code, reference));
            Assert.Equal("22023", refused.SqlState);
        }

        Assert.Equal("PENDING:0:1", await NotificationStateAsync(whatsapp));

        // SUCCESS stores the provider reference, unique per channel.
        Assert.True(await ApplyAsync(whatsappSend, lease, whatsapp, 1, "SUCCESS", "MESSAGING_ACCEPTED", "wamid.HBgM-ntf003_A1=="));
        Assert.Equal("SENT:1:2", await NotificationStateAsync(whatsapp));
        Assert.Equal("wamid.HBgM-ntf003_A1==", await AdminScalarAsync<string>(
            "SELECT provider_reference FROM notifications.notifications WHERE id=@id", new NpgsqlParameter("id", whatsapp)));
        Assert.Equal("PROCESSED:-:true", await OutboxStateAsync(whatsappSend));
        Assert.Equal("v2:WHATSAPP:SENT:1", await LatestStatusChangedAsync(whatsapp));

        // E-mail: an ambiguous outcome keeps the retry, a synthetic IN_APP code is refused, then a permanent failure.
        var emailLease = await LeaseAsync(emailSend);
        var family = await Assert.ThrowsAsync<PostgresException>(() =>
            ApplyAsync(emailSend, emailLease, email, 1, "TRANSIENT", "SYNTHETIC_TRANSIENT", null));
        Assert.Equal("22023", family.SqlState);
        var missingRetry = await Assert.ThrowsAsync<PostgresException>(() =>
            ApplyAsync(emailSend, emailLease, email, 1, "AMBIGUOUS", "MESSAGING_PROVIDER_TIMEOUT", "op-1", retryLater: false));
        Assert.Equal("22023", missingRetry.SqlState);
        Assert.True(await ApplyAsync(emailSend, emailLease, email, 1, "AMBIGUOUS", "MESSAGING_PROVIDER_TIMEOUT", "3d2c1b0a-0000-4000-8000-00000000ac50"));
        Assert.Equal("PENDING:1:2", await NotificationStateAsync(email));
        Assert.Equal("RETRY:MESSAGING_PROVIDER_TIMEOUT:false", await OutboxStateAsync(emailSend));
        emailLease = await LeaseAsync(emailSend);
        Assert.True(await ApplyAsync(emailSend, emailLease, email, 2, "PERMANENT", "TRACKING_LINK_UNAVAILABLE", null));
        Assert.Equal("FAILED:2:3", await NotificationStateAsync(email));
        Assert.Equal("DEAD:TRACKING_LINK_UNAVAILABLE:true", await OutboxStateAsync(emailSend));
        Assert.Equal("v2:EMAIL:FAILED:2", await LatestStatusChangedAsync(email));
        // The same provider reference cannot belong to two notifications of one channel.
        var second = (await ExpandOneAsync(admin, owner, "WHATSAPP")).Notification;
        var reused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAdminAsync(
            "UPDATE notifications.notifications SET provider_reference='wamid.HBgM-ntf003_A1==' WHERE id=@id",
            new NpgsqlParameter("id", second)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, reused.SqlState);

        // NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02 still holds for a recipient notice: never retried.
        var (ambiguous, ambiguousSend, _) = await ExpandOneAsync(admin, owner, "WHATSAPP");
        Assert.True(await ApplyAsync(ambiguousSend, await LeaseAsync(ambiguousSend), ambiguous, 1, "AMBIGUOUS", "MESSAGING_PROVIDER_TIMEOUT", null));
        Assert.Equal("FAILED:1:2", await NotificationStateAsync(ambiguous));
        Assert.Equal("DEAD:AMBIGUOUS_TIMEOUT:true", await OutboxStateAsync(ambiguousSend));
        Assert.Equal("v2:WHATSAPP:FAILED:1", await LatestStatusChangedAsync(ambiguous));

        // NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03 too: a stale WhatsApp lease is never requeued.
        var (stale, staleSend, _) = await ExpandOneAsync(admin, owner, "WHATSAPP");
        await LeaseAsync(staleSend);
        await ExecuteAdminAsync(
            "UPDATE platform.outbox_events SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id=@id",
            new NpgsqlParameter("id", staleSend));
        await WorkerScalarAsync<long>(
            "SELECT count(*) FROM security.recover_stale_notifications_outbox('ntf003-stale',1000,5,interval '30 seconds')");
        Assert.Equal("FAILED:1:2", await NotificationStateAsync(stale));
        Assert.Equal("DEAD:AMBIGUOUS_TIMEOUT:true", await OutboxStateAsync(staleSend));
        Assert.Equal("v2:WHATSAPP:FAILED:1", await LatestStatusChangedAsync(stale));

        await RetireAsync(owner);
    }

    [PostgreSqlContractFact]
    public async Task Cancellation_ends_a_recipient_notice_cancelled_without_counting_an_attempt()
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-003 Cancel");
        var (notification, send, order) = await ExpandOneAsync(admin, owner, "WHATSAPP");
        var lease = await LeaseAsync(send);

        var invalid = await Assert.ThrowsAsync<PostgresException>(() => CancelAsync(send, lease, notification, 1, "MESSAGING_PROVIDER_REJECTED"));
        Assert.Equal("22023", invalid.SqlState);
        Assert.False(await CancelAsync(send, lease, notification, 7, "RECIPIENT_OPTED_OUT"));
        Assert.True(await CancelAsync(send, lease, notification, 1, "RECIPIENT_OPTED_OUT"));
        Assert.False(await CancelAsync(send, lease, notification, 2, "RECIPIENT_OPTED_OUT"));

        Assert.Equal("CANCELLED:0:2", await NotificationStateAsync(notification));
        Assert.Equal("PROCESSED:RECIPIENT_OPTED_OUT:true", await OutboxStateAsync(send));
        Assert.Equal(
            "PENDING:0:-,CANCELLED:0:RECIPIENT_OPTED_OUT",
            await AdminScalarAsync<string>(
                "SELECT string_agg(status || ':' || attempts || ':' || COALESCE(provider_attempt_code,'-'),',' ORDER BY version) FROM notifications.notification_status_events WHERE notification_id=@id",
                new NpgsqlParameter("id", notification)));
        Assert.Null(await AdminScalarAsync<object?>(
            "SELECT last_attempt_at FROM notifications.notifications WHERE id=@id", new NpgsqlParameter("id", notification)));
        using (var payload = JsonDocument.Parse(await AdminScalarAsync<string>(
                   """
                   SELECT payload::text FROM platform.outbox_events
                   WHERE topic='notifications.status-changed' AND aggregate_id=@id AND aggregate_version=2
                   """,
                   new NpgsqlParameter("id", notification))))
        {
            Assert.Equal("CANCELLED", payload.RootElement.GetProperty("status").GetString());
            Assert.Equal("WHATSAPP", payload.RootElement.GetProperty("channel").GetString());
            Assert.Equal(order, payload.RootElement.GetProperty("order_id").GetGuid());
            Assert.Equal("DELIVERED", payload.RootElement.GetProperty("notice").GetString());
            Assert.Equal(0, payload.RootElement.GetProperty("attempts").GetInt32());
            Assert.DoesNotContain("RECIPIENT_OPTED_OUT", payload.RootElement.GetRawText(), StringComparison.Ordinal);
        }

        // The CANCELLED outcome of the v2 settle is the same path, and IN_APP has none.
        var (other, otherSend, _) = await ExpandOneAsync(admin, owner, "EMAIL");
        Assert.True(await ApplyAsync(otherSend, await LeaseAsync(otherSend), other, 1, "CANCELLED", "NOTICE_SUPERSEDED", null));
        Assert.Equal("CANCELLED:0:2", await NotificationStateAsync(other));
        Assert.Equal("PROCESSED:NOTICE_SUPERSEDED:true", await OutboxStateAsync(otherSend));

        var inAppSource = Guid.NewGuid();
        var inAppOrder = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
              payload,priority,status,attempts,available_at,created_at)
            VALUES(@id,@owner,jsonb_build_object('organization_ids',jsonb_build_array(@owner::text)),
              'orders.created','Order',@order,1,'{}',50,'PENDING',0,clock_timestamp(),clock_timestamp())
            """,
            new NpgsqlParameter("id", inAppSource), new NpgsqlParameter("owner", owner), new NpgsqlParameter("order", inAppOrder));
        Assert.Equal("SOURCE_EXPANDED", await WorkerScalarAsync<string>(
            "SELECT security.expand_order_created_notifications(@source,@lease,@order,'ORD_abcdefghijklmnopqrstuv','DRAFT',clock_timestamp(),@recipients)",
            new NpgsqlParameter("source", inAppSource), new NpgsqlParameter("lease", await LeaseAsync(inAppSource)),
            new NpgsqlParameter("order", inAppOrder), new NpgsqlParameter<Guid[]>("recipients", [Guid.NewGuid()])));
        var inApp = await AdminScalarAsync<Guid>(
            "SELECT id FROM notifications.notifications WHERE source_event_id=@id", new NpgsqlParameter("id", inAppSource));
        var inAppSend = await AdminScalarAsync<Guid>(
            "SELECT id FROM platform.outbox_events WHERE topic='notifications.send-requested' AND aggregate_id=@id",
            new NpgsqlParameter("id", inApp));
        var inAppLease = await LeaseAsync(inAppSend);
        var noCancel = await Assert.ThrowsAsync<PostgresException>(() => CancelAsync(inAppSend, inAppLease, inApp, 1, "NOTICE_SUPERSEDED"));
        Assert.Equal("22023", noCancel.SqlState);
        var noReference = await Assert.ThrowsAsync<PostgresException>(() =>
            ApplyAsync(inAppSend, inAppLease, inApp, 1, "SUCCESS", "SYNTHETIC_ACCEPTED", "wamid.X"));
        Assert.Equal("22023", noReference.SqlState);
        var noMessagingCode = await Assert.ThrowsAsync<PostgresException>(() =>
            ApplyAsync(inAppSend, inAppLease, inApp, 1, "SUCCESS", "MESSAGING_ACCEPTED", null));
        Assert.Equal("22023", noMessagingCode.SqlState);
        // The published IN_APP path is unchanged: SENT, with a v2 status-changed row that carries no order or notice.
        Assert.True(await ApplyAsync(inAppSend, inAppLease, inApp, 1, "SUCCESS", "SYNTHETIC_ACCEPTED", null));
        Assert.Equal("SENT:1:2", await NotificationStateAsync(inApp));
        using (var payload = JsonDocument.Parse(await AdminScalarAsync<string>(
                   "SELECT payload::text FROM platform.outbox_events WHERE topic='notifications.status-changed' AND aggregate_id=@id AND aggregate_version=2",
                   new NpgsqlParameter("id", inApp))))
        {
            Assert.Equal(
                ["attempts", "channel", "notification_id", "occurred_at", "schema_version", "status"],
                payload.RootElement.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal("notification-status-changed-v2", payload.RootElement.GetProperty("schema_version").GetString());
        }

        await RetireAsync(owner);
    }

    [PostgreSqlContractFact]
    public async Task Opt_out_lookup_reads_only_keyed_hashes_of_one_channel()
    {
        await ExecuteAdminAsync(
            """
            INSERT INTO notifications.recipient_opt_outs(contact_hash,channel,key_version,source,recorded_at)
            VALUES (@whatsapp,'WHATSAPP',1,'RECIPIENT_REPLY',clock_timestamp()),(@email,'EMAIL',2,'DISPATCHER',clock_timestamp())
            """,
            new NpgsqlParameter("whatsapp", Hash(0x21)), new NpgsqlParameter("email", Hash(0x22)));

        Assert.True(await OptedOutAsync("WHATSAPP", Hash(0x21)));
        Assert.True(await OptedOutAsync("WHATSAPP", Hash(0x20), Hash(0x21)));
        Assert.False(await OptedOutAsync("EMAIL", Hash(0x21)));
        Assert.True(await OptedOutAsync("EMAIL", Hash(0x22)));
        Assert.False(await OptedOutAsync("WHATSAPP", Hash(0x23)));
        foreach (var (channel, hashes) in new (string?, byte[][]?)[]
                 {
                     ("SMS", [Hash(0x21)]),
                     (null, [Hash(0x21)]),
                     ("WHATSAPP", []),
                     ("WHATSAPP", null),
                     ("WHATSAPP", [new byte[31]]),
                     ("WHATSAPP", Enumerable.Range(0, 17).Select(static value => Hash((byte)value)).ToArray()),
                 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => OptedOutAsync(channel, hashes));
            Assert.Equal("22023", refused.SqlState);
        }

        // The API's role cannot ask (AI-18: EXECUTE for paqueteria_worker only).
        await using var app = await fixture.AppDataSource.OpenConnectionAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ExecuteAsync(app, transaction, "SET LOCAL ROLE paqueteria_app");
        var denied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, transaction,
            "SELECT security.is_recipient_opted_out('WHATSAPP',ARRAY[decode(repeat('21',32),'hex')])"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    private async Task<(Guid Notification, Guid Send, Guid Order)> ExpandOneAsync(NpgsqlConnection admin, Guid owner, string channel)
    {
        var (order, orderEvent, publicId) = await RecipientNoticeSeed.InsertOrderAsync(admin, owner);
        var source = await RecipientNoticeSeed.InsertRequestAsync(admin, owner, order, orderEvent, "DELIVERED", DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal("SOURCE_EXPANDED", await WorkerScalarAsync<string>(
            "SELECT security.expand_recipient_notice_request(@source,@lease,true,interval '12 hours',time '00:00',time '23:59:59.999999',@public_id,@channels)",
            new NpgsqlParameter("source", source), new NpgsqlParameter("lease", await LeaseAsync(source)),
            new NpgsqlParameter("public_id", publicId), new NpgsqlParameter<string[]>("channels", [channel])));
        var notification = await AdminScalarAsync<Guid>(
            "SELECT id FROM notifications.notifications WHERE source_event_id=@id", new NpgsqlParameter("id", source));
        var send = await AdminScalarAsync<Guid>(
            "SELECT id FROM platform.outbox_events WHERE topic='notifications.send-requested' AND aggregate_id=@id",
            new NpgsqlParameter("id", notification));
        return (notification, send, order);
    }

    private Task<string> ExpandAsync(Guid source, Guid lease, string publicId, string[] channels) =>
        WorkerScalarAsync<string>(
            "SELECT security.expand_recipient_notice_request(@source,@lease,true,interval '12 hours',time '08:00',time '21:00',@public_id,@channels)",
            new NpgsqlParameter("source", source), new NpgsqlParameter("lease", lease),
            new NpgsqlParameter("public_id", publicId), new NpgsqlParameter<string[]>("channels", channels));

    private Task<bool> ApplyAsync(
        Guid outbox,
        Guid lease,
        Guid notification,
        int version,
        string outcome,
        string code,
        string? reference,
        bool retryLater = true)
    {
        // A retried outcome needs its next attempt time; retryLater false sends none.
        DateTimeOffset? retryAt = retryLater && outcome is "TRANSIENT" or "AMBIGUOUS" ? DateTimeOffset.UtcNow.AddMinutes(1) : null;
        return WorkerScalarAsync<bool>(
            "SELECT security.apply_notification_outcome(@outbox,@lease,@notification,@version,@outcome,@code,@reference,clock_timestamp(),@available_at)",
            new NpgsqlParameter("outbox", outbox), new NpgsqlParameter("lease", lease),
            new NpgsqlParameter("notification", notification), new NpgsqlParameter("version", version),
            new NpgsqlParameter("outcome", outcome), new NpgsqlParameter("code", code),
            new NpgsqlParameter("reference", NpgsqlDbType.Text) { Value = (object?)reference ?? DBNull.Value },
            new NpgsqlParameter("available_at", NpgsqlDbType.TimestampTz) { Value = (object?)retryAt ?? DBNull.Value });
    }

    private Task<bool> CancelAsync(Guid outbox, Guid lease, Guid notification, int version, string code) =>
        WorkerScalarAsync<bool>(
            "SELECT security.cancel_notification(@outbox,@lease,@notification,@version,@code,clock_timestamp())",
            new NpgsqlParameter("outbox", outbox), new NpgsqlParameter("lease", lease),
            new NpgsqlParameter("notification", notification), new NpgsqlParameter("version", version),
            new NpgsqlParameter("code", code));

    private Task<bool> OptedOutAsync(string? channel, params byte[][]? hashes) =>
        WorkerScalarAsync<bool>(
            "SELECT security.is_recipient_opted_out(@channel,@hashes)",
            new NpgsqlParameter("channel", NpgsqlDbType.Text) { Value = (object?)channel ?? DBNull.Value },
            new NpgsqlParameter("hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = (object?)hashes ?? DBNull.Value });

    /// <summary>
    /// The lease a claim would give, set by the test administrator on one row only, so that no other row of the shared
    /// database is claimed (the claim functions themselves are covered by the NTF-001 contract tests).
    /// </summary>
    private Task<Guid> LeaseAsync(Guid id) =>
        AdminScalarAsync<Guid>(
            """
            UPDATE platform.outbox_events SET status='PROCESSING',attempts=attempts+1,locked_at=clock_timestamp(),
              locked_by='ntf003-contract',lease_token=gen_random_uuid(),lease_expires_at=clock_timestamp()+interval '1 minute',
              available_at=LEAST(available_at,clock_timestamp())
            WHERE id=@id AND status IN ('PENDING','RETRY','PROCESSING')
            RETURNING lease_token
            """,
            new NpgsqlParameter("id", id));

    /// <summary>Every row this test left claimable for the organization ends DEAD, so no other test claims it.</summary>
    private Task RetireAsync(Guid owner) =>
        ExecuteAdminAsync(
            """
            UPDATE platform.outbox_events SET status='DEAD',processed_at=clock_timestamp(),
              locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
            WHERE owner_org_id=@owner AND status IN ('PENDING','RETRY','PROCESSING')
            """,
            new NpgsqlParameter("owner", owner));

    private Task<string> NotificationStateAsync(Guid id) =>
        AdminScalarAsync<string>(
            "SELECT status || ':' || attempts || ':' || version FROM notifications.notifications WHERE id=@id",
            new NpgsqlParameter("id", id));

    private Task<string> OutboxStateAsync(Guid id) =>
        AdminScalarAsync<string>(
            "SELECT status || ':' || COALESCE(last_error,'-') || ':' || (lease_token IS NULL AND processed_at IS NOT NULL)::text FROM platform.outbox_events WHERE id=@id",
            new NpgsqlParameter("id", id));

    private Task<string> LatestStatusChangedAsync(Guid notification) =>
        AdminScalarAsync<string>(
            """
            SELECT replace(payload->>'schema_version','notification-status-changed-','') || ':' || (payload->>'channel') || ':' ||
              (payload->>'status') || ':' || (payload->>'attempts')
            FROM platform.outbox_events WHERE topic='notifications.status-changed' AND aggregate_id=@id
            ORDER BY aggregate_version DESC LIMIT 1
            """,
            new NpgsqlParameter("id", notification));

    private async Task<int> TenantCountAsync(Guid organization, Guid orderEvent)
    {
        await using var tenant = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", Guid.NewGuid(), [organization]);
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::integer FROM notifications.notifications WHERE order_event_id=@id", tenant.Connection, tenant.Transaction);
        command.Parameters.AddWithValue("id", orderEvent);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private Task<int> CountAsync(string sql, Guid id) =>
        AdminScalarAsync<int>(sql, new NpgsqlParameter("id", id));

    private static byte[] Hash(byte value) => Enumerable.Repeat(value, 32).ToArray();

    private async Task<T> AdminScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? default! : (T)value!;
    }

    private async Task<T> WorkerScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_worker");
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        var result = (T)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return result;
    }

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }
}
