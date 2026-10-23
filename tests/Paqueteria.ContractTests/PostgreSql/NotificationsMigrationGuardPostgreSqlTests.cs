using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Notifications.Infrastructure.Persistence;
using Notifications.Infrastructure.Persistence.Migrations;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;
using Testcontainers.PostgreSql;

namespace Paqueteria.ContractTests.PostgreSql;

[Trait("Category", "PostgreSqlContract")]
public sealed class NotificationsMigrationGuardPostgreSqlTests
{
    private const string Image = "postgis/postgis:18-3.6@sha256:b410052c6f0d7d37b83cac1369df144e1c843971155dea3317961001704d0a9d";

    [PostgreSqlContractFact]
    public async Task Realtime_topic_migrations_preserve_function_history_and_schema_owners_across_up_down_up()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder(Image)
            .WithDatabase("paqueteria_ext001_routing")
            .WithUsername("postgres")
            .WithPassword(password)
            .WithCleanUp(true)
            .Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await AssertSecuritySchemaBaselineAsync(connection);
        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await using var context = CreateNotificationsContext(connection);
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync();
        await AssertRoutingStateAsync(connection, "REALTIME", "REALTIME", routeMigrationApplied: true);

        await migrator.MigrateAsync(RouteExternalOfferRealtime.MigrationId);
        await AssertRoutingStateAsync(connection, "REALTIME", "UNROUTED", routeMigrationApplied: false);

        await migrator.MigrateAsync(AddTenantSafeOutboxNotifications.MigrationId);
        await AssertRoutingStateAsync(connection, "UNROUTED", "UNROUTED", routeMigrationApplied: false);

