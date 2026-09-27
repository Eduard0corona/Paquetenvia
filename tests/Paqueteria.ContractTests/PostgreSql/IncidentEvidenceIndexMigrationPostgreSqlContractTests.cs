using Incidents.Infrastructure.Persistence;
using Incidents.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// The Incidents lane's evidence index: the canonical migrator applies it after the INC-001
/// adoption, records both migrations in the lane's history, and the index rolls back and forward
/// against a real PostgreSQL as the migrator role, leaving the adoption untouched.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class IncidentEvidenceIndexMigrationPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Module = "Incidents";

    [PostgreSqlContractFact]
    public async Task The_shared_fixture_carries_the_evidence_index_as_the_latest_incidents_migration()
    {
        var verified = Assert.Single(ModuleMigrationCoordinator.VerifySources(), state => state.Module == Module);
        Assert.Equal(IndexIncidentEvidenceByOrderProof.MigrationId, verified.MigrationId);

        var applied = Assert.Single(
            await new ModuleMigrationCoordinator().AssertAsync(fixture.DeploymentConnectionString, CancellationToken.None),
            state => state.Module == Module);
        Assert.Equal("APPLIED", applied.Status);
        Assert.True(await IndexPresentAsync(fixture.DeploymentConnectionString));
    }

    [PostgreSqlContractFact]
    public async Task The_evidence_index_migration_rolls_back_and_forward_as_the_migrator()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("incevidenceidx");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            Assert.Equal(
                DatabaseBaselineApplyStatus.Applied,
                (await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString)).Status);
            var coordinator = new ModuleMigrationCoordinator();
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.Equal("APPLIED", await LaneStatusAsync(coordinator, connectionString));
            Assert.True(await IndexPresentAsync(connectionString));
            Assert.Equal(
                [AdoptCanonicalIncidentsBaseline.MigrationId, IndexIncidentEvidenceByOrderProof.MigrationId],
                await HistoryAsync(connectionString));
            Assert.True(await UsesIndexAsync(connectionString));

            // Down: only the index goes; the adoption and its evidence table stay.
            await RollBackIndexMigrationAsMigratorAsync(connectionString);
            Assert.False(await IndexPresentAsync(connectionString));
            Assert.Equal([AdoptCanonicalIncidentsBaseline.MigrationId], await HistoryAsync(connectionString));
            Assert.True(await ScalarAsync<bool>(connectionString,
                "SELECT to_regclass('incidents.incident_evidence') IS NOT NULL"));
            Assert.Equal("PENDING", await LaneStatusAsync(coordinator, connectionString));

            // Up again, through the canonical migrator as deployment runs it.
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.All(
                await coordinator.AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            Assert.True(await IndexPresentAsync(connectionString));

            // The index SQL is idempotent when it runs again.
            await MigrateSqlAsMigratorAsync(connectionString, IndexIncidentEvidenceByOrderProof.UpSql);
            Assert.True(await IndexPresentAsync(connectionString));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    private static Task<bool> IndexPresentAsync(string connectionString) =>
        ScalarAsync<bool>(connectionString, $"""
            SELECT EXISTS (
              SELECT 1
              FROM pg_index x
              JOIN pg_class i ON i.oid=x.indexrelid
              WHERE x.indrelid='incidents.incident_evidence'::regclass
                AND i.relname='{IndexIncidentEvidenceByOrderProof.IndexName}'
                AND x.indisvalid
                AND pg_get_indexdef(x.indexrelid) LIKE '%(order_id, proof_id)')
            """);

    /// <summary>The ORD-002 lookup by order and proof can be served by the new index.</summary>
    private static async Task<bool> UsesIndexAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setting = new NpgsqlCommand("SET LOCAL enable_seqscan=off", connection, transaction))
        {
            await setting.ExecuteNonQueryAsync();
        }

        await using var explain = new NpgsqlCommand(
            """
            EXPLAIN SELECT 1 FROM incidents.incident_evidence ev
            WHERE ev.proof_id=gen_random_uuid() AND ev.order_id=gen_random_uuid()
            """,
            connection,
            transaction);
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(0));
            }
        }

        await transaction.RollbackAsync();
        return plan.Any(line => line.Contains(IndexIncidentEvidenceByOrderProof.IndexName, StringComparison.Ordinal));
    }

    private static async Task<string[]> HistoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"MigrationId\" FROM platform.__ef_migrations_history_incidents ORDER BY \"MigrationId\"",
            connection);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return [.. ids];
    }

    private static async Task<string> LaneStatusAsync(ModuleMigrationCoordinator coordinator, string connectionString) =>
        (await coordinator.PlanAsync(connectionString, CancellationToken.None))
            .Single(state => state.Module == Module)
            .Status;

    /// <summary>
    /// Reverts the index migration exactly as EF Core's migrator does: its Down operations through
    /// the provider's SQL generator and the removal of its history row, in one transaction, as
    /// <c>paqueteria_migrator</c>. The pre-EF-format adoption identifier cannot be named as a
    /// migrator target, so the step is driven through the same services instead.
    /// </summary>
    private static async Task RollBackIndexMigrationAsMigratorAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<IncidentsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(IncidentsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_incidents", "platform");
            })
            .Options;
        await using var context = new IncidentsDbContext(options, new TenantDatabaseExecutionState());
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(
            assembly.Migrations[IndexIncidentEvidenceByOrderProof.MigrationId],
            context.Database.ProviderName!);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(migration.DownOperations);
        var history = context.GetService<IHistoryRepository>()
            .GetDeleteScript(IndexIncidentEvidenceByOrderProof.MigrationId);

        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var sql in commands.Select(command => command.CommandText).Append(history))
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task MigrateSqlAsMigratorAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
