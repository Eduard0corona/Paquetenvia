using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.DataProtection.Migrations;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// SCL-001: the shared Data Protection key ring is only real if the canonical deployment migrator
/// plans, applies and asserts its own lane. A clean database taken through the migrator alone has
/// to end up with the migration, the table and the runtime grants the replicas depend on.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class Scl001DataProtectionMigrationLaneTests(PostgreSqlContractFixture fixture)
{
    private const string Module = "DataProtection";
    private const string HistoryTable = "platform.__ef_migrations_history_platform";

    [PostgreSqlContractFact]
    public void Verified_sources_carry_the_data_protection_lane()
    {
        var verified = ModuleMigrationCoordinator.VerifySources();
        var lane = Assert.Single(verified, state => state.Module == Module);

        Assert.Equal(HistoryTable, lane.HistoryTable);
        Assert.Equal(AddDistributedDataProtectionKeyRing.MigrationId, lane.MigrationId);
        Assert.Equal("VERIFIED", lane.Status);
    }

    [PostgreSqlContractFact]
    public async Task Canonical_migrator_applies_the_data_protection_lane_from_a_clean_database()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("scl001lane");
        try
        {
            await DeployBaselineAsync(connectionString);
            var coordinator = new ModuleMigrationCoordinator();

            // A baseline-only database carries neither the lane nor the key ring.
            Assert.Equal("PENDING", await LaneStatusAsync(coordinator, connectionString));
            Assert.Null(await ScalarAsync<string>(
                connectionString,
                "SELECT to_regclass('platform.data_protection_keys')::text"));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.AssertAsync(connectionString, CancellationToken.None));

            await coordinator.ApplyAsync(connectionString, CancellationToken.None);

            Assert.All(
                await coordinator.AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            Assert.Equal(
                AddDistributedDataProtectionKeyRing.MigrationId,
                await ScalarAsync<string>(
                    connectionString,
                    "SELECT \"MigrationId\" FROM " + HistoryTable));
            Assert.Equal("paqueteria_migrator", await ScalarAsync<string>(
                connectionString,
                """
                SELECT pg_get_userbyid(relowner)
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='platform' AND c.relname='data_protection_keys'
                """));
            Assert.True(await ScalarAsync<bool>(
                connectionString,
                """
                SELECT c.relrowsecurity AND c.relforcerowsecurity
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='platform' AND c.relname='data_protection_keys'
                """));
            Assert.Equal(4, await ScalarAsync<int>(
                connectionString,
                """
                SELECT count(*)::integer
                FROM pg_class c
                JOIN pg_namespace n ON n.oid=c.relnamespace
                CROSS JOIN LATERAL aclexplode(COALESCE(c.relacl, acldefault('r', c.relowner))) acl
                JOIN pg_roles g ON g.oid=acl.grantee
                WHERE n.nspname='platform' AND c.relname='data_protection_keys'
                  AND g.rolname IN ('paqueteria_app','paqueteria_worker')
                  AND acl.privilege_type IN ('SELECT','INSERT')
                """));
            Assert.Equal(1, await ScalarAsync<int>(
                connectionString,
                """
                SELECT count(*)::integer FROM pg_trigger
                WHERE NOT tgisinternal AND tgname='data_protection_keys_append_only'
                """));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Plan_and_assert_detect_a_drifted_data_protection_history_lane()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("scl001drift");
        try
        {
            await DeployBaselineAsync(connectionString);
            var coordinator = new ModuleMigrationCoordinator();
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);

            await ExecuteAsync(
                connectionString,
                "SET ROLE paqueteria_migrator; INSERT INTO " + HistoryTable +
                "(\"MigrationId\",\"ProductVersion\") " +
                "VALUES ('20260922000200_UnknownDataProtectionEvolution','10.0.0');");

            Assert.Equal("DRIFT", await LaneStatusAsync(coordinator, connectionString));
            var asserted = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.AssertAsync(connectionString, CancellationToken.None));
            Assert.Contains(Module, asserted.Message, StringComparison.Ordinal);

            // Drift stops the whole apply before any lane is touched.
            var applied = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.ApplyAsync(connectionString, CancellationToken.None));
            Assert.Equal($"Migration history drift detected for {Module}.", applied.Message);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Assert_fails_when_the_applied_lane_no_longer_leaves_a_usable_key_ring()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("scl001ring");
        try
        {
            await DeployBaselineAsync(connectionString);
            var coordinator = new ModuleMigrationCoordinator();
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);

            // A replica cannot publish its key material without INSERT, so a recorded history row
            // on its own is not enough to call the lane applied.
            await ExecuteAsync(
                connectionString,
                """
                SET ROLE paqueteria_migrator;
                REVOKE INSERT ON platform.data_protection_keys FROM paqueteria_worker;
                """);
            var revoked = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.AssertAsync(connectionString, CancellationToken.None));
            Assert.Contains("runtime_grants=3", revoked.Message, StringComparison.Ordinal);

            await ExecuteAsync(
                connectionString,
                """
                SET ROLE paqueteria_migrator;
                DROP TABLE platform.data_protection_keys;
                """);
            var dropped = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.AssertAsync(connectionString, CancellationToken.None));
            Assert.Contains(
                "platform.data_protection_keys is missing",
                dropped.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    private static async Task DeployBaselineAsync(string connectionString)
    {
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        Assert.Equal(
            DatabaseBaselineApplyStatus.Applied,
            (await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString)).Status);
    }

    private static async Task<string> LaneStatusAsync(
        ModuleMigrationCoordinator coordinator,
        string connectionString) =>
        (await coordinator.PlanAsync(connectionString, CancellationToken.None))
            .Single(state => state.Module == Module).Status;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }
}
