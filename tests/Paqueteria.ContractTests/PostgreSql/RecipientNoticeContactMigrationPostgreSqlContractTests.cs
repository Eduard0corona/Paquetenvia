using Locations.Infrastructure.Persistence;
using Locations.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-002; AI-06 declared contract) on a real PostgreSQL 18 / PostGIS 3.6:
/// the Locations lane step <c>20261020000100_AddRecipientNoticeContact</c> adopts the AI-06 columns and checks on a
/// fresh installation and creates them on one whose baseline predates them (no row rewritten); it is idempotent,
/// refuses drift, and its rollback refuses while any of the four columns holds a value; Up, Down and Up again leave the
/// same catalog, and the baseline assertions accept each state the recorded history allows.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class RecipientNoticeContactMigrationPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Module = "Locations";

    private static readonly string[] FullHistory =
        [AdoptCanonicalLocationsBaseline.MigrationId, AddRecipientNoticeContact.MigrationId];

    [PostgreSqlContractFact]
    public async Task The_shared_fixture_carries_the_recipient_contact_as_the_latest_locations_migration()
    {
        var verified = Assert.Single(ModuleMigrationCoordinator.VerifySources(), state => state.Module == Module);
        Assert.Equal(AddRecipientNoticeContact.MigrationId, verified.MigrationId);
        var applied = Assert.Single(
            await new ModuleMigrationCoordinator().AssertAsync(fixture.DeploymentConnectionString, CancellationToken.None),
            state => state.Module == Module);
        Assert.Equal("APPLIED", applied.Status);
        Assert.Equal(FullHistory, await HistoryAsync(fixture.DeploymentConnectionString));
        await AssertCanonicalShapeAsync(fixture.DeploymentConnectionString);
    }

    [PostgreSqlContractFact]
    public async Task Up_down_and_up_again_adopt_create_refuse_drift_and_roll_back_only_without_data()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("ntf002lane");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            Assert.Equal(
                DatabaseBaselineApplyStatus.Applied,
                (await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString)).Status);
            // A fresh AI-06 baseline already carries the columns and checks (the deployer's assertions passed).
            await AssertCanonicalShapeAsync(connectionString);

            var coordinator = new ModuleMigrationCoordinator();
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.Equal(FullHistory, await HistoryAsync(connectionString));
            Assert.Equal("APPLIED", await LaneStatusAsync(coordinator, connectionString));
            await AssertCanonicalShapeAsync(connectionString);
            await AssertBaselineAsync(connectionString);

            // Running the Up SQL again changes nothing.
            await ExecuteAsMigratorAsync(connectionString, AddRecipientNoticeContact.RenderUpSql());
            await AssertCanonicalShapeAsync(connectionString);

            // Drift is refused: a missing check, a different check, a missing or retyped column.
            await ExpectUpRefusalAsync(
                connectionString,
                "ALTER TABLE locations.locations DROP CONSTRAINT locations_notice_email_check;",
                "locations.locations recipient contact checks differ from the NTF-002 contract",
                "ALTER TABLE locations.locations ADD CONSTRAINT locations_notice_email_check CHECK (notice_email_ciphertext IS NULL OR email_ciphertext IS NOT NULL);");
            await ExpectUpRefusalAsync(
                connectionString,
                "ALTER TABLE locations.locations DROP CONSTRAINT locations_notice_key_version_check; ALTER TABLE locations.locations ADD CONSTRAINT locations_notice_key_version_check CHECK (true);",
                "locations.locations recipient contact checks differ from the NTF-002 contract",
                "ALTER TABLE locations.locations DROP CONSTRAINT locations_notice_key_version_check; ALTER TABLE locations.locations ADD CONSTRAINT locations_notice_key_version_check CHECK ((notice_key_version IS NULL) = (notice_phone_ciphertext IS NULL AND notice_email_ciphertext IS NULL));");
            await ExpectUpRefusalAsync(
                connectionString,
                "ALTER TABLE locations.locations DROP CONSTRAINT locations_notice_email_check, DROP COLUMN email_ciphertext;",
                "locations.locations recipient contact columns are partially present",
                "ALTER TABLE locations.locations ADD COLUMN email_ciphertext bytea, ADD CONSTRAINT locations_notice_email_check CHECK (notice_email_ciphertext IS NULL OR email_ciphertext IS NOT NULL);");
            await ExpectUpRefusalAsync(
                connectionString,
                "ALTER TABLE locations.locations ALTER COLUMN notice_key_version TYPE varchar(200);",
                "locations.locations recipient contact columns differ from the NTF-002 contract",
                "ALTER TABLE locations.locations ALTER COLUMN notice_key_version TYPE text;");
            await AssertCanonicalShapeAsync(connectionString);

            var org = Guid.NewGuid();
            var city = Guid.NewGuid();
            await ExecuteAsync(connectionString,
                """
                INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
                VALUES (@org,'NTF-002 lane','NTF-002 lane','BUSINESS');
                INSERT INTO locations.cities(id,state_code,name,timezone) VALUES (@city,'SI',@city_name,'America/Mazatlan');
                """,
                new("org", org), new("city", city), new("city_name", $"NTF-002 {city:N}"));
            var location = Guid.NewGuid();
            await InsertLocationAsync(connectionString, location, org, city);

            // Each column alone blocks the rollback.
            foreach (var set in new[]
                     {
                         "email_ciphertext='\\x01'",
                         "email_ciphertext='\\x01',notice_email_ciphertext='\\x02',notice_key_version='akv:recipient-notice-kek/v'",
                         "notice_phone_ciphertext='\\x03',notice_key_version='akv:recipient-notice-kek/v'",
                     })
            {
                await ExecuteAsync(connectionString, $"UPDATE locations.locations SET {set} WHERE id=@id", new NpgsqlParameter("id", location));
                await AssertRollbackBlockedAsync(connectionString);
                await ExecuteAsync(
                    connectionString,
                    "UPDATE locations.locations SET email_ciphertext=NULL,notice_phone_ciphertext=NULL,notice_email_ciphertext=NULL,notice_key_version=NULL WHERE id=@id",
                    new NpgsqlParameter("id", location));
            }

            // Without data it drops exactly the four columns and the two checks; the row stays.
            await RollBackToAdoptionAsync(connectionString);
            Assert.Equal([AdoptCanonicalLocationsBaseline.MigrationId], await HistoryAsync(connectionString));
            Assert.Equal("PENDING", await LaneStatusAsync(coordinator, connectionString));
            Assert.Equal(0L, await ColumnCountAsync(connectionString));
            Assert.Equal(0L, await CheckCountAsync(connectionString));
            Assert.Equal(1L, await ScalarAsync<long>(connectionString, $"SELECT count(*) FROM locations.locations WHERE id='{location}'"));
            // An installation whose Locations lane has not recorded the step passes with the columns absent.
            await AssertBaselineAsync(connectionString);

            // Up again on what is now an upgraded installation: the canonical DDL runs, the existing row reads as
            // one without an e-mail or a notice copy, and the baseline assertions pass.
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.All(
                await coordinator.AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            Assert.Equal(FullHistory, await HistoryAsync(connectionString));
            await AssertCanonicalShapeAsync(connectionString);
            await AssertBaselineAsync(connectionString);
            Assert.Equal(
                1L,
                await ScalarAsync<long>(connectionString,
                    $"SELECT count(*) FROM locations.locations WHERE id='{location}' AND email_ciphertext IS NULL AND notice_phone_ciphertext IS NULL AND notice_email_ciphertext IS NULL AND notice_key_version IS NULL"));

            // The recorded step requires the exact shape: the baseline assertions refuse drift.
            await ExecuteAsync(connectionString, "ALTER TABLE locations.locations DROP CONSTRAINT locations_notice_email_check;");
            var drift = await Assert.ThrowsAsync<DatabaseAssertionException>(() => AssertBaselineAsync(connectionString));
            Assert.Contains(drift.Violations, violation => violation.Contains("recipient contact", StringComparison.Ordinal));
            await ExecuteAsync(
                connectionString,
                "ALTER TABLE locations.locations ADD CONSTRAINT locations_notice_email_check CHECK (notice_email_ciphertext IS NULL OR email_ciphertext IS NOT NULL);");
            await AssertBaselineAsync(connectionString);

            // And down and up once more, now through a row that held a notice copy and was cleared.
            await RollBackToAdoptionAsync(connectionString);
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            await AssertCanonicalShapeAsync(connectionString);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    private static async Task ExpectUpRefusalAsync(string connectionString, string drift, string message, string repair)
    {
        await ExecuteAsync(connectionString, drift);
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsMigratorAsync(connectionString, AddRecipientNoticeContact.RenderUpSql()));
        Assert.Equal(message, refused.MessageText);
        await ExecuteAsync(connectionString, repair);
    }

    private static async Task AssertCanonicalShapeAsync(string connectionString)
    {
        Assert.Equal(
            DatabaseBaselineAssertions.RecipientNoticeContactColumns,
            await ListAsync(connectionString,
                """
                SELECT column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default,'')
                FROM information_schema.columns
                WHERE table_schema='locations' AND table_name='locations'
                  AND column_name IN ('email_ciphertext','notice_phone_ciphertext','notice_email_ciphertext','notice_key_version')
                ORDER BY column_name
                """));
        Assert.Equal(
            DatabaseBaselineAssertions.RecipientNoticeContactChecks,
            await ListAsync(connectionString,
                """
                SELECT conname || ':' || pg_get_constraintdef(oid) || ':' || convalidated::text
                FROM pg_constraint
                WHERE conrelid='locations.locations'::regclass AND contype='c'
                  AND conname IN ('locations_notice_email_check','locations_notice_key_version_check')
                ORDER BY conname
                """));
        Assert.Equal(
            $"{AddRecipientNoticeContact.NoticeEmailCheckDefinition}|{AddRecipientNoticeContact.NoticeKeyVersionCheckDefinition}",
            await ScalarAsync<string>(connectionString,
                """
                SELECT string_agg(pg_get_constraintdef(oid), '|' ORDER BY conname)
                FROM pg_constraint
                WHERE conrelid='locations.locations'::regclass
                  AND conname IN ('locations_notice_email_check','locations_notice_key_version_check')
                """));
        Assert.Equal(
            "true|true|paqueteria_migrator",
            await ScalarAsync<string>(connectionString,
                "SELECT relrowsecurity::text || '|' || relforcerowsecurity::text || '|' || pg_get_userbyid(relowner) FROM pg_class WHERE oid='locations.locations'::regclass"));
        // The new columns have no default and no index: nothing to read or rewrite.
        Assert.Equal(
            0L,
            await ScalarAsync<long>(connectionString,
                """
                SELECT count(*) FROM pg_indexes
                WHERE schemaname='locations' AND tablename='locations'
                  AND (indexdef LIKE '%email%' OR indexdef LIKE '%notice%')
                """));
    }

    private async Task AssertRollbackBlockedAsync(string connectionString)
    {
        var blocked = await Assert.ThrowsAnyAsync<Exception>(() => RollBackToAdoptionAsync(connectionString));
        var postgres = blocked as PostgresException ?? blocked.InnerException as PostgresException;
        Assert.NotNull(postgres);
        Assert.Equal(AddRecipientNoticeContact.DowngradeBlockedMessage, postgres.MessageText);
        Assert.Equal(FullHistory, await HistoryAsync(connectionString));
        await AssertCanonicalShapeAsync(connectionString);
        Assert.Equal(
            0L,
            await ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM pg_constraint WHERE conname='locations_recipient_contact_downgrade_guard'"));
    }

    /// <summary>
    /// Rolls the step back exactly as EF Core's migrator does, as <c>paqueteria_migrator</c>: its Down operations through
    /// the provider's SQL generator and the removal of its history row, in one transaction. The pre-EF-format GEO-001
    /// adoption identifier cannot be named as a migrator target (the IncidentsLaneMigrations precedent).
    /// </summary>
    private static async Task RollBackToAdoptionAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<LocationsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.UseNetTopologySuite();
                postgres.MigrationsAssembly(typeof(LocationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_locations", "platform");
            })
            .Options;
        await using var context = new LocationsDbContext(options, new TenantDatabaseExecutionState());
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(
            assembly.Migrations[AddRecipientNoticeContact.MigrationId],
            context.Database.ProviderName!);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(migration.DownOperations);
        var history = context.GetService<IHistoryRepository>().GetDeleteScript(AddRecipientNoticeContact.MigrationId);

        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var sql in commands.Select(command => command.CommandText).Append(history))
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task AssertBaselineAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await new DatabaseBaselineAssertions().AssertAsync(connection, transaction);
        await transaction.RollbackAsync();
    }

    private static Task InsertLocationAsync(string connectionString, Guid location, Guid org, Guid city) =>
        ExecuteAsync(connectionString,
            """
            INSERT INTO locations.locations(id,owner_org_id,city_id,point,address_ciphertext,address_summary,pii_key_version)
            VALUES (@id,@org,@city,ST_SetSRID(ST_MakePoint(-107.4,24.8),4326),'\x01','Synthetic quote destination','geo001-mock-v1');
            """,
            new("id", location), new("org", org), new("city", city));

    private static Task<long> ColumnCountAsync(string connectionString) => ScalarAsync<long>(connectionString,
        """
        SELECT count(*) FROM information_schema.columns
        WHERE table_schema='locations' AND table_name='locations'
          AND column_name IN ('email_ciphertext','notice_phone_ciphertext','notice_email_ciphertext','notice_key_version')
        """);

    private static Task<long> CheckCountAsync(string connectionString) => ScalarAsync<long>(connectionString,
        """
        SELECT count(*) FROM pg_constraint
        WHERE conrelid='locations.locations'::regclass AND conname LIKE 'locations_notice_%'
        """);

    private static Task<string[]> HistoryAsync(string connectionString) =>
        ListAsync(connectionString, "SELECT \"MigrationId\" FROM platform.__ef_migrations_history_locations ORDER BY 1");

    private static async Task<string> LaneStatusAsync(ModuleMigrationCoordinator coordinator, string connectionString) =>
        (await coordinator.PlanAsync(connectionString, CancellationToken.None))
            .Single(state => state.Module == Module)
            .Status;

    private static async Task ExecuteAsMigratorAsync(string connectionString, string sql)
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

    private static async Task ExecuteAsync(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string[]> ListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }
}
