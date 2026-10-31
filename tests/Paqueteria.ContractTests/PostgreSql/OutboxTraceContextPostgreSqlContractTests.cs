using System.Globalization;
using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Notifications.Infrastructure.Persistence;
using Notifications.Infrastructure.Persistence.Migrations;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13 (project owner, Q-PR7: "Columna nueva (Recommended)"): both outbox tables
/// keep the producing W3C trace context in <c>trace_parent</c>, of the exact stored shape only; every claim function
/// returns it with the row and the lease lifecycle and purge never change it; the Notifications writers copy the
/// originating event's context and never put it in a payload; and the Dispatch and Notifications lane steps go Up, Down
/// and Up again on a real PostgreSQL with an identical catalog each time.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class OutboxTraceContextPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string TraceA = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    private const string TraceB = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00";
    private const string TraceC = "00-5c1a8f3e9d2b4c6a8e0f1a2b3c4d5e6f-1a2b3c4d5e6f7a8b-01";
    private const string TraceD = "00-6d2b9f4e0e3c5d7b9f1a2b3c4d5e6f70-2b3c4d5e6f7a8b9c-00";
    private const string TraceE = "00-7e3c0a5f1f4d6e8c0a2b3c4d5e6f7081-3c4d5e6f7a8b9cad-01";
    private const string TraceF = "00-8f4d1b6a2a5e7f9d1b3c4d5e6f708192-4d5e6f7a8b9cadbe-01";

    [PostgreSqlContractFact]
    public async Task The_shared_fixture_carries_both_columns_their_checks_and_their_privileges()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        Assert.Equal(
            "location_outbox_events:text:YES:-,outbox_events:text:YES:-",
            await ScalarAsync<string>(connection, null,
                """
                SELECT string_agg(table_name || ':' || data_type || ':' || is_nullable || ':' || coalesce(column_default,'-'),
                                  ',' ORDER BY table_name)
                FROM information_schema.columns
                WHERE table_schema='platform' AND table_name IN ('outbox_events','location_outbox_events')
                  AND column_name='trace_parent'
                """));
        Assert.Equal(
            $"{AddOutboxTraceContext.LocationOutboxConstraint}={AddOutboxTraceContext.ConstraintDefinition}|" +
            $"{AddOutboxTraceContext.OutboxConstraint}={AddOutboxTraceContext.ConstraintDefinition}",
            await ScalarAsync<string>(connection, null,
                $"""
                SELECT string_agg(conname || '=' || pg_get_constraintdef(oid), '|' ORDER BY conname)
                FROM pg_constraint
                WHERE conname IN ('{AddOutboxTraceContext.OutboxConstraint}','{AddOutboxTraceContext.LocationOutboxConstraint}')
                  AND conrelid IN ('platform.outbox_events'::regclass,'platform.location_outbox_events'::regclass)
                """));

        // The producers write it (table-wide INSERT, or the operator executor's column grant); no runtime role reads or
        // changes it directly, and only the outbox executor's reviewed definers update rows.
        Assert.Equal(
            "app:t:f:f|worker:t:f:f|app-location:t:f:f|worker-location:t:f:f|operator:t:f:f|operator-location:f:f:f|realtime:f:f:f",
            await ScalarAsync<string>(connection, null,
                """
                SELECT string_agg(label || ':' ||
                         left(has_column_privilege(role::name, tbl, 'trace_parent', 'INSERT')::text, 1) || ':' ||
                         left(has_column_privilege(role::name, tbl, 'trace_parent', 'SELECT')::text, 1) || ':' ||
                         left(has_column_privilege(role::name, tbl, 'trace_parent', 'UPDATE')::text, 1), '|' ORDER BY ord)
                FROM (VALUES
                  (1,'app','paqueteria_app','platform.outbox_events'),
                  (2,'worker','paqueteria_worker','platform.outbox_events'),
                  (3,'app-location','paqueteria_app','platform.location_outbox_events'),
                  (4,'worker-location','paqueteria_worker','platform.location_outbox_events'),
                  (5,'operator','paqueteria_operator_outbox_executor','platform.outbox_events'),
                  (6,'operator-location','paqueteria_operator_outbox_executor','platform.location_outbox_events'),
                  (7,'realtime','paqueteria_realtime','platform.outbox_events')) v(ord,label,role,tbl)
                """));
        Assert.True(await ScalarAsync<bool>(connection, null,
            $"""
            SELECT EXISTS (SELECT 1 FROM platform.__ef_migrations_history_dispatch WHERE "MigrationId"='{AddOutboxTraceContext.MigrationId}')
               AND EXISTS (SELECT 1 FROM platform.__ef_migrations_history_notifications WHERE "MigrationId"='{CarryOutboxTraceContext.MigrationId}')
               AND (SELECT prosrc FROM pg_proc WHERE oid=to_regprocedure('{CarryOutboxTraceContext.EmitSignature}')) LIKE '%v_trace_parent%'
               AND (SELECT prosrc FROM pg_proc WHERE oid=to_regprocedure('{CarryOutboxTraceContext.ExpandSignature}')) LIKE '%v_source.trace_parent%'
               AND (SELECT prosrc FROM pg_proc WHERE oid=to_regprocedure('{AddOperatorOwnerOutboxExecutor.OutboxFunctionSignature}')) LIKE '%app.trace_parent%'
            """));

        // The installed catalog, not the migration sources: every function of any schema whose body inserts into either
        // outbox table (qualified or not, quoted or not) names trace_parent in each INSERT's column list.
        var inserts = new List<(string Function, string? Columns)>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT n.nspname || '.' || p.proname, m[1]
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid=p.pronamespace
            CROSS JOIN LATERAL regexp_matches(
              p.prosrc,
              'insert\s+into\s+(?:"?platform"?\s*\.\s*)?"?(?:location_)?outbox_events"?(?![a-z0-9_])\s*(\([^)]*\))?',
              'gi') AS m
            ORDER BY 1, 2
            """,
            connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                inserts.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        Assert.Equal(
            [
                "security.append_operator_order_outbox",
                "security.emit_notification_status_changed",
                "security.expand_order_created_notifications",
                // NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-003): the recipient notice expansion.
                "security.expand_recipient_notice_request",
            ],
            inserts.Select(insert => insert.Function).Distinct().Order(StringComparer.Ordinal));
        Assert.All(inserts, insert => Assert.Matches(@"\btrace_parent\b", insert.Columns ?? string.Empty));

        // The content guards compare the column like the content.
        Assert.Equal(2L, await ScalarAsync<long>(connection, null,
            """
            SELECT count(*) FROM pg_proc
            WHERE oid IN ('platform.protect_outbox_content()'::regprocedure,'platform.protect_location_outbox_content()'::regprocedure)
              AND position('NEW.trace_parent' IN prosrc) > 0 AND position('OLD.trace_parent' IN prosrc) > 0
            """));
    }

    [PostgreSqlContractFact]
    public async Task Both_tables_store_only_the_exact_trace_context_shape()
    {
        var org = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@id,'OBS-003 Trace Shape','OBS-003 Trace Shape','BUSINESS')",
            P("id", org));
        try
        {
            // The stored shape and NULL are accepted, from the API role and from the Worker role.
            foreach (var value in new string?[] { TraceA, TraceB, null })
            {
                await using var app = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", Guid.NewGuid(), [org]);
                await InsertBusinessAsync(app.Connection, app.Transaction, org, Guid.NewGuid(), "obs003.trace-shape", value);
                await using var worker = await TenantTransaction.BeginAsync(fixture.WorkerDataSource, "paqueteria_worker", Guid.NewGuid(), [org]);
                await InsertLocationAsync(worker.Connection, worker.Transaction, org, Guid.NewGuid(), value);
            }

            // Anything else is refused by the CHECK of each table: tracestate or baggage appended, another version,
            // uppercase, unknown flags, all-zero ids, truncated or padded values, or an empty string.
            foreach (var value in new[]
                     {
                         TraceA + ",vendor=synthetic",
                         "01" + TraceA[2..],
                         TraceA.ToUpperInvariant(),
                         TraceA[..^2] + "02",
                         "00-00000000000000000000000000000000-b7ad6b7169203331-01",
                         "00-0af7651916cd43dd8448eb211c80319c-0000000000000000-01",
                         TraceA[..^1],
                         TraceA + " ",
                         string.Empty,
                     })
            {
                await using (var app = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", Guid.NewGuid(), [org]))
                {
                    var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                        InsertBusinessAsync(app.Connection, app.Transaction, org, Guid.NewGuid(), "obs003.trace-shape", value));
                    Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
                    Assert.Equal(AddOutboxTraceContext.OutboxConstraint, refused.ConstraintName);
                }

                await using (var worker = await TenantTransaction.BeginAsync(fixture.WorkerDataSource, "paqueteria_worker", Guid.NewGuid(), [org]))
                {
                    var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                        InsertLocationAsync(worker.Connection, worker.Transaction, org, Guid.NewGuid(), value));
                    Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
                    Assert.Equal(AddOutboxTraceContext.LocationOutboxConstraint, refused.ConstraintName);
                }
            }
        }
        finally
        {
            await ExecuteAdminAsync(
                "DELETE FROM platform.outbox_events WHERE owner_org_id=@id; DELETE FROM platform.location_outbox_events WHERE owner_org_id=@id; DELETE FROM organizations.organizations WHERE id=@id",
                P("id", org));
        }
    }

    [PostgreSqlContractFact]
    public async Task Every_claim_function_returns_the_trace_context_and_the_lifecycle_never_changes_it()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();

        // Every set-returning claim or recovery function returns the whole row type of its outbox table, so it returns
        // trace_parent; a new one must be reviewed here.
        Assert.Equal(
            "claim_dispatch_outbox:platform.outbox_events|claim_location_outbox:platform.location_outbox_events|" +
            "claim_notifications_outbox:platform.outbox_events|claim_outbox:platform.outbox_events|" +
            "claim_realtime_outbox:platform.outbox_events|claim_unowned_outbox:platform.outbox_events|" +
            "recover_stale_notifications_outbox:platform.outbox_events",
            await ScalarAsync<string>(connection, null,
                """
                SELECT string_agg(p.proname || ':' || CASE WHEN p.proretset THEN p.prorettype::regtype::text ELSE 'not-a-set' END,
                                  '|' ORDER BY p.proname)
                FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='security'
                  AND (p.proname LIKE 'claim\_%outbox' OR p.proname LIKE 'recover\_%outbox')
                """));

        // One transaction, rolled back: the shared fixture keeps no row and other tests' rows are never settled.
        await using var transaction = await connection.BeginTransactionAsync();
        var org = Guid.NewGuid();
        var user = Guid.NewGuid();
        await ExecuteAsync(connection, transaction,
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@id,'OBS-003 Trace Claims','OBS-003 Trace Claims','BUSINESS')",
            P("id", org));

        var realtime = Guid.NewGuid();
        var notifications = Guid.NewGuid();
        var dispatch = Guid.NewGuid();
        var unrouted = Guid.NewGuid();
        var location = Guid.NewGuid();
        await AsAppAsync(connection, transaction, user, org);
        await InsertBusinessAsync(connection, transaction, org, realtime, "orders.status-changed", TraceA);
        await InsertBusinessAsync(connection, transaction, org, notifications, "orders.created", TraceB);
        await InsertBusinessAsync(connection, transaction, org, dispatch, "dispatch.order-status-reaction-requested", TraceC);
        await InsertBusinessAsync(connection, transaction, org, unrouted, "obs003.synthetic-unrouted", TraceD);
        await ExecuteAsync(connection, transaction, "RESET ROLE");
        await AsWorkerAsync(connection, transaction, user, org);
        await InsertLocationAsync(connection, transaction, org, location, TraceE);
        await ExecuteAsync(connection, transaction, "RESET ROLE");

        Assert.Equal(TraceA, (await ClaimAsync(connection, transaction, "claim_realtime_outbox", realtime)).TraceParent);
        Assert.Equal(TraceB, (await ClaimAsync(connection, transaction, "claim_notifications_outbox", notifications)).TraceParent);
        Assert.Equal(TraceC, (await ClaimAsync(connection, transaction, "claim_dispatch_outbox", dispatch)).TraceParent);
        Assert.Equal(TraceE, (await ClaimAsync(connection, transaction, "claim_location_outbox", location)).TraceParent);
        var claimed = await ClaimAsync(connection, transaction, "claim_unowned_outbox", unrouted);
        Assert.Equal(TraceD, claimed.TraceParent);

        // Settle to RETRY, claim again, let the lease expire and requeue: the context never changes.
        Assert.True(await ScalarAsync<bool>(connection, transaction,
            "SELECT security.settle_outbox(@id,@lease,'RETRY','OBS003_SYNTHETIC_RETRY',clock_timestamp()-interval '1 second')",
            P("id", unrouted), P("lease", claimed.Lease)));
        Assert.Equal(TraceD, await TraceOfAsync(connection, transaction, "platform.outbox_events", unrouted));
        var reclaimed = await ClaimAsync(connection, transaction, "claim_unowned_outbox", unrouted);
        Assert.Equal(TraceD, reclaimed.TraceParent);
        await ExecuteAsync(connection, transaction,
            "UPDATE platform.outbox_events SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id=@id",
            P("id", unrouted));
        Assert.True(await ScalarAsync<int>(connection, transaction,
            "SELECT security.requeue_stale_unowned_outbox(interval '0 seconds',1000,10)") >= 1);
        Assert.Equal("RETRY", await ScalarAsync<string>(connection, transaction,
            "SELECT status FROM platform.outbox_events WHERE id=@id", P("id", unrouted)));
        Assert.Equal(TraceD, await TraceOfAsync(connection, transaction, "platform.outbox_events", unrouted));

        // The content guards refuse any change of the stored context, on both tables, even to the superuser.
        foreach (var (table, id, message) in new[]
                 {
                     ("platform.outbox_events", unrouted, "outbox event content is immutable"),
                     ("platform.location_outbox_events", location, "location outbox event content is immutable"),
                 })
        {
            await ExecuteAsync(connection, transaction, "SAVEPOINT guard");
            var guarded = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, transaction,
                $"UPDATE {table} SET trace_parent=@trace WHERE id=@id", P("trace", TraceF), P("id", id)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, guarded.SqlState);
            Assert.Equal(message, guarded.MessageText);
            await ExecuteAsync(connection, transaction, "ROLLBACK TO SAVEPOINT guard");
        }

        // The generic claim (unused by the runtime) returns it too.
        var generic = Guid.NewGuid();
        await AsAppAsync(connection, transaction, user, org);
        await InsertBusinessAsync(connection, transaction, org, generic, "obs003.synthetic-generic", TraceF);
        await ExecuteAsync(connection, transaction, "RESET ROLE");
        Assert.Equal(TraceF, (await ClaimAsync(connection, transaction, "claim_outbox", generic)).TraceParent);

        // Retention: purge removes terminal rows that carry a trace context exactly as it removes the others.
        var processed = Guid.NewGuid();
        var processedLocation = Guid.NewGuid();
        await AsAppAsync(connection, transaction, user, org);
        await InsertBusinessAsync(connection, transaction, org, processed, "obs003.synthetic-processed", TraceA, terminal: true);
        await ExecuteAsync(connection, transaction, "RESET ROLE");
        await AsWorkerAsync(connection, transaction, user, org);
        await InsertLocationAsync(connection, transaction, org, processedLocation, TraceE, terminal: true);
        await ExecuteAsync(connection, transaction, "RESET ROLE");
        await PurgeAllAsync(connection, transaction, "security.purge_outbox");
        await PurgeAllAsync(connection, transaction, "security.purge_location_outbox");

        Assert.Equal(0L, await ScalarAsync<long>(connection, transaction,
            "SELECT (SELECT count(*) FROM platform.outbox_events WHERE id=@a) + (SELECT count(*) FROM platform.location_outbox_events WHERE id=@b)",
            P("a", processed), P("b", processedLocation)));
        await transaction.RollbackAsync();
    }

    [PostgreSqlContractFact]
    public async Task The_notification_writers_copy_the_originating_trace_context_and_never_a_payload()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var org = Guid.NewGuid();
        var order = Guid.NewGuid();
        var traced = Guid.NewGuid();
        var untraced = Guid.NewGuid();
        await ExecuteAsync(connection, transaction,
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@id,'OBS-003 Trace Notifications','OBS-003 Trace Notifications','BUSINESS')",
            P("id", org));
        await InsertBusinessAsync(connection, transaction, org, traced, "orders.created", TraceA, aggregate: order);
        await InsertBusinessAsync(connection, transaction, org, untraced, "orders.created", null, aggregate: order);

        // orders.created is expanded: the send request and the PENDING status change carry the source's context.
        var sources = await ClaimAllAsync(connection, transaction, "claim_notifications_outbox");
        Assert.Equal(TraceA, sources[traced].TraceParent);
        Assert.Null(sources[untraced].TraceParent);
        foreach (var source in new[] { traced, untraced })
        {
            var claim = sources[source];
            Assert.Equal("SOURCE_EXPANDED", await ScalarAsync<string>(connection, transaction,
                """
                SELECT security.expand_order_created_notifications(
                  @source,@lease,@order,'ORD_abcdefghijklmnopqrstuv','DRAFT',clock_timestamp(),@recipients)
                """,
                P("source", source),
                P("lease", claim.Lease),
                P("order", order),
                new NpgsqlParameter<Guid[]>("recipients", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = [Guid.NewGuid()] }));
        }

        Assert.Equal(
            $"notifications.send-requested={TraceA},notifications.status-changed={TraceA}",
            await DerivedTracesAsync(connection, transaction, traced));
        Assert.Equal(
            "notifications.send-requested=NULL,notifications.status-changed=NULL",
            await DerivedTracesAsync(connection, transaction, untraced));

        // The send request is claimed with it, and the outcome's status change (written by the emitter from the
        // Notifications dispatch cycle, with no activity) still carries the originating context.
        var notification = await ScalarAsync<Guid>(connection, transaction,
            "SELECT id FROM notifications.notifications WHERE source_event_id=@source", P("source", traced));
        var send = await ScalarAsync<Guid>(connection, transaction,
            "SELECT id FROM platform.outbox_events WHERE topic='notifications.send-requested' AND aggregate_id=@id",
            P("id", notification));
        var sendClaim = await ClaimAsync(connection, transaction, "claim_notifications_outbox", send);
        Assert.Equal(TraceA, sendClaim.TraceParent);
        Assert.True(await ScalarAsync<bool>(connection, transaction,
            "SELECT security.apply_notification_outcome(@outbox,@lease,@notification,1,'TRANSIENT','SYNTHETIC_TRANSIENT',NULL,clock_timestamp(),clock_timestamp())",
            P("outbox", send), P("lease", sendClaim.Lease), P("notification", notification)));
        Assert.Equal(
            $"notifications.send-requested={TraceA},notifications.status-changed={TraceA},notifications.status-changed={TraceA}",
            await DerivedTracesAsync(connection, transaction, traced));

        // The context is a column of the row only: no payload, notification or status history names it.
        var traceId = TraceA.Substring(3, 32);
        Assert.Equal(0L, await ScalarAsync<long>(connection, transaction,
            """
            SELECT (SELECT count(*) FROM platform.outbox_events
                    WHERE owner_org_id=@org AND (payload::text LIKE '%' || @trace || '%' OR tenant_context::text LIKE '%' || @trace || '%'))
                 + (SELECT count(*) FROM notifications.notifications n
                    WHERE n.owner_org_id=@org AND to_jsonb(n)::text LIKE '%' || @trace || '%')
                 + (SELECT count(*) FROM notifications.notification_status_events h
                    WHERE h.owner_org_id=@org AND to_jsonb(h)::text LIKE '%' || @trace || '%')
            """,
            P("org", org), P("trace", traceId)));
        Assert.Equal(0L, await ScalarAsync<long>(connection, transaction,
            "SELECT count(*) FROM platform.outbox_events WHERE owner_org_id=@org AND payload ? 'trace_parent'",
            P("org", org)));
        await transaction.RollbackAsync();
    }

    [PostgreSqlContractFact]
    public async Task Both_lane_steps_go_up_down_and_up_again_with_an_identical_catalog()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("obs003tracectx");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            Assert.Equal(
                DatabaseBaselineApplyStatus.Applied,
                (await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString)).Status);
            var coordinator = new ModuleMigrationCoordinator();
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            var installed = await CatalogAsync(connection);
            // Each column line ends with its default expression, '-' for none.
            Assert.Contains("column:location_outbox_events.trace_parent:text:nullable:-", installed);
            Assert.Contains("column:outbox_events.trace_parent:text:nullable:-", installed);
            Assert.Contains("column-acl:outbox_events.trace_parent:paqueteria_operator_outbox_executor:INSERT:false", installed);
            // The definer, the two Notifications writers and the two content guards name the column, and so does the
            // NTF-RECIPIENT-NOTICES-2026-10-20 expansion of the recipient notice lane step that follows the OBS-003 one.
            Assert.Equal(6, installed.Count(line => line.StartsWith("function:", StringComparison.Ordinal) && line.EndsWith(":trace", StringComparison.Ordinal)));

            await using var dispatch = CreateDispatchContext(connection);
            await using var notifications = CreateNotificationsContext(connection);
            var dispatchPrevious = Previous(dispatch, AddOutboxTraceContext.MigrationId);
            var notificationsPrevious = Previous(notifications, CarryOutboxTraceContext.MigrationId);

            // Rolling the Dispatch step back first is refused: the Notifications writers still write the column.
            await ExecuteAsync(connection, null, "SET ROLE paqueteria_migrator");
            var refused = await Assert.ThrowsAnyAsync<Exception>(() =>
                dispatch.GetService<IMigrator>().MigrateAsync(dispatchPrevious));
            await ExecuteAsync(connection, null, "RESET ROLE");
            Assert.Equal(AddOutboxTraceContext.DowngradeOrderRefused, FindPostgresException(refused).MessageText);
            Assert.Equal(installed, await CatalogAsync(connection));

            // Down in order: the Notifications step, then the Dispatch step. The previous functions, grants and tables
            // come back exactly, and the AI-06/AI-18 boundary holds without the column.
            await MigrateAsMigratorAsync(connection, notifications, notificationsPrevious);
            Assert.False(await NotificationsHistoryContainsAsync(connection, CarryOutboxTraceContext.MigrationId));
            await MigrateAsMigratorAsync(connection, dispatch, dispatchPrevious);
            var before = await CatalogAsync(connection);
            Assert.DoesNotContain(before, line => line.Contains("trace_parent", StringComparison.Ordinal) && !line.StartsWith("history:", StringComparison.Ordinal));
            Assert.DoesNotContain(before, line => line.StartsWith("function:", StringComparison.Ordinal) && line.EndsWith(":trace", StringComparison.Ordinal));
            Assert.False(await E002OperatorOutboxStateReader.IsOutboxTraceContextAppliedAsync(connection));
            await new DatabaseBaselineAssertions().AssertAsync(connection);

            // Up through EF, lane by lane in the migrator's order: the installed catalog again.
            await MigrateAsMigratorAsync(connection, dispatch, null);
            await MigrateAsMigratorAsync(connection, notifications, null);
            Assert.Equal(installed, await CatalogAsync(connection));

            // Down again: the same pre-OBS-003 catalog. Up again through the canonical migrator: the same installed one.
            await MigrateAsMigratorAsync(connection, notifications, notificationsPrevious);
            await MigrateAsMigratorAsync(connection, dispatch, dispatchPrevious);
            Assert.Equal(before, await CatalogAsync(connection));
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.All(
                await coordinator.AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            Assert.Equal(installed, await CatalogAsync(connection));
            await new DatabaseBaselineAssertions().AssertAsync(connection);

            // The Up SQL adopts what it finds and is idempotent; a column of another shape is refused, never altered.
            // NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (a later Notifications step) adds the receipt executor as a grantee
            // of the status writer; the OBS-003 step is replayed on its own catalog, so that later grant is withdrawn
            // inside the rolled-back transaction first.
            await using (var again = await connection.BeginTransactionAsync())
            {
                await ExecuteAsync(connection, again, AddOutboxTraceContext.UpSql);
                await ExecuteAsync(connection, again, "RESET ROLE");
                await ExecuteAsync(connection, again,
                    """
                    SET ROLE paqueteria_outbox_executor;
                    REVOKE EXECUTE ON FUNCTION security.emit_notification_status_changed(uuid,uuid,integer,text,text,integer,timestamptz) FROM paqueteria_notification_receipt_executor;
                    RESET ROLE;
                    """);
                await ExecuteAsync(connection, again, CarryOutboxTraceContext.UpSql);
                await ExecuteAsync(connection, again, "RESET ROLE");
                await again.RollbackAsync();
            }

            await using (var widened = await connection.BeginTransactionAsync())
            {
                await ExecuteAsync(connection, widened,
                    $"ALTER TABLE platform.location_outbox_events DROP CONSTRAINT {AddOutboxTraceContext.LocationOutboxConstraint}");
                await ExecuteAsync(connection, widened,
                    $"ALTER TABLE platform.location_outbox_events ADD CONSTRAINT {AddOutboxTraceContext.LocationOutboxConstraint} CHECK (trace_parent IS NOT NULL OR trace_parent IS NULL)");
                var shape = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, widened, AddOutboxTraceContext.UpSql));
                Assert.Contains("does not match the OBS-003-OUTBOX-TRACE-CONTEXT contract", shape.MessageText, StringComparison.Ordinal);
                await widened.RollbackAsync();
            }

            Assert.Equal(installed, await CatalogAsync(connection));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    /// <summary>
    /// The catalog both lane steps touch: both tables' columns, checks, triggers, indexes and grants, every function of
    /// schemas security and platform, the content guards included (owner, SECURITY DEFINER, configuration, ACL, body and
    /// whether it names the trace context), and the ACL of schema security.
    /// </summary>
    private static async Task<string[]> CatalogAsync(NpgsqlConnection connection)
    {
        const string sql =
            """
            WITH tables AS (
              SELECT oid, relname FROM pg_class
              WHERE oid IN ('platform.outbox_events'::regclass,'platform.location_outbox_events'::regclass))
            SELECT 'column:' || t.relname || '.' || a.attname || ':' || format_type(a.atttypid,a.atttypmod) || ':' ||
                   CASE WHEN a.attnotnull THEN 'not-null' ELSE 'nullable' END || ':' || coalesce(pg_get_expr(d.adbin,d.adrelid),'-')
            FROM tables t JOIN pg_attribute a ON a.attrelid=t.oid
            LEFT JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
            WHERE a.attnum>0 AND NOT a.attisdropped
            UNION ALL
            SELECT 'constraint:' || t.relname || '.' || c.conname || ':' || pg_get_constraintdef(c.oid) || ':' || c.convalidated::text
            FROM tables t JOIN pg_constraint c ON c.conrelid=t.oid
            UNION ALL
            SELECT 'trigger:' || t.relname || ':' || pg_get_triggerdef(g.oid)
            FROM tables t JOIN pg_trigger g ON g.tgrelid=t.oid WHERE NOT g.tgisinternal
            UNION ALL
            SELECT 'index:' || pg_get_indexdef(i.indexrelid)
            FROM tables t JOIN pg_index i ON i.indrelid=t.oid
            UNION ALL
            SELECT 'table-acl:' || t.relname || ':' || pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type || ':' || acl.is_grantable::text
            FROM tables t JOIN pg_class c ON c.oid=t.oid CROSS JOIN LATERAL aclexplode(c.relacl) acl
            UNION ALL
            SELECT 'column-acl:' || t.relname || '.' || a.attname || ':' || pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type || ':' || acl.is_grantable::text
            FROM tables t JOIN pg_attribute a ON a.attrelid=t.oid CROSS JOIN LATERAL aclexplode(a.attacl) acl
            WHERE a.attacl IS NOT NULL AND NOT a.attisdropped
            UNION ALL
            SELECT 'function:' || p.oid::regprocedure::text || ':' || pg_get_userbyid(p.proowner) || ':' || p.prosecdef::text || ':' ||
                   coalesce(array_to_string(p.proconfig, ';'),'-') || ':' ||
                   coalesce((SELECT string_agg(CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(acl.grantee) END
                                               || '=' || acl.privilege_type || '/' || acl.is_grantable::text, ','
                                               ORDER BY (CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(acl.grantee) END) COLLATE "C",
                                                        acl.privilege_type)
                             FROM aclexplode(p.proacl) acl), '-') || ':' ||
                   md5(p.prosrc) || ':' || CASE WHEN p.prosrc LIKE '%trace_parent%' THEN 'trace' ELSE 'plain' END
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname IN ('security','platform')
            UNION ALL
            SELECT 'schema-acl:security:' || CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(acl.grantee) END || ':' ||
                   acl.privilege_type || ':' || acl.is_grantable::text
            FROM pg_namespace n CROSS JOIN LATERAL aclexplode(n.nspacl) acl WHERE n.nspname='security'
            """;
        var lines = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        return [.. lines.Order(StringComparer.Ordinal)];
    }

    private static async Task<bool> NotificationsHistoryContainsAsync(NpgsqlConnection connection, string migrationId) =>
        await ScalarAsync<bool>(connection, null,
            "SELECT EXISTS (SELECT 1 FROM platform.__ef_migrations_history_notifications WHERE \"MigrationId\"=@id)",
            P("id", migrationId));

    private static string Previous(DbContext context, string migrationId)
    {
        var ids = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order(StringComparer.Ordinal).ToArray();
        var index = Array.IndexOf(ids, migrationId);
        Assert.True(index > 0, $"{migrationId} must follow an earlier step of its lane");
        // Only the DP-INDEXES-ORDERS-DISPATCH-2026-10-20 read-index step (Dispatch), the NTF-RECIPIENT-NOTICES-2026-10-20
        // recipient notice lane step, the NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 count step and the
        // NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 receipt step (Notifications) may follow the OBS-003 steps: rolling back
        // to the previous step reverts them first; the read-index, count and receipt steps touch no outbox writer (the
        // receipt executor only executes the existing status writer), and the recipient notice step keeps the trace
        // context of every row it writes and restores the OBS-003 emitter on its way down.
        Assert.All(ids[(index + 1)..], later => Assert.Contains(later, new[]
        {
            AddAssignmentReadIndexes.MigrationId, AddRecipientNoticeLane.MigrationId, AddRecipientNoticeCounts.MigrationId,
            AddRecipientNoticeReceipts.MigrationId,
        }));
        return ids[index - 1];
    }

    private static async Task MigrateAsMigratorAsync(NpgsqlConnection connection, DbContext context, string? target)
    {
        await ExecuteAsync(connection, null, "SET ROLE paqueteria_migrator");
        await context.GetService<IMigrator>().MigrateAsync(target);
        await ExecuteAsync(connection, null, "RESET ROLE");
    }

    private static DispatchDbContext CreateDispatchContext(NpgsqlConnection connection) =>
        new(new DbContextOptionsBuilder<DispatchDbContext>()
                .UseNpgsql(connection, postgres =>
                {
                    postgres.MigrationsAssembly(typeof(DispatchDbContext).Assembly.FullName);
                    postgres.MigrationsHistoryTable("__ef_migrations_history_dispatch", "platform");
                })
                .Options,
            new TenantDatabaseExecutionState());

    private static NotificationsDbContext CreateNotificationsContext(NpgsqlConnection connection) =>
        new(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(NotificationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_notifications", "platform");
            })
            .Options);

    private static PostgresException FindPostgresException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected a PostgresException, got {exception}");
    }

    private static async Task<string> DerivedTracesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid source) =>
        await ScalarAsync<string>(connection, transaction,
            """
            SELECT string_agg(o.topic || '=' || coalesce(o.trace_parent,'NULL'), ',' ORDER BY o.topic, o.aggregate_version)
            FROM platform.outbox_events o
            WHERE o.aggregate_type='Notification'
              AND o.aggregate_id IN (SELECT id FROM notifications.notifications WHERE source_event_id=@source)
            """,
            P("source", source));

    private static async Task<(Guid Lease, string? TraceParent)> ClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string function,
        Guid expectedId)
    {
        var claims = await ClaimAllAsync(connection, transaction, function);
        Assert.True(claims.ContainsKey(expectedId), $"{function} must claim {expectedId}");
        return claims[expectedId];
    }

    /// <summary>Every row one call of a claim function returns, by id, with its lease and the trace_parent it returns.</summary>
    private static async Task<Dictionary<Guid, (Guid Lease, string? TraceParent)>> ClaimAllAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string function)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT id,lease_token,trace_parent FROM security.{function}('obs003-trace-context',500,interval '1 minute')",
            connection,
            transaction);
        var claims = new Dictionary<Guid, (Guid Lease, string? TraceParent)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            claims[reader.GetGuid(0)] = (reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }

        return claims;
    }

    /// <summary>Runs a purge function until it removes nothing more (bounded), as the retention job does.</summary>
    private static async Task PurgeAllAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string function)
    {
        for (var round = 0; round < 100; round++)
        {
            var removed = await ScalarAsync<int>(connection, transaction,
                $"SELECT {function}(clock_timestamp()-interval '2 days',clock_timestamp()-interval '8 days',1000,false)");
            if (removed == 0)
            {
                return;
            }
        }
    }

    private static async Task<string?> TraceOfAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, Guid id)
    {
        await using var command = new NpgsqlCommand($"SELECT trace_parent FROM {table} WHERE id=@id", connection, transaction);
        command.Parameters.Add(P("id", id));
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : (string)value;
    }

    private static async Task AsAppAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid user, Guid org)
    {
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_app");
        await SetTenantAsync(connection, transaction, user, org);
    }

    private static async Task AsWorkerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid user, Guid org)
    {
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_worker");
        await SetTenantAsync(connection, transaction, user, org);
    }

    private static Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid user, Guid org) =>
        ExecuteAsync(connection, transaction,
            "SELECT set_config('app.current_user_id', @user::text, true), set_config('app.current_org_ids', ARRAY[@org]::uuid[]::text, true)",
            P("user", user), P("org", org));

    private static async Task InsertBusinessAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid org,
        Guid id,
        string topic,
        string? traceParent,
        bool terminal = false,
        Guid? aggregate = null)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,priority,status,
              attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,last_error,created_at,processed_at,trace_parent)
            VALUES (
              @id,@org,jsonb_build_object('organization_ids',jsonb_build_array(@org::text)),@topic,'Order',@aggregate,1,'{}',100,
              CASE WHEN @terminal THEN 'PROCESSED' ELSE 'PENDING' END,0,
              clock_timestamp()-interval '30 days',NULL,NULL,NULL,NULL,NULL,
              clock_timestamp()-interval '30 days',
              CASE WHEN @terminal THEN clock_timestamp()-interval '10 days' END,
              @trace_parent)
            """,
            connection,
            transaction);
        command.Parameters.Add(P("id", id));
        command.Parameters.Add(P("org", org));
        command.Parameters.Add(P("topic", topic));
        command.Parameters.Add(P("aggregate", aggregate ?? Guid.NewGuid()));
        command.Parameters.Add(new NpgsqlParameter<bool>("terminal", NpgsqlDbType.Boolean) { TypedValue = terminal });
        command.Parameters.Add(new NpgsqlParameter<string?>("trace_parent", NpgsqlDbType.Text) { TypedValue = traceParent });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertLocationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid org,
        Guid id,
        string? traceParent,
        bool terminal = false)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.location_outbox_events(
              id,owner_org_id,driver_position_id,topic,payload,status,attempts,available_at,created_at,processed_at,trace_parent)
            VALUES (
              @id,@org,@position,'obs003.synthetic-location','{}',CASE WHEN @terminal THEN 'PROCESSED' ELSE 'PENDING' END,0,
              clock_timestamp()-interval '30 days',clock_timestamp()-interval '30 days',
              CASE WHEN @terminal THEN clock_timestamp()-interval '10 days' END,
              @trace_parent)
            """,
            connection,
            transaction);
        command.Parameters.Add(P("id", id));
        command.Parameters.Add(P("org", org));
        command.Parameters.Add(P("position", Guid.NewGuid()));
        command.Parameters.Add(new NpgsqlParameter<bool>("terminal", NpgsqlDbType.Boolean) { TypedValue = terminal });
        command.Parameters.Add(new NpgsqlParameter<string?>("trace_parent", NpgsqlDbType.Text) { TypedValue = traceParent });
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        var result = await command.ExecuteScalarAsync();
        return result is T typed ? typed : (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static NpgsqlParameter P(string name, object value) => new(name, value);
}
