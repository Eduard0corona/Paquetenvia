using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Orders.Infrastructure.Persistence.Migrations;
using Paqueteria.Contracts.Tracking;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Database.Evolution.Migrations;

namespace Paqueteria.ContractTests.PostgreSql.Tracking;

/// <summary>
/// TRK-002-AUTO-LINK database steps on real PostgreSQL 18/PostGIS: the Orders lane step
/// <c>20260929000100_AddTrackingLinkGenerations</c> (generation and key_version columns, two partial unique
/// indexes) and the PlatformEvolution step <c>20260929000200_BoundTrackingLinksToOrderLifecycle</c> (the lifecycle
/// bound in the public lookup). A fresh AI-06 installation and an upgraded populated one end in the same shape;
/// both steps reapply idempotently, refuse another pre-existing shape and never roll back.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class TrackingLinkMigrationPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Fresh_installation_carries_the_generation_columns_indexes_and_lifecycle_bound()
    {
        await AssertInstalledAsync();
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await new DatabaseBaselineAssertions().AssertAsync(connection);
    }

    [PostgreSqlContractFact]
    public async Task Both_steps_reapply_idempotently_and_their_rollbacks_fail_closed()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using (var reapply = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, reapply, AddTrackingLinkGenerations.UpSql);
            await ExecuteAsync(connection, reapply, BoundTrackingLinksToOrderLifecycle.UpSql);
            await reapply.RollbackAsync();
        }

        foreach (var (downgrade, code) in new[]
                 {
                     (AddTrackingLinkGenerations.SchemaDowngradeNotSupportedSql, "TRK002_GENERATION_DOWNGRADE_NOT_SUPPORTED"),
                     (BoundTrackingLinksToOrderLifecycle.SchemaDowngradeNotSupportedSql, "TRK002_LIFECYCLE_DOWNGRADE_NOT_SUPPORTED"),
                 })
        {
            await using var transaction = await connection.BeginTransactionAsync();
            var blocked = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, transaction, downgrade));
            Assert.Equal(code, blocked.MessageText);
            await transaction.RollbackAsync();
        }

        await AssertInstalledAsync();
    }

    [PostgreSqlContractFact]
    public async Task The_orders_step_refuses_a_pre_existing_shape_outside_the_contract()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        foreach (var (drift, message) in new[]
                 {
                     ("""
                      DROP INDEX orders.tracking_tokens_order_generation_uq;
                      DROP INDEX orders.tracking_tokens_one_active_derived_uq;
                      ALTER TABLE orders.public_tracking_tokens DROP COLUMN generation;
                      ALTER TABLE orders.public_tracking_tokens ADD COLUMN generation bigint NOT NULL DEFAULT 1;
                      """, "generation columns differ"),
                     ("""
                      DROP INDEX orders.tracking_tokens_order_generation_uq;
                      CREATE INDEX tracking_tokens_order_generation_uq ON orders.public_tracking_tokens(order_id,generation);
                      """, "generation indexes differ"),
                     ("""
                      ALTER TABLE orders.public_tracking_tokens DROP CONSTRAINT public_tracking_tokens_key_version_check;
                      ALTER TABLE orders.public_tracking_tokens ADD CONSTRAINT public_tracking_tokens_key_version_check
                        CHECK (key_version IS NULL OR key_version >= 0);
                      """, "generation checks differ"),
                     ("ALTER TABLE orders.public_tracking_tokens NO FORCE ROW LEVEL SECURITY;", "FORCE ROW LEVEL SECURITY"),
                 })
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, drift);
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, transaction, AddTrackingLinkGenerations.UpSql));
            Assert.Contains(message, refused.MessageText, StringComparison.Ordinal);
            await transaction.RollbackAsync();
        }

        await AssertInstalledAsync();
    }

    [PostgreSqlContractFact]
    public async Task Populated_pre_auto_link_installation_upgrades_through_both_lanes_without_rewriting_rows()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "IN_TRANSIT");
        var legacy = new TrackingTokenHasher().CreateToken();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO orders.public_tracking_tokens(id,order_id,owner_org_id,token_hash,expires_at)
            VALUES (gen_random_uuid(),@order,@org,@hash,clock_timestamp()+interval '7 days');
            """,
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("hash", SHA256.HashData(Encoding.UTF8.GetBytes(legacy))));
        var before = await SnapshotAsync(scenario.OrderId);
        var coordinator = new ModuleMigrationCoordinator();
        try
        {
            // What an installation provisioned before TRK-002-AUTO-LINK looks like: no generation columns, the pilot
            // delta body of the projection, both histories one step behind.
            await ExecuteAdminAsync(
                $"""
                DROP INDEX orders.tracking_tokens_order_generation_uq;
                DROP INDEX orders.tracking_tokens_one_active_derived_uq;
                ALTER TABLE orders.public_tracking_tokens DROP COLUMN key_version, DROP COLUMN generation;
                DELETE FROM platform."__ef_migrations_history_orders" WHERE "MigrationId"='{AddTrackingLinkGenerations.MigrationId}';
                DELETE FROM platform."__ef_migrations_history_platform_evolution" WHERE "MigrationId"='{BoundTrackingLinksToOrderLifecycle.MigrationId}';
                """);
            await ExecuteAdminAsync(ApplyPilotContractDeltas.UpSql);
            Assert.DoesNotContain(
                BoundTrackingLinksToOrderLifecycle.LifecycleMarker,
                await ProjectionSourceAsync(),
                StringComparison.Ordinal);
            await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync())
            {
                await new DatabaseBaselineAssertions().AssertAsync(connection);
            }

            var pending = await coordinator.PlanAsync(fixture.DeploymentConnectionString, CancellationToken.None);
            Assert.Equal("PENDING", pending.Single(state => state.Module == "Orders").Status);
            Assert.Equal("PENDING", pending.Single(state => state.Module == "PlatformEvolution").Status);
            Assert.All(
                pending.Where(state => state.Module is not ("Orders" or "PlatformEvolution")),
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

            // The existing row is untouched and reads as a pre-derivation link: generation 1, no key version.
            Assert.Equal(before, await SnapshotAsync(scenario.OrderId));
            await using var command = fixture.AdminDataSource.CreateCommand(
                "SELECT generation, key_version IS NULL FROM orders.public_tracking_tokens WHERE order_id=@order;");
            command.Parameters.AddWithValue("order", scenario.OrderId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.True(reader.GetBoolean(1));
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
            WHERE table_schema='orders' AND table_name='public_tracking_tokens'
              AND column_name IN ('generation','key_version');
            """);
        Assert.Equal(
            ["generation:integer:NO:1", "key_version:integer:YES:"],
            (string[])(await columns.ExecuteScalarAsync())!);

        await using var indexes = fixture.AdminDataSource.CreateCommand(
            "SELECT pg_get_indexdef(to_regclass(name)) FROM unnest(@names) AS listed(name) ORDER BY 1;");
        indexes.Parameters.Add(new NpgsqlParameter<string[]>(
            "names",
            AddTrackingLinkGenerations.Indexes.Select(index => index.Name).ToArray()));
        var definitions = new List<string>();
        await using (var reader = await indexes.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                definitions.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            AddTrackingLinkGenerations.Indexes.Select(index => index.Definition).Order(StringComparer.Ordinal),
            definitions);

        var source = await ProjectionSourceAsync();
        Assert.Contains(BoundTrackingLinksToOrderLifecycle.LifecycleMarker, source, StringComparison.Ordinal);
        Assert.Contains("ORDER BY e.occurred_at, e.aggregate_version", source, StringComparison.Ordinal);
        Assert.Contains("+ interval '24 hours'", source, StringComparison.Ordinal);
        Assert.Contains(
            "extensions.digest(pg_catalog.convert_to(p_token,'UTF8'),'sha256')",
            source,
            StringComparison.Ordinal);

        await using var owner = fixture.AdminDataSource.CreateCommand(
            """
            SELECT pg_get_userbyid(proowner) || ':' || prosecdef::text || ':' ||
                   has_function_privilege('paqueteria_app',oid,'EXECUTE')::text || ':' ||
                   has_function_privilege('public',oid,'EXECUTE')::text
            FROM pg_proc WHERE oid='security.get_public_tracking_projection(text)'::regprocedure;
            """);
        Assert.Equal("paqueteria_bootstrap:true:true:false", (string)(await owner.ExecuteScalarAsync())!);
    }

    private async Task<string> ProjectionSourceAsync()
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT prosrc FROM pg_proc WHERE oid='security.get_public_tracking_projection(text)'::regprocedure;");
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> SnapshotAsync(Guid orderId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT string_agg(
              id::text || '|' || encode(token_hash,'hex') || '|' || expires_at::text || '|' ||
              COALESCE(revoked_at::text,'') || '|' || created_at::text || '|' || owner_org_id::text, ',' ORDER BY id)
            FROM orders.public_tracking_tokens WHERE order_id=@order;
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