        await migrator.MigrateAsync();
        await AssertRoutingStateAsync(connection, "REALTIME", "REALTIME", routeMigrationApplied: true);
    }

    [PostgreSqlContractFact]
    public async Task D8_dispatch_lane_down_is_blocked_by_active_reaction_rows_and_reversible_once_drained()
    {
        // D8-OUTBOX-LANE-DISPATCH: 20260927000200_AddDispatchOutboxLane.Down fails closed while any
        // dispatch.order-status-reaction-requested row is PENDING, RETRY or PROCESSING.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder(Image)
            .WithDatabase("paqueteria_d8_dispatch_lane")
            .WithUsername("postgres")
            .WithPassword(password)
            .WithCleanUp(true)
            .Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await using var context = CreateNotificationsContext(connection);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync();
        await AssertDispatchLaneAsync(connection, installed: true);

        var owner = Guid.NewGuid();
        await ExecuteAsync(connection,
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES(@owner,'D8 Guard','D8 Guard','BUSINESS')",
            new NpgsqlParameter("owner", owner));
        foreach (var status in new[] { "PENDING", "RETRY", "PROCESSING" })
        {
            var row = Guid.NewGuid();
            await ExecuteAsync(connection,
                """
                INSERT INTO platform.outbox_events(
                  id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
                  payload,priority,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,created_at)
                VALUES(@id,@owner,jsonb_build_object('organization_ids',jsonb_build_array(@owner::text)),
                  'dispatch.order-status-reaction-requested','Order',gen_random_uuid(),2,'{}',50,@status,
                  CASE WHEN @status='PENDING' THEN 0 ELSE 1 END,clock_timestamp(),
                  CASE WHEN @status='PROCESSING' THEN clock_timestamp() END,
                  CASE WHEN @status='PROCESSING' THEN 'd8-guard' END,
                  CASE WHEN @status='PROCESSING' THEN gen_random_uuid() END,
                  CASE WHEN @status='PROCESSING' THEN clock_timestamp()+interval '1 minute' END,
                  clock_timestamp())
                """,
                new NpgsqlParameter("id", row),
                new NpgsqlParameter("owner", owner),
                new NpgsqlParameter("status", status));
            await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");

            var blocked = await Assert.ThrowsAnyAsync<Exception>(() =>
                migrator.MigrateAsync(RouteManualRouteRealtime.MigrationId));
            var postgres = blocked as PostgresException ?? blocked.InnerException as PostgresException;
            Assert.NotNull(postgres);
            Assert.Equal(
                "D8 DISPATCH lane rollback blocked by active Dispatch-owned rows",
                postgres.MessageText);
            await AssertDispatchLaneAsync(connection, installed: true);

            // Settling the row lifts the block.
            await ExecuteAsync(connection,
                """
                UPDATE platform.outbox_events
                SET status='PROCESSED',processed_at=clock_timestamp(),
                    locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
                WHERE id=@id
                """,
                new NpgsqlParameter("id", row));
        }

        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync(RouteManualRouteRealtime.MigrationId);
        await AssertDispatchLaneAsync(connection, installed: false);

        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync();
        await AssertDispatchLaneAsync(connection, installed: true);
    }

    [PostgreSqlContractFact]
    public async Task Ambiguous_whatsapp_settle_migration_replaces_only_the_outcome_body_across_up_down_up()
    {
        // NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02: 20261002000100_FailAmbiguousWhatsAppNotifications swaps the
        // apply_notification_outcome body and nothing else; Down restores the NTF-001 body.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder(Image)
            .WithDatabase("paqueteria_ntf_whatsapp_ambiguous")
            .WithUsername("postgres")
            .WithPassword(password)
            .WithCleanUp(true)
            .Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await using var context = CreateNotificationsContext(connection);
        var migrator = context.GetService<IMigrator>();

        // NTF-RECIPIENT-NOTICES-2026-10-20: the recipient notice lane step replaces this 8-argument settle with v2, so
        // the published body is inspected at the step before it.
        await migrator.MigrateAsync(CarryOutboxTraceContext.MigrationId);
        await AssertAmbiguousWhatsAppSettleAsync(connection, installed: true);

        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync(AddDispatchOutboxLane.MigrationId);
        await AssertAmbiguousWhatsAppSettleAsync(connection, installed: false);

        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync(CarryOutboxTraceContext.MigrationId);
        await AssertAmbiguousWhatsAppSettleAsync(connection, installed: true);
    }

    [PostgreSqlContractFact]
    public async Task Stale_whatsapp_lease_migration_replaces_only_the_recovery_body_across_up_down_up()
    {
        // NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03: 20261003000100_FailStaleWhatsAppNotificationLeases swaps the
        // recover_stale_notifications_outbox body and nothing else; Down restores the previous body byte for byte.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder(Image)
            .WithDatabase("paqueteria_ntf_whatsapp_stale_lease")
            .WithUsername("postgres")
            .WithPassword(password)
            .WithCleanUp(true)
            .Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await using var context = CreateNotificationsContext(connection);
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(FailAmbiguousWhatsAppNotifications.MigrationId);
        var previous = await AssertStaleWhatsAppRecoveryAsync(connection, installed: false);

        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync();
        var installedBody = await AssertStaleWhatsAppRecoveryAsync(connection, installed: true);
        Assert.NotEqual(previous, installedBody);

        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync(FailAmbiguousWhatsAppNotifications.MigrationId);
        Assert.Equal(previous, await AssertStaleWhatsAppRecoveryAsync(connection, installed: false));

        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync();
        Assert.Equal(installedBody, await AssertStaleWhatsAppRecoveryAsync(connection, installed: true));
    }

    [PostgreSqlContractFact]
    public async Task Recipient_notice_lane_up_down_up_restores_the_published_catalog_and_refuses_while_rows_need_it()
    {
        // NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-003): 20261020000100_AddRecipientNoticeLane. Up provisions the four
        // recipient notice templates of every organization that holds the NTF-001 template; Down refuses while any row
        // needs the new shape and otherwise restores the published catalog exactly; Up again reproduces the same catalog.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder(Image)
            .WithDatabase("paqueteria_ntf003_lane")
            .WithUsername("postgres")
            .WithPassword(password)
            .WithCleanUp(true)
            .Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await using var context = CreateNotificationsContext(connection);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(CarryOutboxTraceContext.MigrationId);
        await ExecuteAsync(connection, "RESET ROLE");
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(connection, "NTF-003 Lane");
        var published = await NotificationsCatalogAsync(connection);
        Assert.Equal(1, await TemplateCountAsync(connection, owner));
        Assert.Equal("UNROUTED", await ScalarTextAsync(connection,
            "SELECT security.resolve_outbox_consumer('orders.recipient-notice-requested')"));

        // NTF-RECIPIENT-NOTICE-CAPS-2026-10-23: the later count step has its own Up, Down and Up test below; this one
        // stops at the recipient notice lane step.
        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync(AddRecipientNoticeLane.MigrationId);
        await ExecuteAsync(connection, "RESET ROLE");
        var installed = await NotificationsCatalogAsync(connection);
        Assert.NotEqual(published, installed);
        Assert.Equal(9, await TemplateCountAsync(connection, owner));
        Assert.Equal("NOTIFICATIONS", await ScalarTextAsync(connection,
            "SELECT security.resolve_outbox_consumer('orders.recipient-notice-requested')"));
        Assert.True(await ScalarBooleanAsync(connection,
            $"SELECT EXISTS (SELECT 1 FROM platform.__ef_migrations_history_notifications WHERE \"MigrationId\"='{AddRecipientNoticeLane.MigrationId}')"));

        // Every kind of row that needs the new shape blocks the rollback; once it is gone, the rollback can run.
        var (order, orderEvent, _) = await RecipientNoticeSeed.InsertOrderAsync(connection, owner);
        var inAppNotification = Guid.NewGuid();
        await ExecuteAsync(connection,
            """
            INSERT INTO notifications.notifications(
              id,owner_org_id,order_id,recipient_user_id,channel,status,attempts,version,
              template_key,template_version,variables_snapshot,source_event_id,created_at,updated_at)
            VALUES(@id,@owner,NULL,gen_random_uuid(),'IN_APP','PENDING',0,1,
              'orders.created.operations',1,'{}'::jsonb,gen_random_uuid(),clock_timestamp(),clock_timestamp())
            """,
            new NpgsqlParameter("id", inAppNotification), new NpgsqlParameter("owner", owner));
        var blockers = new (string Insert, string Clear)[]
        {
            (
                "INSERT INTO notifications.recipient_opt_outs(contact_hash,channel,key_version,source,recorded_at) " +
                "VALUES (decode(repeat('ab',32),'hex'),'WHATSAPP',1,'RECIPIENT_REPLY',clock_timestamp())",
                "ALTER TABLE notifications.recipient_opt_outs DISABLE TRIGGER recipient_opt_outs_append_only; " +
                "DELETE FROM notifications.recipient_opt_outs; " +
                "ALTER TABLE notifications.recipient_opt_outs ENABLE TRIGGER recipient_opt_outs_append_only"
            ),
            (
                $$"""
                INSERT INTO notifications.notifications(
                  id,owner_org_id,order_id,order_event_id,recipient_kind,channel,status,attempts,version,
                  template_key,template_version,variables_snapshot,source_event_id,created_at,updated_at)
                VALUES('{{Guid.NewGuid()}}','{{owner}}','{{order}}','{{orderEvent}}','ORDER_DESTINATION','EMAIL','PENDING',0,1,
                  'recipient.delivered',1,'{"order_public_id":"ORD_x"}'::jsonb,gen_random_uuid(),clock_timestamp(),clock_timestamp())
                """,
                "DELETE FROM notifications.notifications WHERE recipient_kind='ORDER_DESTINATION'"
            ),
            (
                $$"""
                INSERT INTO notifications.notification_status_events(
                  id,owner_org_id,notification_id,version,status,attempts,provider_attempt_code,occurred_at)
                VALUES(gen_random_uuid(),'{{owner}}','{{inAppNotification}}',2,'CANCELLED',0,'NOTICE_SUPERSEDED',clock_timestamp())
                """,
                "ALTER TABLE notifications.notification_status_events DISABLE TRIGGER notification_status_events_append_only; " +
                "DELETE FROM notifications.notification_status_events WHERE status='CANCELLED'; " +
                "ALTER TABLE notifications.notification_status_events ENABLE TRIGGER notification_status_events_append_only"
            ),
            (
                $$"""
                INSERT INTO platform.outbox_events(
                  id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
                  payload,priority,status,attempts,available_at,created_at)
                VALUES(gen_random_uuid(),'{{owner}}',jsonb_build_object('organization_ids',jsonb_build_array('{{owner}}')),
                  'orders.recipient-notice-requested','Order','{{order}}',2,'{}',50,'RETRY',1,clock_timestamp(),clock_timestamp())
                """,
                "UPDATE platform.outbox_events SET status='DEAD',processed_at=clock_timestamp() " +
                "WHERE topic='orders.recipient-notice-requested' AND status='RETRY'"
            ),
            (
                $$"""
                INSERT INTO platform.outbox_events(
                  id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
                  payload,priority,status,attempts,available_at,created_at)
                VALUES(gen_random_uuid(),'{{owner}}',jsonb_build_object('organization_ids',jsonb_build_array('{{owner}}')),
                  'notifications.status-changed','Notification','{{inAppNotification}}',2,
                  jsonb_build_object('schema_version','notification-status-changed-v2'),50,'PENDING',0,clock_timestamp(),clock_timestamp())
                """,
                "UPDATE platform.outbox_events SET status='PROCESSED',processed_at=clock_timestamp() " +
                "WHERE topic='notifications.status-changed' AND status='PENDING'"
            ),
        };
        foreach (var (insert, clear) in blockers)
        {
            await ExecuteAsync(connection, insert);
            await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
            var blocked = await Assert.ThrowsAnyAsync<Exception>(() =>
                migrator.MigrateAsync(CarryOutboxTraceContext.MigrationId));
            var postgres = blocked as PostgresException ?? blocked.InnerException as PostgresException;
            Assert.NotNull(postgres);
            Assert.Equal(AddRecipientNoticeLane.DowngradeBlockedMessage, postgres.MessageText);
            await ExecuteAsync(connection, "RESET ROLE");
            Assert.Equal(installed, await NotificationsCatalogAsync(connection));
            await ExecuteAsync(connection, clear);
        }

        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync(CarryOutboxTraceContext.MigrationId);
        await ExecuteAsync(connection, "RESET ROLE");
        Assert.Equal(published, await NotificationsCatalogAsync(connection));
        Assert.Equal(1, await TemplateCountAsync(connection, owner));
        Assert.Equal("UNROUTED", await ScalarTextAsync(connection,
            "SELECT security.resolve_outbox_consumer('orders.recipient-notice-requested')"));

        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync(AddRecipientNoticeLane.MigrationId);
        await ExecuteAsync(connection, "RESET ROLE");
        Assert.Equal(installed, await NotificationsCatalogAsync(connection));
        Assert.Equal(9, await TemplateCountAsync(connection, owner));
    }

    [PostgreSqlContractFact]
    public async Task Recipient_notice_count_step_up_down_up_adds_only_the_count_function_and_its_indexes()
    {
        // NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 (L1 "Función de conteo (Recommended)"; AI-08 NTF-005):
        // 20261023000100_AddRecipientNoticeCounts. Up adds security.read_recipient_notice_counts and its two partial
        // indexes and nothing else; Down restores the recipient notice lane catalog exactly, even with recipient notice
        // rows present (no row needs the step); Up again reproduces the same catalog.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder(Image)
            .WithDatabase("paqueteria_ntf005_counts")
            .WithUsername("postgres")
            .WithPassword(password)
            .WithCleanUp(true)
            .Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await using var context = CreateNotificationsContext(connection);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(AddRecipientNoticeLane.MigrationId);
        await ExecuteAsync(connection, "RESET ROLE");
        var lane = await NotificationsCatalogAsync(connection);
        Assert.True(await ScalarBooleanAsync(connection,
            $"SELECT to_regprocedure('{AddRecipientNoticeCounts.CountsSignature}') IS NULL"));

        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync();
        await ExecuteAsync(connection, "RESET ROLE");
        var counts = await NotificationsCatalogAsync(connection);
        var added = counts.Except(lane).ToArray();
        Assert.Empty(lane.Except(counts));
        Assert.Equal(3, added.Length);
        Assert.Contains(added, line => line.StartsWith(
            "routine security.read_recipient_notice_counts(uuid,uuid) owner=paqueteria_outbox_executor definer=true " +
            "config=search_path=pg_catalog, platform, notifications, pg_temp result=TABLE(order_channel_count integer, day_channel_count integer)",
            StringComparison.Ordinal) && line.EndsWith(
            "acl=paqueteria_outbox_executor:EXECUTE:false,paqueteria_worker:EXECUTE:false", StringComparison.Ordinal));
        Assert.Contains(
            "index notifications CREATE INDEX notifications_recipient_order_channel_idx ON notifications.notifications USING btree (owner_org_id, order_id, channel) WHERE (recipient_kind = 'ORDER_DESTINATION'::text)",
            added);
        Assert.Contains(
            "index notifications CREATE INDEX notifications_recipient_attempt_day_idx ON notifications.notifications USING btree (channel, last_attempt_at) WHERE ((recipient_kind = 'ORDER_DESTINATION'::text) AND (attempts > 0))",
            added);
        Assert.True(await ScalarBooleanAsync(connection,
            $"SELECT EXISTS (SELECT 1 FROM platform.__ef_migrations_history_notifications WHERE \"MigrationId\"='{AddRecipientNoticeCounts.MigrationId}')"));
        Assert.False(await ScalarBooleanAsync(connection,
            "SELECT has_table_privilege('paqueteria_worker','notifications.notifications','SELECT') " +
            $"OR has_function_privilege('paqueteria_app','{AddRecipientNoticeCounts.CountsSignature}','EXECUTE') " +
            "OR has_schema_privilege('paqueteria_outbox_executor','security','CREATE')"));

        // No row needs the step: a recipient notice that was attempted does not block its rollback.
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(connection, "NTF-005 Counts");
        var (order, orderEvent, _) = await RecipientNoticeSeed.InsertOrderAsync(connection, owner);
        await ExecuteAsync(connection,
            $$"""
            INSERT INTO notifications.notifications(
              id,owner_org_id,order_id,order_event_id,recipient_kind,channel,status,attempts,version,
              template_key,template_version,variables_snapshot,source_event_id,last_attempt_at,created_at,updated_at)
            VALUES(gen_random_uuid(),'{{owner}}','{{order}}','{{orderEvent}}','ORDER_DESTINATION','EMAIL','FAILED',1,2,
              'recipient.delivered',1,'{"order_public_id":"ORD_x"}'::jsonb,gen_random_uuid(),clock_timestamp(),
              clock_timestamp(),clock_timestamp())
            """);

        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync(AddRecipientNoticeLane.MigrationId);
        await ExecuteAsync(connection, "RESET ROLE");
        Assert.Equal(lane, await NotificationsCatalogAsync(connection));

        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync();
        await ExecuteAsync(connection, "RESET ROLE");
        Assert.Equal(counts, await NotificationsCatalogAsync(connection));
    }

    /// <summary>
    /// The catalog the recipient notice lane step touches: every column, constraint, index, trigger, policy, row security
    /// flag and privilege of the notifications schema, every routine of the security and notifications schemas (owner,
    /// SECURITY DEFINER, settings, result, body digest and EXECUTE list) and both schema ACLs. Identifiers are names, never
    /// OIDs or column numbers, so an Up, Down and Up again compares equal.
    /// </summary>
    private static async Task<IReadOnlyList<string>> NotificationsCatalogAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH tables AS (
              SELECT c.oid,c.relname,c.relrowsecurity,c.relforcerowsecurity,c.relacl,c.relowner
              FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
              WHERE n.nspname='notifications' AND c.relkind IN ('r','p'))
            SELECT 'column ' || t.relname || '.' || a.attname || ' ' || format_type(a.atttypid,a.atttypmod) ||
                   ' notnull=' || a.attnotnull || ' default=' || COALESCE(pg_get_expr(d.adbin,d.adrelid),'')
            FROM tables t JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum>0 AND NOT a.attisdropped
            LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
            UNION ALL
            SELECT 'constraint ' || t.relname || '.' || con.conname || ' ' || pg_get_constraintdef(con.oid)
            FROM tables t JOIN pg_constraint con ON con.conrelid=t.oid
            UNION ALL
            SELECT 'index ' || t.relname || ' ' || pg_get_indexdef(i.indexrelid)
            FROM tables t JOIN pg_index i ON i.indrelid=t.oid
            UNION ALL
            SELECT 'trigger ' || c.relname || ' ' || pg_get_triggerdef(tg.oid) || ' enabled=' || tg.tgenabled::text
            FROM pg_trigger tg JOIN pg_class c ON c.oid=tg.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE NOT tg.tgisinternal AND (n.nspname='notifications'
              OR tg.tgfoid=to_regprocedure('notifications.provision_default_templates()'))
            UNION ALL
            SELECT 'policy ' || t.relname || '.' || pol.polname || ' ' || COALESCE(pg_get_expr(pol.polqual,pol.polrelid),'') ||
                   ' check ' || COALESCE(pg_get_expr(pol.polwithcheck,pol.polrelid),'')
            FROM tables t JOIN pg_policy pol ON pol.polrelid=t.oid
            UNION ALL
            SELECT 'table ' || t.relname || ' rls=' || t.relrowsecurity || ' force=' || t.relforcerowsecurity ||
                   ' owner=' || pg_get_userbyid(t.relowner) || ' acl=' || COALESCE((
                     SELECT string_agg(pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type || ':' || acl.is_grantable,
                       ',' ORDER BY pg_get_userbyid(acl.grantee) COLLATE "C", acl.privilege_type COLLATE "C")
                     FROM aclexplode(t.relacl) acl),'')
            FROM tables t
            UNION ALL
            SELECT 'routine ' || p.oid::regprocedure::text || ' owner=' || pg_get_userbyid(p.proowner) ||
                   ' definer=' || p.prosecdef || ' config=' || COALESCE(array_to_string(p.proconfig,'|'),'') ||
                   ' result=' || pg_get_function_result(p.oid) || ' body=' || md5(p.prosrc) || ' acl=' || COALESCE((
                     SELECT string_agg(pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type || ':' || acl.is_grantable,
                       ',' ORDER BY pg_get_userbyid(acl.grantee) COLLATE "C")
                     FROM aclexplode(p.proacl) acl),'default')
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname IN ('security','notifications')
            UNION ALL
            SELECT 'schema ' || n.nspname || ' acl=' || (
                     SELECT string_agg(pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type || ':' || acl.is_grantable,
                       ',' ORDER BY pg_get_userbyid(acl.grantee) COLLATE "C", acl.privilege_type COLLATE "C")
                     FROM aclexplode(n.nspacl) acl)
            FROM pg_namespace n WHERE n.nspname IN ('security','notifications')
            ORDER BY 1
            """,
            connection);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<int> TemplateCountAsync(NpgsqlConnection connection, Guid owner)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::integer FROM notifications.notification_templates WHERE owner_org_id=@owner", connection);
        command.Parameters.AddWithValue("owner", owner);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ScalarTextAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> ScalarBooleanAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<string> AssertStaleWhatsAppRecoveryAsync(NpgsqlConnection connection, bool installed)
    {
        await ExecuteAsync(connection, "RESET ROLE");
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_get_userbyid(p.proowner),
                   p.prosecdef,
                   position('AMBIGUOUS_TIMEOUT' IN p.prosrc) > 0,
                   position('lease_token=v_request.lease_token' IN p.prosrc) > 0,
                   has_function_privilege('paqueteria_worker',p.oid,'EXECUTE'),
                   has_function_privilege('paqueteria_app',p.oid,'EXECUTE'),
                   has_function_privilege('public',p.oid,'EXECUTE'),
                   has_schema_privilege('paqueteria_outbox_executor','security','CREATE'),
                   EXISTS (SELECT 1 FROM platform.__ef_migrations_history_notifications
                           WHERE "MigrationId"=@migration_id),
                   p.proconfig::text,
                   pg_get_function_result(p.oid),
                   p.prosrc
            FROM pg_catalog.pg_proc p
            WHERE p.oid='security.recover_stale_notifications_outbox(text,integer,integer,interval)'::regprocedure
            """,
            connection);
        command.Parameters.AddWithValue("migration_id", FailStaleWhatsAppNotificationLeases.MigrationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("paqueteria_outbox_executor", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(installed, reader.GetBoolean(2));
        Assert.Equal(installed, reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
        Assert.Equal(installed, reader.GetBoolean(8));
        Assert.Equal("{\"search_path=pg_catalog, platform, security, pg_temp\"}", reader.GetString(9));
        Assert.Equal("SETOF platform.outbox_events", reader.GetString(10));
        var body = reader.GetString(11);
        Assert.False(await reader.ReadAsync());
        return body;
    }

    private static async Task AssertAmbiguousWhatsAppSettleAsync(NpgsqlConnection connection, bool installed)
    {
        await ExecuteAsync(connection, "RESET ROLE");
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_get_userbyid(p.proowner),
                   p.prosecdef,
                   position('AMBIGUOUS_FAILED' IN p.prosrc) > 0,
                   position('lease_token=p_lease_token' IN p.prosrc) > 0,
                   has_function_privilege('paqueteria_worker',p.oid,'EXECUTE'),
                   has_function_privilege('paqueteria_app',p.oid,'EXECUTE'),
                   has_schema_privilege('paqueteria_outbox_executor','security','CREATE'),
                   EXISTS (SELECT 1 FROM platform.__ef_migrations_history_notifications
                           WHERE "MigrationId"=@migration_id),
                   p.proconfig::text
            FROM pg_catalog.pg_proc p
            WHERE p.oid='security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamptz,timestamptz)'::regprocedure
            """,
            connection);
        command.Parameters.AddWithValue("migration_id", FailAmbiguousWhatsAppNotifications.MigrationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("paqueteria_outbox_executor", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(installed, reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
        Assert.Equal(installed, reader.GetBoolean(7));
        Assert.Equal("{\"search_path=pg_catalog, platform, notifications, security, pg_temp\"}", reader.GetString(8));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task AssertDispatchLaneAsync(NpgsqlConnection connection, bool installed)
    {
        // Inspected by the deployment principal: the migrator holds no EXECUTE on the resolver.
        await ExecuteAsync(connection, "RESET ROLE");
        await using var command = new NpgsqlCommand(
            """
            SELECT security.resolve_outbox_consumer('dispatch.order-status-reaction-requested'),
                   security.resolve_outbox_consumer('orders.status-changed'),
                   to_regprocedure('security.claim_dispatch_outbox(text,integer,interval)') IS NOT NULL,
                   to_regprocedure('security.requeue_stale_dispatch_outbox(interval,integer,integer)') IS NOT NULL,
                   EXISTS (SELECT 1 FROM platform.__ef_migrations_history_notifications
                           WHERE "MigrationId"=@migration_id),
                   has_schema_privilege('paqueteria_outbox_executor','security','CREATE')
            """,
            connection);
        command.Parameters.AddWithValue("migration_id", AddDispatchOutboxLane.MigrationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(installed ? "DISPATCH" : "UNROUTED", reader.GetString(0));
        Assert.Equal("REALTIME", reader.GetString(1));
        Assert.Equal(installed, reader.GetBoolean(2));
        Assert.Equal(installed, reader.GetBoolean(3));
        Assert.Equal(installed, reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
    }

    [PostgreSqlContractFact]
    public async Task Cutover_and_operational_rollback_are_fail_closed_on_active_owned_rows()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder(Image)
            .WithDatabase("paqueteria_ntf001_guards")
            .WithUsername("postgres")
            .WithPassword(password)
            .WithCleanUp(true)
            .Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var owner = Guid.NewGuid();
        var source = Guid.NewGuid();
        await ExecuteAsync(connection,
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES(@owner,'NTF Guard','NTF Guard','BUSINESS')",
            new NpgsqlParameter("owner", owner));
        await ExecuteAsync(connection,
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
              payload,priority,status,attempts,available_at,created_at)
            VALUES(@id,@owner,jsonb_build_object('organization_ids',jsonb_build_array(@owner::text)),
              'orders.created','Order',gen_random_uuid(),1,'{}',50,'PENDING',0,clock_timestamp(),clock_timestamp())
            """,
            new NpgsqlParameter("id", source), new NpgsqlParameter("owner", owner));
        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");

        var cutover = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connection, AddTenantSafeOutboxNotifications.UpSql));
        Assert.Contains("cutover blocked", cutover.MessageText, StringComparison.Ordinal);

        await ExecuteAsync(connection, "RESET ROLE");
        await ExecuteAsync(connection, "DELETE FROM platform.outbox_events WHERE id=@id", new NpgsqlParameter("id", source));
        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await using (var context = CreateNotificationsContext(connection))
        {
            // The NTF-001 operational rollback revokes the published 8-argument settle, which the
            // NTF-RECIPIENT-NOTICES-2026-10-20 lane step replaces: it runs where that step is not applied (or was rolled
            // back first, as the NTF-001 note says).
            await context.GetService<IMigrator>().MigrateAsync(CarryOutboxTraceContext.MigrationId);
        }
        await using (var migrationAssertion = new NpgsqlCommand(
            """
            SELECT EXISTS (
              SELECT 1 FROM platform.__ef_migrations_history_notifications
              WHERE "MigrationId"='20260815000100_AddTenantSafeOutboxNotifications'
            )
            """,
            connection))
        {
            Assert.True(await migrationAssertion.ExecuteScalarAsync() is true);
        }
        await ExecuteAsync(connection, "RESET ROLE");
        var active = Guid.NewGuid();
        await ExecuteAsync(connection,
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
              payload,priority,status,attempts,available_at,created_at)
            VALUES(@id,@owner,jsonb_build_object('organization_ids',jsonb_build_array(@owner::text)),
              'notifications.send-requested','Notification',gen_random_uuid(),1,'{}',50,'PENDING',0,clock_timestamp(),clock_timestamp())
            """,
            new NpgsqlParameter("id", active), new NpgsqlParameter("owner", owner));
        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");

        var rollback = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connection, AddTenantSafeOutboxNotifications.OperationalRollbackSql));
        Assert.Contains("rollback blocked", rollback.MessageText, StringComparison.Ordinal);

        await ExecuteAsync(connection, "RESET ROLE");
        await ExecuteAsync(connection,
            "UPDATE platform.outbox_events SET status='DEAD',processed_at=clock_timestamp() WHERE id=@id",
            new NpgsqlParameter("id", active));
        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await ExecuteAsync(connection, AddTenantSafeOutboxNotifications.OperationalRollbackSql);
        await ExecuteAsync(connection, "RESET ROLE");
        await using var assertion = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1 FROM platform.__ef_migrations_history_notifications
                WHERE "MigrationId"='20260815000100_AddTenantSafeOutboxNotifications'
              )
              AND to_regclass('notifications.notifications') IS NOT NULL
              AND to_regclass('notifications.notification_templates') IS NOT NULL
              AND to_regclass('notifications.notification_status_events') IS NOT NULL
              AND has_function_privilege('paqueteria_worker','security.claim_outbox(text,integer,interval)','EXECUTE')
              AND has_function_privilege('paqueteria_worker','security.requeue_stale_outbox(interval,integer,integer)','EXECUTE')
              AND NOT has_function_privilege('paqueteria_worker','security.claim_realtime_outbox(text,integer,interval)','EXECUTE')
              AND NOT has_function_privilege('paqueteria_worker','security.claim_notifications_outbox(text,integer,interval)','EXECUTE')
              AND NOT has_function_privilege('paqueteria_worker','security.claim_unowned_outbox(text,integer,interval)','EXECUTE')
              AND NOT has_function_privilege('paqueteria_worker','security.requeue_stale_realtime_outbox(interval,integer,integer)','EXECUTE')
              AND NOT has_function_privilege('paqueteria_worker','security.recover_stale_notifications_outbox(text,integer,integer,interval)','EXECUTE')
              AND NOT has_function_privilege('paqueteria_worker','security.requeue_stale_unowned_outbox(interval,integer,integer)','EXECUTE')
            """,
            connection);
        Assert.True(await assertion.ExecuteScalarAsync() is true);
    }

    [PostgreSqlContractFact]
    public async Task Ef_schema_downgrade_fails_closed_without_changing_history_schema_or_routing()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder(Image)
            .WithDatabase("paqueteria_ntf001_downgrade")
            .WithUsername("postgres")
            .WithPassword(password)
            .WithCleanUp(true)
            .Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);

        await using (var migrationConnection = new NpgsqlConnection(connectionString))
        {
            await migrationConnection.OpenAsync();
            await ExecuteAsync(migrationConnection, "SET ROLE paqueteria_migrator");
            await using (var context = CreateNotificationsContext(migrationConnection))
            {
                await context.Database.MigrateAsync();
                await using (var migrationAssertion = new NpgsqlCommand(
                    """
                    SELECT EXISTS (
                      SELECT 1 FROM platform.__ef_migrations_history_notifications
                      WHERE "MigrationId"='20260815000100_AddTenantSafeOutboxNotifications'
                    )
                    """,
                    migrationConnection))
                {
                    Assert.True(await migrationAssertion.ExecuteScalarAsync() is true);
                }

                var migrator = context.GetService<IMigrator>();
                var downgrade = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("0"));
                Assert.Equal("NTF001_SCHEMA_DOWNGRADE_NOT_SUPPORTED", downgrade.MessageText);
            }
            await ExecuteAsync(migrationConnection, "RESET ROLE");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var assertion = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1 FROM platform.__ef_migrations_history_notifications
                WHERE "MigrationId"='20260815000100_AddTenantSafeOutboxNotifications'
              )
              AND to_regclass('notifications.notifications') IS NOT NULL
              AND to_regclass('notifications.notification_templates') IS NOT NULL
              AND to_regclass('notifications.notification_status_events') IS NOT NULL
              AND NOT has_function_privilege('paqueteria_worker','security.claim_outbox(text,integer,interval)','EXECUTE')
              AND has_function_privilege('paqueteria_worker','security.claim_realtime_outbox(text,integer,interval)','EXECUTE')
              AND has_function_privilege('paqueteria_worker','security.claim_notifications_outbox(text,integer,interval)','EXECUTE')
              AND has_function_privilege('paqueteria_worker','security.claim_unowned_outbox(text,integer,interval)','EXECUTE')
            """,
            connection);
        Assert.True(await assertion.ExecuteScalarAsync() is true);
    }

    private static NotificationsDbContext CreateNotificationsContext(NpgsqlConnection connection)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(NotificationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_notifications", "platform");
            })
            .Options;
        return new NotificationsDbContext(options);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertRoutingStateAsync(
        NpgsqlConnection connection,
        string expectedExternalConsumer,
        string expectedRouteConsumer,
        bool routeMigrationApplied)
    {
        await using (var roleCommand = new NpgsqlCommand("SELECT current_user;", connection))
        {
            Assert.Equal("paqueteria_migrator", await roleCommand.ExecuteScalarAsync());
        }

        await ExecuteAsync(connection, "RESET ROLE");
        try
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT
                  security.resolve_outbox_consumer('dispatch.external-offer-changed'),
                  security.resolve_outbox_consumer('routes.route-changed'),
                  pg_get_userbyid(target_function.proowner),
                  pg_get_userbyid(history.relowner),
                  pg_get_userbyid(namespace.nspowner),
                  has_schema_privilege('paqueteria_outbox_executor','security','CREATE'),
                  EXISTS (
                    SELECT 1
                    FROM platform.__ef_migrations_history_notifications
                    WHERE "MigrationId"=@migration_id
                  )
                FROM pg_catalog.pg_proc target_function
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid=target_function.pronamespace
                CROSS JOIN pg_catalog.pg_class history
                JOIN pg_catalog.pg_namespace history_namespace ON history_namespace.oid=history.relnamespace
                WHERE namespace.nspname='security'
                  AND target_function.proname='resolve_outbox_consumer'
                  AND pg_get_function_identity_arguments(target_function.oid)='p_topic text'
                  AND history_namespace.nspname='platform'
                  AND history.relname='__ef_migrations_history_notifications';
                """,
                connection);
            command.Parameters.AddWithValue("migration_id", RouteManualRouteRealtime.MigrationId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(expectedExternalConsumer, reader.GetString(0));
            Assert.Equal(expectedRouteConsumer, reader.GetString(1));
            Assert.Equal("paqueteria_outbox_executor", reader.GetString(2));
            Assert.Equal("paqueteria_migrator", reader.GetString(3));
            Assert.Equal("paqueteria_migrator", reader.GetString(4));
            Assert.False(reader.GetBoolean(5));
            Assert.Equal(routeMigrationApplied, reader.GetBoolean(6));
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        }
    }

    private static async Task AssertSecuritySchemaBaselineAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
              pg_get_userbyid(nspowner),
              has_schema_privilege('paqueteria_outbox_executor','security','CREATE')
            FROM pg_catalog.pg_namespace
            WHERE nspname='security';
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("paqueteria_migrator", reader.GetString(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(await reader.ReadAsync());
    }
}
