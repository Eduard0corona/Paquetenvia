using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// E-002 v0.9 Amendment 1/2/8: structural classification comes from canonical database objects only.
/// The seven canonical roles are cluster-wide and already exist in the fixture, so every isolated database here
/// is the "roles-only" prestate, which must remain Clean-compatible.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class DatabaseBaselineStateDetectorContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Roles_only_prestate_is_clean_compatible()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("detectorroles");
        try
        {
            var state = await DetectAsync(connectionString);
            Assert.Equal(DatabaseBaselineStatus.Clean, state.Status);
            Assert.Contains("role:paqueteria_migrator", state.PresentCriticalObjects);
            Assert.DoesNotContain(state.PresentCriticalObjects, value => !value.StartsWith("role:", StringComparison.Ordinal));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Extensions_only_prestate_is_partial_and_fails_closed()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("detectorext");
        try
        {
            await ExecuteAsync(connectionString,
                "CREATE SCHEMA extensions; CREATE EXTENSION pgcrypto WITH SCHEMA extensions;");

            var state = await DetectAsync(connectionString);
            Assert.Equal(DatabaseBaselineStatus.Partial, state.Status);
            Assert.Equal(["schema:extensions"],
                state.PresentCriticalObjects.Where(value => !value.StartsWith("role:", StringComparison.Ordinal)));

            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            var exception = await Assert.ThrowsAsync<PartialDatabaseBaselineException>(
                () => new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString));
            Assert.StartsWith("E002_BASELINE_PRESTATE_PARTIAL; STOP_FOR_CONTRACT_REVIEW", exception.Message, StringComparison.Ordinal);
            Assert.True(await ScalarAsync(connectionString, "SELECT to_regnamespace('security') IS NULL"));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Postgis_only_in_public_does_not_create_application_object_presence()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("detectorpostgis");
        try
        {
            await ExecuteAsync(connectionString, "CREATE EXTENSION postgis;");
            var state = await DetectAsync(connectionString);
            Assert.Equal(DatabaseBaselineStatus.Clean, state.Status);
            Assert.DoesNotContain(state.PresentCriticalObjects, value => value.StartsWith("schema:", StringComparison.Ordinal));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    private static async Task<DatabaseBaselineState> DetectAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return await new DatabaseBaselineStateDetector().DetectAsync(connection);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
