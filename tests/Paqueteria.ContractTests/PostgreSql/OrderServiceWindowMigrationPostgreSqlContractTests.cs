using Npgsql;
using Orders.Infrastructure.Persistence.Migrations;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02 database step on real PostgreSQL 18/PostGIS: the Orders lane step
/// <c>20261002000100_AddOrderServiceWindow</c> (two nullable timestamptz columns and
/// <c>orders_service_window_check</c>). A fresh AI-06 installation and an upgraded populated one end in the same
/// shape; the step reapplies idempotently, refuses another pre-existing shape, never rewrites a row and its
/// rollback fails closed.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class OrderServiceWindowMigrationPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Fresh_installation_carries_the_service_window_columns_and_check()
    {
        await AssertInstalledAsync();
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await new DatabaseBaselineAssertions().AssertAsync(connection);
    }

    [PostgreSqlContractFact]
    public async Task The_step_reapplies_idempotently_and_its_rollback_fails_closed()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using (var reapply = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, reapply, AddOrderServiceWindow.UpSql);
            await ExecuteAsync(connection, reapply, AddOrderServiceWindow.UpSql);
            await reapply.RollbackAsync();
        }

        await using (var downgrade = await connection.BeginTransactionAsync())
        {
            var blocked = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, downgrade, AddOrderServiceWindow.SchemaDowngradeNotSupportedSql));
            Assert.Equal("ORD_SERVICE_WINDOW_DOWNGRADE_NOT_SUPPORTED", blocked.MessageText);
            await downgrade.RollbackAsync();
        }

        await AssertInstalledAsync();
    }

    [PostgreSqlContractFact]
    public async Task The_step_refuses_a_pre_existing_shape_outside_the_contract()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        foreach (var (drift, message) in new[]
                 {
                     ("""
                      ALTER TABLE orders.orders DROP CONSTRAINT orders_service_window_check;
                      ALTER TABLE orders.orders DROP COLUMN service_window_to;
                      """, "partially present"),
                     ("""
                      ALTER TABLE orders.orders DROP CONSTRAINT orders_service_window_check;
                      ALTER TABLE orders.orders ALTER COLUMN service_window_to TYPE timestamp without time zone;
                      ALTER TABLE orders.orders ADD CONSTRAINT orders_service_window_check
                        CHECK ((service_window_from IS NULL) = (service_window_to IS NULL));
                      """, "service window columns differ"),
                     ("""
                      ALTER TABLE orders.orders DROP CONSTRAINT orders_service_window_check;
                      ALTER TABLE orders.orders ADD CONSTRAINT orders_service_window_check
                        CHECK ((service_window_from IS NULL) = (service_window_to IS NULL));
                      """, "service window check differs"),
                     ("""
                      ALTER TABLE orders.orders DROP CONSTRAINT orders_service_window_check;
                      ALTER TABLE orders.orders ADD CONSTRAINT orders_service_window_check
                        CHECK ((service_window_from IS NULL) = (service_window_to IS NULL)
                          AND (service_window_from IS NULL OR service_window_from < service_window_to)) NOT VALID;
                      """, "service window check differs"),
                     ("ALTER TABLE orders.orders NO FORCE ROW LEVEL SECURITY;", "FORCE ROW LEVEL SECURITY"),
                 })
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, drift);
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, transaction, AddOrderServiceWindow.UpSql));
            Assert.Contains(message, refused.MessageText, StringComparison.Ordinal);
            await transaction.RollbackAsync();
        }

        await AssertInstalledAsync();
    }

    [PostgreSqlContractFact]
    public async Task Populated_pre_window_installation_upgrades_through_the_orders_lane_without_rewriting_rows()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "IN_TRANSIT");
        var coordinator = new ModuleMigrationCoordinator();
        try
        {
            // What an installation provisioned before ORD-SERVICE-WINDOW-OPTIONAL looks like: no window columns
            // and the Orders history one step behind.
            await ExecuteAdminAsync(
                $"""
                ALTER TABLE orders.orders DROP CONSTRAINT orders_service_window_check;
                ALTER TABLE orders.orders DROP COLUMN service_window_from, DROP COLUMN service_window_to;
                DELETE FROM platform."__ef_migrations_history_orders" WHERE "MigrationId" IN (
                  '{AddOrderServiceWindow.MigrationId}','{AddOrderAutoCloseDiscovery.MigrationId}');
                """);
            var before = await SnapshotAsync(scenario.OrderId);

            var pending = await coordinator.PlanAsync(fixture.DeploymentConnectionString, CancellationToken.None);
            Assert.Equal("PENDING", pending.Single(state => state.Module == "Orders").Status);
            Assert.All(
                pending.Where(state => state.Module != "Orders"),
                state => Assert.Equal("APPLIED", state.Status));

            await coordinator.ApplyAsync(fixture.DeploymentConnectionString, CancellationToken.None);

            Assert.All(
                await coordinator.AssertAsync(fixture.DeploymentConnectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            await AssertInstalledAsync();
            await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync())
            {
                await new DatabaseBaselineAssertions().AssertAsync(connection);
            }

            // The existing order is untouched and reads as having no window of its own.
            Assert.Equal(before, await SnapshotAsync(scenario.OrderId));
            await using var command = fixture.AdminDataSource.CreateCommand(
                "SELECT service_window_from IS NULL AND service_window_to IS NULL FROM orders.orders WHERE id=@order;");
            command.Parameters.AddWithValue("order", scenario.OrderId);
            Assert.True((bool)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            var states = await coordinator.PlanAsync(fixture.DeploymentConnectionString, CancellationToken.None);
            if (states.Any(state => state.Status != "APPLIED"))
            {
                await coordinator.ApplyAsync(fixture.DeploymentConnectionString, CancellationToken.None);
            }
        }
    }

    private async Task AssertInstalledAsync()
    {
        await using var columns = fixture.AdminDataSource.CreateCommand(
            """
            SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default,'')
              ORDER BY column_name)
            FROM information_schema.columns
            WHERE table_schema='orders' AND table_name='orders'
              AND column_name IN ('service_window_from','service_window_to');
            """);
        Assert.Equal(
            ["service_window_from:timestamp with time zone:YES:", "service_window_to:timestamp with time zone:YES:"],
            (string[])(await columns.ExecuteScalarAsync())!);

        await using var check = fixture.AdminDataSource.CreateCommand(
            """
            SELECT pg_get_constraintdef(oid) || ':' || convalidated::text
            FROM pg_constraint
            WHERE conrelid='orders.orders'::regclass AND conname=@name AND contype='c';
            """);
        check.Parameters.AddWithValue("name", AddOrderServiceWindow.CheckName);
        Assert.Equal(AddOrderServiceWindow.CheckDefinition + ":true", (string)(await check.ExecuteScalarAsync())!);
    }

    /// <summary>Every column the step does not own, so an upgrade that rewrote a value would show.</summary>
    private async Task<string> SnapshotAsync(Guid orderId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT (to_jsonb(o) - 'service_window_from' - 'service_window_to')::text
            FROM orders.orders o WHERE o.id=@order;
            """);
        command.Parameters.AddWithValue("order", orderId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }
}
