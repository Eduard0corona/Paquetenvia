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
    public async Task External_offer_routing_migration_preserves_function_and_history_owners_across_up_down_up()
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
        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await using var context = CreateNotificationsContext(connection);
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync();
        await AssertRoutingStateAsync(connection, "REALTIME", routeMigrationApplied: true);

        await migrator.MigrateAsync(AddTenantSafeOutboxNotifications.MigrationId);
        await AssertRoutingStateAsync(connection, "UNROUTED", routeMigrationApplied: false);

        await migrator.MigrateAsync();
        await AssertRoutingStateAsync(connection, "REALTIME", routeMigrationApplied: true);
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
            await context.Database.MigrateAsync();
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
        string expectedConsumer,
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
                  pg_get_userbyid(target_function.proowner),
                  pg_get_userbyid(history.relowner),
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
            command.Parameters.AddWithValue("migration_id", RouteExternalOfferRealtime.MigrationId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(expectedConsumer, reader.GetString(0));
            Assert.Equal("paqueteria_outbox_executor", reader.GetString(1));
            Assert.Equal("paqueteria_migrator", reader.GetString(2));
            Assert.Equal(routeMigrationApplied, reader.GetBoolean(3));
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        }
    }
}
