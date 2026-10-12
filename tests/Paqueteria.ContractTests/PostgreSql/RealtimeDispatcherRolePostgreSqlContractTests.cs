using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Notifications.Infrastructure.Persistence;
using Notifications.Infrastructure.Persistence.Migrations;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;
using Testcontainers.PostgreSql;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12 against real PostgreSQL. The API's in-process realtime dispatchers run as
/// <c>paqueteria_realtime</c> (NOLOGIN NOBYPASSRLS): they claim, settle and requeue the REALTIME business lane and the
/// location lane only through the lease-token functions, read the evidence columns under FORCE RLS, and can do nothing
/// else. Every scenario runs inside a transaction that is rolled back, so no row outlives it.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class RealtimeDispatcherRolePostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Role = AddRealtimeDispatcherRole.Role;
    private const string Worker = "rtm-api-role-contract";

    [PostgreSqlContractFact]
    public async Task The_lane_installs_the_exact_contract_and_every_assertion_accepts_it()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await new DatabaseBaselineAssertions().AssertAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        await DatabaseBaselineAssertions.AssertRealtimeDispatcherRoleInstalledAsync(connection, transaction);
        Assert.True(await E002NotificationStateReader.IsRealtimeDispatcherRoleAppliedAsync(connection, transaction));
        Assert.Equal(
            "f|f|f|f|f|t",
            await ScalarAsync<string>(connection, transaction,
                """
                SELECT concat_ws('|',
                  r.rolcanlogin, r.rolbypassrls, r.rolsuper,
                  pg_has_role('paqueteria_app','paqueteria_realtime','MEMBER'),
                  pg_has_role('paqueteria_worker','paqueteria_realtime','MEMBER'),
                  pg_has_role(@login,'paqueteria_realtime','SET'))
                FROM pg_roles r WHERE r.rolname='paqueteria_realtime'
                """,
                ("login", PostgreSqlContractFixture.RealtimeLogin)));
        await transaction.RollbackAsync();
    }

    [PostgreSqlContractFact]
    public async Task Realtime_role_claims_settles_and_requeues_only_the_realtime_and_location_lanes()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var organization = await OrganizationAsync(connection, transaction);
        var realtime = await OutboxAsync(connection, transaction, organization, "orders.status-changed");
        var notifications = await OutboxAsync(connection, transaction, organization, "orders.created");
        var dispatch = await OutboxAsync(connection, transaction, organization, "dispatch.order-status-reaction-requested");
        var unrouted = await OutboxAsync(connection, transaction, organization, "rtm-contract.unknown-topic");
        var staleRealtime = await OutboxAsync(connection, transaction, organization, "orders.timeline-event-added", stale: true);
        var staleNotifications = await OutboxAsync(connection, transaction, organization, "notifications.send-requested", stale: true);
        var location = await LocationOutboxAsync(connection, transaction, organization);
        var staleLocation = await LocationOutboxAsync(connection, transaction, organization, stale: true);

        await ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {Role}");
        var claimed = await ClaimAsync(connection, transaction, "security.claim_realtime_outbox");
        Assert.Contains(realtime, claimed.Keys);
        Assert.DoesNotContain(notifications, claimed.Keys);
        Assert.DoesNotContain(dispatch, claimed.Keys);
        Assert.DoesNotContain(unrouted, claimed.Keys);

        // Settle needs the unexpired lease token of the claim: a wrong token and a second settle both fail.
        Assert.False(await SettleAsync(connection, transaction, "security.settle_outbox", realtime, Guid.NewGuid()));
        Assert.True(await SettleAsync(connection, transaction, "security.settle_outbox", realtime, claimed[realtime]));
        Assert.False(await SettleAsync(connection, transaction, "security.settle_outbox", realtime, claimed[realtime]));
        // A row of another lane cannot be settled without the lease token its own claim issued.
        Assert.False(await SettleAsync(connection, transaction, "security.settle_outbox", staleNotifications, Guid.NewGuid()));

        var claimedLocations = await ClaimAsync(connection, transaction, "security.claim_location_outbox");
        Assert.Contains(location, claimedLocations.Keys);
        Assert.True(await SettleAsync(connection, transaction, "security.settle_location_outbox", location, claimedLocations[location]));
        Assert.False(await SettleAsync(connection, transaction, "security.settle_location_outbox", location, claimedLocations[location]));

        Assert.True(await ScalarAsync<int>(connection, transaction,
            "SELECT security.requeue_stale_realtime_outbox(interval '0 seconds',1000,10)") >= 1);
        Assert.True(await ScalarAsync<int>(connection, transaction,
            "SELECT security.requeue_stale_location_outbox(interval '0 seconds',5000,20)") >= 1);

        await ExecuteAsync(connection, transaction, "RESET ROLE");
        Assert.Equal("PROCESSED", await StatusAsync(connection, transaction, "platform.outbox_events", realtime));
        Assert.Equal("PROCESSED", await StatusAsync(connection, transaction, "platform.location_outbox_events", location));
        Assert.Equal("RETRY", await StatusAsync(connection, transaction, "platform.outbox_events", staleRealtime));
        Assert.Equal("RETRY", await StatusAsync(connection, transaction, "platform.location_outbox_events", staleLocation));
        // The realtime requeue never touches a stale row of another lane, and no other lane's row was claimed.
        Assert.Equal("PROCESSING", await StatusAsync(connection, transaction, "platform.outbox_events", staleNotifications));
        foreach (var other in new[] { notifications, dispatch, unrouted })
        {
            Assert.Equal("PENDING", await StatusAsync(connection, transaction, "platform.outbox_events", other));
        }

        await transaction.RollbackAsync();
    }

    [PostgreSqlContractFact]
    public async Task Realtime_role_cannot_reach_other_lanes_maintenance_purge_executors_or_the_outbox_tables()
    {
        foreach (var statement in new[]
                 {
                     // Direct outbox access, read or write (invariant 3: only the lease-token functions).
                     "SELECT id FROM platform.outbox_events LIMIT 1",
                     "SELECT id FROM platform.location_outbox_events LIMIT 1",
                     "UPDATE platform.outbox_events SET status='DEAD' WHERE false",
                     "DELETE FROM platform.location_outbox_events WHERE false",
                     "INSERT INTO platform.outbox_events(id) SELECT gen_random_uuid() WHERE false",
                     // Other lanes and the legacy single-lane functions.
                     "SELECT * FROM security.claim_notifications_outbox('x',1,interval '1 minute')",
                     "SELECT * FROM security.claim_unowned_outbox('x',1,interval '1 minute')",
                     "SELECT * FROM security.claim_dispatch_outbox('x',1,interval '1 minute')",
                     "SELECT security.requeue_stale_dispatch_outbox(interval '0 seconds',1,1)",
                     "SELECT security.requeue_stale_unowned_outbox(interval '0 seconds',1,1)",
                     "SELECT * FROM security.recover_stale_notifications_outbox('x',1,1,interval '1 minute')",
                     "SELECT * FROM security.claim_outbox('x',1,interval '1 minute')",
                     "SELECT security.requeue_stale_outbox(interval '0 seconds',1,1)",
                     "SELECT security.resolve_outbox_consumer('orders.status-changed')",
                     "SELECT * FROM security.read_owner_dispatcher_ids(gen_random_uuid(),1)",
                     // Maintenance, purge and the other executors.
                     "SELECT security.purge_outbox(now(),now(),1,true)",
                     "SELECT security.purge_location_outbox(now(),now(),1,true)",
                     "SELECT security.finalize_expired_orders(1)",
                     "SELECT * FROM security.list_auto_close_owner_organizations(NULL,1)",
                     "SELECT security.purge_expired_idempotency_keys(now(),1,true)",
                     "SELECT security.expire_proof_upload_sessions(1)",
                     "SELECT security.purge_bff_sessions(1)",
                     "SELECT * FROM security.resolve_identity_context('x')",
                     "SELECT * FROM security.get_public_tracking_projection('x')",
                     "SELECT security.map_public_order_status('DELIVERED')",
                     // Columns, tables and schemas outside the evidence reads, and any write.
                     "SELECT email_ciphertext FROM identity.users LIMIT 1",
                     "SELECT total_cents FROM orders.orders LIMIT 1",
                     "SELECT * FROM orders.orders LIMIT 1",
                     "SELECT id FROM orders.public_tracking_tokens LIMIT 1",
                     "SELECT id FROM pricing.quotes LIMIT 1",
                     "SELECT id FROM notifications.notifications LIMIT 1",
                     "SELECT id FROM custody.proofs LIMIT 1",
                     "SELECT id FROM finance.cod_transactions LIMIT 1",
                     "SELECT id FROM platform.audit_logs LIMIT 1",
                     "SELECT heading_degrees FROM drivers.driver_positions LIMIT 1",
                     "UPDATE dispatch.assignments SET status='ACTIVE' WHERE false",
                     "DELETE FROM drivers.driver_positions WHERE false",
                     "INSERT INTO orders.order_events(id) SELECT gen_random_uuid() WHERE false",
                     "CREATE TABLE orders.rtm_role_probe(id integer)",
                 })
        {
            await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {Role}");
            var denied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, transaction, statement));
            Assert.True(denied.SqlState == PostgresErrorCodes.InsufficientPrivilege, $"{statement}: {denied.SqlState} {denied.MessageText}");
            await transaction.RollbackAsync();
        }
    }

    [PostgreSqlContractFact]
    public async Task Realtime_login_cannot_assume_any_other_role()
    {
        await using var connection = new NpgsqlConnection(fixture.RealtimeConnectionString);
        await connection.OpenAsync();
        foreach (var role in new[]
                 {
                     "paqueteria_worker", "paqueteria_app", "paqueteria_migrator", "paqueteria_bootstrap",
                     "paqueteria_outbox_executor", "paqueteria_maintenance", "paqueteria_auto_close_executor",
                 })
        {
            await using var transaction = await connection.BeginTransactionAsync();
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {role}"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            await transaction.RollbackAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {Role}");
            Assert.Equal(Role, await ScalarAsync<string>(connection, transaction, "SELECT current_user::text"));
            await transaction.RollbackAsync();
        }
    }

    [PostgreSqlContractFact]
    public async Task Realtime_role_cannot_bypass_rls_and_reads_only_the_organization_in_context()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var first = await OrganizationAsync(connection, transaction);
        var second = await OrganizationAsync(connection, transaction);
        var firstUser = await MemberAsync(connection, transaction, first);
        var secondUser = await MemberAsync(connection, transaction, second);
        var organizations = new[] { first, second };

        await ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {Role}");
        await SetOrganizationsAsync(connection, transaction, []);
        Assert.Equal(0L, await MembershipCountAsync(connection, transaction, organizations));
        Assert.Equal(0L, await ScalarAsync<long>(connection, transaction,
            "SELECT count(*) FROM identity.users WHERE id=ANY(@users)", ("users", new[] { firstUser, secondUser })));

        await SetOrganizationsAsync(connection, transaction, [first]);
        Assert.Equal(1L, await MembershipCountAsync(connection, transaction, organizations));
        Assert.Equal(
            firstUser,
            await ScalarAsync<Guid>(connection, transaction,
                "SELECT id FROM identity.users WHERE id=ANY(@users)", ("users", new[] { firstUser, secondUser })));

        // Turning row security off is refused for a role that cannot bypass it.
        await ExecuteAsync(connection, transaction, "SAVEPOINT rls_off");
        await ExecuteAsync(connection, transaction, "SET LOCAL row_security = off");
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            MembershipCountAsync(connection, transaction, organizations));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        await transaction.RollbackAsync();
    }

    [PostgreSqlContractFact]
    public async Task Baseline_assertions_fail_closed_on_any_widened_realtime_privilege()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await new DatabaseBaselineAssertions().AssertAsync(connection);
        foreach (var widening in new[]
                 {
                     $"GRANT SELECT ON orders.orders TO {Role};",
                     $"GRANT SELECT (total_cents) ON orders.orders TO {Role};",
                     $"GRANT UPDATE (status) ON dispatch.assignments TO {Role};",
                     $"GRANT INSERT ON platform.outbox_events TO {Role};",
                     $"GRANT SELECT (id) ON platform.outbox_events TO {Role};",
                     $"GRANT USAGE ON SCHEMA platform TO {Role};",
                     $"GRANT USAGE ON SCHEMA notifications TO {Role};",
                     $"GRANT CREATE ON SCHEMA orders TO {Role};",
                     $"GRANT EXECUTE ON FUNCTION security.claim_notifications_outbox(text,integer,interval) TO {Role};",
                     $"GRANT EXECUTE ON FUNCTION security.purge_outbox(timestamptz,timestamptz,integer,boolean) TO {Role};",
                     $"REVOKE EXECUTE ON FUNCTION security.claim_realtime_outbox(text,integer,interval) FROM {Role}; " +
                     $"GRANT EXECUTE ON FUNCTION security.claim_realtime_outbox(text,integer,interval) TO {Role} WITH GRANT OPTION;",
                     $"REVOKE SELECT (public_id) ON orders.orders FROM {Role};",
                     "REVOKE SELECT ON orders.orders FROM paqueteria_app;",
                     $"GRANT paqueteria_worker TO {Role};",
                     $"GRANT {Role} TO paqueteria_app;",
                     $"ALTER ROLE {Role} BYPASSRLS;",
                     $"ALTER ROLE {Role} LOGIN;",
                     $"ALTER DEFAULT PRIVILEGES FOR ROLE paqueteria_migrator IN SCHEMA orders GRANT SELECT ON TABLES TO {Role};",
                     $"""
                      CREATE FUNCTION security.rtm_role_rogue() RETURNS integer LANGUAGE sql AS 'SELECT 1';
                      ALTER FUNCTION security.rtm_role_rogue() OWNER TO {Role};
                      """,
                 })
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, widening);
            await Assert.ThrowsAsync<DatabaseAssertionException>(() =>
                new DatabaseBaselineAssertions().AssertAsync(connection, transaction));
            await transaction.RollbackAsync();
        }

        await new DatabaseBaselineAssertions().AssertAsync(connection);
    }

    [PostgreSqlContractFact]
    public async Task Migration_reapplies_rolls_down_and_up_and_refuses_contract_violations()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using (var reapply = await connection.BeginTransactionAsync())
        {
            // A group-role membership granted by mistake is removed; nothing else changes.
            await ExecuteAsync(connection, reapply, $"GRANT {Role} TO paqueteria_app;");
            await ExecuteAsync(connection, reapply, AddRealtimeDispatcherRole.UpSql);
            await ExecuteAsync(connection, reapply, "RESET ROLE;");
            await DatabaseBaselineAssertions.AssertRealtimeDispatcherRoleInstalledAsync(connection, reapply);
            Assert.False(await ScalarAsync<bool>(connection, reapply,
                "SELECT pg_has_role('paqueteria_app','paqueteria_realtime','MEMBER')"));
            await reapply.RollbackAsync();
        }

        await using (var roundTrip = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, roundTrip, AddRealtimeDispatcherRole.DownSql);
            await ExecuteAsync(connection, roundTrip, "RESET ROLE;");
            await ExecuteAsync(connection, roundTrip,
                "DELETE FROM platform.__ef_migrations_history_notifications WHERE \"MigrationId\"=@id",
                ("id", AddRealtimeDispatcherRole.MigrationId));
            Assert.Equal(
                "t|0|0|0|0",
                await ScalarAsync<string>(connection, roundTrip,
                    """
                    SELECT concat_ws('|',
                      to_regrole('paqueteria_realtime') IS NOT NULL,
                      (SELECT count(*) FROM information_schema.column_privileges WHERE grantee='paqueteria_realtime'),
                      (SELECT count(*) FROM information_schema.routine_privileges WHERE grantee='paqueteria_realtime'),
                      (SELECT count(*) FROM pg_namespace n CROSS JOIN LATERAL aclexplode(n.nspacl) acl
                        WHERE n.nspacl IS NOT NULL AND acl.grantee='paqueteria_realtime'::regrole),
                      (SELECT count(*) FROM information_schema.table_privileges WHERE grantee='paqueteria_realtime'))
                    """));
            // Rolled back, the inert role is what a fresh AI-18 declares; the lane check refuses it.
            await new DatabaseBaselineAssertions().AssertAsync(connection, roundTrip);
            await Assert.ThrowsAsync<DatabaseAssertionException>(() =>
                DatabaseBaselineAssertions.AssertRealtimeDispatcherRoleInstalledAsync(connection, roundTrip));
            // The inert role can claim nothing.
            await ExecuteAsync(connection, roundTrip, "SAVEPOINT inert");
            await ExecuteAsync(connection, roundTrip, $"SET LOCAL ROLE {Role}");
            var inert = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, roundTrip,
                "SELECT * FROM security.claim_realtime_outbox('x',1,interval '1 minute')"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, inert.SqlState);
            await ExecuteAsync(connection, roundTrip, "ROLLBACK TO SAVEPOINT inert");
            await ExecuteAsync(connection, roundTrip, "RESET ROLE;");

            await ExecuteAsync(connection, roundTrip, AddRealtimeDispatcherRole.UpSql);
            await ExecuteAsync(connection, roundTrip, "RESET ROLE;");
            await ExecuteAsync(connection, roundTrip,
                "INSERT INTO platform.__ef_migrations_history_notifications(\"MigrationId\",\"ProductVersion\") VALUES (@id,'10.0.0')",
                ("id", AddRealtimeDispatcherRole.MigrationId));
            await DatabaseBaselineAssertions.AssertRealtimeDispatcherRoleInstalledAsync(connection, roundTrip);
            await new DatabaseBaselineAssertions().AssertAsync(connection, roundTrip);
            await roundTrip.RollbackAsync();
        }

        foreach (var (violation, message) in new[]
                 {
                     ("ALTER TABLE orders.orders NO FORCE ROW LEVEL SECURITY;", "requires FORCE ROW LEVEL SECURITY on orders.orders"),
                     ($"ALTER ROLE {Role} LOGIN;", "exists with attributes outside the RTM-API-REALTIME-DISPATCHER-ROLE contract"),
                     ($"ALTER ROLE {Role} BYPASSRLS;", "exists with attributes outside the RTM-API-REALTIME-DISPATCHER-ROLE contract"),
                     ($"GRANT paqueteria_worker TO {Role};", "must not inherit any other role"),
                     ($"GRANT {Role} TO paqueteria_maintenance;", "is granted to a group role outside"),
                     ($"""
                       CREATE FUNCTION security.rtm_role_rogue() RETURNS integer LANGUAGE sql AS 'SELECT 1';
                       ALTER FUNCTION security.rtm_role_rogue() OWNER TO {Role};
                       """, "owns objects outside the RTM-API-REALTIME-DISPATCHER-ROLE contract"),
                     ($"GRANT SELECT ON orders.orders TO {Role};", "table privileges differ from the RTM-API-REALTIME-DISPATCHER-ROLE contract"),
                     ($"GRANT SELECT (total_cents) ON orders.orders TO {Role};", "table privileges differ from the RTM-API-REALTIME-DISPATCHER-ROLE contract"),
                     ("REVOKE SELECT ON orders.orders FROM paqueteria_app;", "reads a column paqueteria_app cannot read"),
                     ($"GRANT USAGE ON SCHEMA platform TO {Role};", "schema privileges differ from the RTM-API-REALTIME-DISPATCHER-ROLE contract"),
                     ($"GRANT EXECUTE ON FUNCTION security.claim_dispatch_outbox(text,integer,interval) TO {Role};", "function privileges differ from the RTM-API-REALTIME-DISPATCHER-ROLE contract"),
                 })
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, violation);
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, transaction, AddRealtimeDispatcherRole.UpSql));
            Assert.Contains(message, refused.MessageText, StringComparison.Ordinal);
            await transaction.RollbackAsync();
        }

        await new DatabaseBaselineAssertions().AssertAsync(connection);
    }

    [PostgreSqlContractFact]
    public async Task Notifications_lane_migrates_the_role_grants_up_down_and_up_through_ef()
    {
        // A fresh AI-06/AI-18 installation: the role exists with no privilege until the lane step, its EF Down revokes
        // every grant (the role stays, inert) and its EF Up restores the exact contract.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var container = new PostgreSqlBuilder(PostgreSqlContractFixture.Image)
            .WithDatabase("paqueteria_rtm_api_role")
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
        Assert.Equal("0|0|0", await PrivilegeCountsAsync(connection));
        await using var context = CreateNotificationsContext(connection);
        var migrator = context.GetService<IMigrator>();

        await ExecuteAsync(connection, null, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync(FailStaleWhatsAppNotificationLeases.MigrationId);
        await ExecuteAsync(connection, null, "RESET ROLE");
        Assert.Equal("0|0|0", await PrivilegeCountsAsync(connection));

        await ExecuteAsync(connection, null, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync();
        await ExecuteAsync(connection, null, "RESET ROLE");
        Assert.Equal(
            $"{AddRealtimeDispatcherRole.ColumnGrants.Count}|{AddRealtimeDispatcherRole.ExecutableFunctions.Count}|{AddRealtimeDispatcherRole.UsageSchemas.Count}",
            await PrivilegeCountsAsync(connection));
        await using (var installed = await connection.BeginTransactionAsync())
        {
            await DatabaseBaselineAssertions.AssertRealtimeDispatcherRoleInstalledAsync(connection, installed);
            await new DatabaseBaselineAssertions().AssertAsync(connection, installed);
            await installed.RollbackAsync();
        }

        await ExecuteAsync(connection, null, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync(FailStaleWhatsAppNotificationLeases.MigrationId);
        await ExecuteAsync(connection, null, "RESET ROLE");
        Assert.Equal("0|0|0", await PrivilegeCountsAsync(connection));
        Assert.False(await E002NotificationStateReader.IsRealtimeDispatcherRoleAppliedAsync(connection));
        await new DatabaseBaselineAssertions().AssertAsync(connection);

        await ExecuteAsync(connection, null, "SET ROLE paqueteria_migrator");
        await migrator.MigrateAsync();
        await ExecuteAsync(connection, null, "RESET ROLE");
        await using (var reinstalled = await connection.BeginTransactionAsync())
        {
            await DatabaseBaselineAssertions.AssertRealtimeDispatcherRoleInstalledAsync(connection, reinstalled);
            await reinstalled.RollbackAsync();
        }
    }

    private static async Task<string> PrivilegeCountsAsync(NpgsqlConnection connection) =>
        await ScalarAsync<string>(connection, null,
            """
            SELECT concat_ws('|',
              (SELECT count(*) FROM pg_attribute a CROSS JOIN LATERAL aclexplode(a.attacl) acl
                WHERE a.attacl IS NOT NULL AND acl.grantee='paqueteria_realtime'::regrole),
              (SELECT count(*) FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) acl
                WHERE p.proacl IS NOT NULL AND acl.grantee='paqueteria_realtime'::regrole),
              (SELECT count(*) FROM pg_namespace n CROSS JOIN LATERAL aclexplode(n.nspacl) acl
                WHERE n.nspacl IS NOT NULL AND acl.grantee='paqueteria_realtime'::regrole))
            """);

    private static NotificationsDbContext CreateNotificationsContext(NpgsqlConnection connection) =>
        new(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(NotificationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_notifications", "platform");
            })
            .Options);

    private static async Task<Guid> OrganizationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(connection, transaction,
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@id,'Synthetic RTM','Synthetic RTM','BUSINESS')",
            ("id", id));
        return id;
    }

    private static async Task<Guid> MemberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organization)
    {
        var user = Guid.NewGuid();
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO identity.users(id,identity_subject,status) VALUES (@user,@subject,'ACTIVE');
            INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status)
            VALUES (gen_random_uuid(),@user,@organization,'DISPATCHER','ACTIVE');
            """,
            ("user", user), ("subject", $"synthetic-rtm-{user:N}"), ("organization", organization));
        return user;
    }

    private static Task<long> MembershipCountAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid[] organizations) =>
        ScalarAsync<long>(connection, transaction,
            "SELECT count(*) FROM organizations.organization_memberships WHERE organization_id=ANY(@organizations)",
            ("organizations", organizations));

    private static async Task SetOrganizationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid[] organizations)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('app.current_user_id','',true), set_config('app.current_org_ids',@ids::uuid[]::text,true)",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = organizations });
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>One outbox row the claims order first (priority 100, available long ago), or a lapsed lease.</summary>
    private static async Task<Guid> OutboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organization, string topic, bool stale = false)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,priority,status,
              attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,created_at)
            VALUES (
              @id,@org,jsonb_build_object('organization_ids',jsonb_build_array(@org::text)),@topic,'Order',gen_random_uuid(),1,
              '{}'::jsonb,100,CASE WHEN @stale THEN 'PROCESSING' ELSE 'PENDING' END,CASE WHEN @stale THEN 1 ELSE 0 END,
              timestamptz '2000-01-01',
              CASE WHEN @stale THEN timestamptz '2000-01-01' END,CASE WHEN @stale THEN 'synthetic-crashed' END,
              CASE WHEN @stale THEN gen_random_uuid() END,CASE WHEN @stale THEN timestamptz '2000-01-01 00:01' END,
              timestamptz '2000-01-01')
            """,
            ("id", id), ("org", organization), ("topic", topic), ("stale", stale));
        return id;
    }

    private static async Task<Guid> LocationOutboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organization, bool stale = false)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO platform.location_outbox_events(
              id,owner_org_id,driver_position_id,topic,payload,status,attempts,available_at,locked_at,locked_by,lease_token,
              lease_expires_at,created_at)
            VALUES (
              @id,@org,gen_random_uuid(),'drivers.location-updated','{}'::jsonb,
              CASE WHEN @stale THEN 'PROCESSING' ELSE 'PENDING' END,CASE WHEN @stale THEN 1 ELSE 0 END,
              timestamptz '2000-01-01',
              CASE WHEN @stale THEN timestamptz '2000-01-01' END,CASE WHEN @stale THEN 'synthetic-crashed' END,
              CASE WHEN @stale THEN gen_random_uuid() END,CASE WHEN @stale THEN timestamptz '2000-01-01 00:01' END,
              timestamptz '2000-01-01')
            """,
            ("id", id), ("org", organization), ("stale", stale));
        return id;
    }

    private static async Task<Dictionary<Guid, Guid>> ClaimAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string function)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT id,lease_token FROM {function}(@worker,@batch,interval '2 minutes')", connection, transaction);
        command.Parameters.AddWithValue("worker", Worker);
        command.Parameters.AddWithValue("batch", 200);
        var claimed = new Dictionary<Guid, Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            claimed[reader.GetGuid(0)] = reader.GetGuid(1);
        }

        return claimed;
    }

    private static Task<bool> SettleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string function, Guid id, Guid leaseToken) =>
        ScalarAsync<bool>(connection, transaction,
            $"SELECT {function}(@id,@lease,'PROCESSED',NULL,NULL)",
            ("id", id), ("lease", leaseToken));

    private static Task<string> StatusAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string table, Guid id) =>
        ScalarAsync<string>(connection, transaction, $"SELECT status FROM {table} WHERE id=@id", ("id", id));

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>
/// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12 without a database: the lane step, the baseline assertions and the E-002
/// routine map name one and the same privilege set, in the order the catalog checks sort it.
/// </summary>
public sealed class RealtimeDispatcherRoleContractTests
{
    [Fact]
    public void Lane_baseline_and_routine_map_declare_the_same_privilege_set()
    {
        Assert.Equal(AddRealtimeDispatcherRole.ColumnGrants, DatabaseBaselineAssertions.RealtimeDispatcherColumnGrants);
        Assert.Equal(AddRealtimeDispatcherRole.ExecutableFunctions, DatabaseBaselineAssertions.RealtimeDispatcherFunctions);
        Assert.Equal(AddRealtimeDispatcherRole.UsageSchemas, DatabaseBaselineAssertions.RealtimeDispatcherUsageSchemas);
        Assert.Equal(AddRealtimeDispatcherRole.ExecutableFunctions, E002RoutineMap.RealtimeDispatcherRoutines);
        Assert.Equal(
            AddRealtimeDispatcherRole.ColumnGrants.Order(StringComparer.Ordinal),
            AddRealtimeDispatcherRole.ColumnGrants);
        Assert.Equal(AddRealtimeDispatcherRole.ColumnGrants.Count, AddRealtimeDispatcherRole.ColumnGrants.Distinct().Count());
        Assert.Equal(AddRealtimeDispatcherRole.MigrationId, E002NotificationStateReader.RealtimeDispatcherRoleMigrationId);
        Assert.All(AddRealtimeDispatcherRole.ColumnGrants, grant => Assert.EndsWith(":SELECT", grant, StringComparison.Ordinal));
    }
}
