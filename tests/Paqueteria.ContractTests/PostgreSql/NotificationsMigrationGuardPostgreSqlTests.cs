using System.Security.Cryptography;
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
    public async Task Cutover_and_rollback_are_fail_closed_on_active_owned_rows()
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
        await ExecuteAsync(connection, AddTenantSafeOutboxNotifications.UpSql);
        var active = Guid.NewGuid();
        await ExecuteAsync(connection, "RESET ROLE");
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
            ExecuteAsync(connection, AddTenantSafeOutboxNotifications.DownSql));
        Assert.Contains("rollback blocked", rollback.MessageText, StringComparison.Ordinal);

        await ExecuteAsync(connection, "RESET ROLE");
        await ExecuteAsync(connection,
            "UPDATE platform.outbox_events SET status='DEAD',processed_at=clock_timestamp() WHERE id=@id",
            new NpgsqlParameter("id", active));
        await ExecuteAsync(connection, "SET ROLE paqueteria_migrator");
        await ExecuteAsync(connection, AddTenantSafeOutboxNotifications.DownSql);
        await using var assertion = new NpgsqlCommand(
            """
            SELECT to_regclass('notifications.notification_status_events') IS NOT NULL
              AND has_function_privilege('paqueteria_worker','security.claim_outbox(text,integer,interval)','EXECUTE')
              AND NOT has_function_privilege('paqueteria_worker','security.claim_notifications_outbox(text,integer,interval)','EXECUTE')
            """,
            connection);
        Assert.True(await assertion.ExecuteScalarAsync() is true);
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
}
