using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;
using Pricing.Infrastructure.Persistence;
using Pricing.Infrastructure.Persistence.Migrations;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// PRC-POLICY-VERSION-PER-ORG: the Pricing lane adopts <c>pricing.tariff_rules.policy_version</c> on a
/// fresh AI-06 baseline, creates it on an installation that predates the decision without rewriting
/// any row, and rolls back and forward as the migrator on a real PostgreSQL.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class PricingPolicyVersionMigrationPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Module = "Pricing";
    private const string OrganizationId = "0c0c0c0c-0000-4000-8000-000000000001";
    private const string CityId = "0c0c0c0c-0000-4000-8000-000000000002";
    private const string LegacyRuleId = "0c0c0c0c-0000-4000-8000-000000000003";

    [PostgreSqlContractFact]
    public async Task The_shared_fixture_carries_the_policy_version_migrations_as_the_latest_pricing_migrations()
    {
        var verified = Assert.Single(ModuleMigrationCoordinator.VerifySources(), state => state.Module == Module);
        Assert.Equal(StoreTariffPolicyVersionInMasterDataLoader.MigrationId, verified.MigrationId);

        var applied = Assert.Single(
            await new ModuleMigrationCoordinator().AssertAsync(fixture.DeploymentConnectionString, CancellationToken.None),
            state => state.Module == Module);
        Assert.Equal("APPLIED", applied.Status);
        Assert.Equal(("NO", 0L), await ShapeAsync(fixture.DeploymentConnectionString));
    }

    [PostgreSqlContractFact]
    public async Task The_policy_version_lane_rolls_back_upgrades_legacy_rows_safely_and_refuses_to_discard_versions()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("prcpolicyver");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            var coordinator = new ModuleMigrationCoordinator();
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);

            // Fresh installation: AI-06 already carries the column, the lane adopts it.
            Assert.Equal(
                [
                    AdoptCanonicalPricingBaseline.MigrationId,
                    AddMasterDataLoader.MigrationId,
                    VersionPricingPolicyPerOrganization.MigrationId,
                    StoreTariffPolicyVersionInMasterDataLoader.MigrationId,
                ],
                await HistoryAsync(connectionString));
            Assert.Equal(("NO", 0L), await ShapeAsync(connectionString));

            // Down on an installation without versioned rules: column and checks gone, lane pending.
            await MigratePricingAsync(connectionString, Migration.InitialDatabase);
            Assert.False(await ColumnExistsAsync(connectionString));
            Assert.Equal(0L, await ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM pg_constraint WHERE conrelid='pricing.tariff_rules'::regclass AND conname LIKE 'tariff_rules_policy_version%'"));
            Assert.Empty(await HistoryAsync(connectionString));
            Assert.Equal("PENDING", await LaneStatusAsync(coordinator, connectionString));

            // An installation that predates the decision already holds a tariff rule.
            await ExecuteAsync(connectionString, $"""
                INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
                  VALUES ('{OrganizationId}','PRC policy legacy','PRC policy legacy','BUSINESS');
                INSERT INTO locations.cities(id,country_code,state_code,name,timezone,status)
                  VALUES ('{CityId}','MX','SIN','PRC policy legacy city','America/Mazatlan','ACTIVE');
                INSERT INTO pricing.tariff_rules(
                  id,owner_org_id,city_id,pricing_tier,service_type,amount_cents,tax_mode,active_from,status)
                  VALUES ('{LegacyRuleId}','{OrganizationId}','{CityId}','OCCASIONAL','SAME_DAY',12000,'EXEMPT',now(),'ACTIVE');
                """);

            // Up through the canonical migrator: no row is rewritten; new rows must carry a version.
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.All(
                await coordinator.AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            Assert.Equal(("YES", 1L), await ShapeAsync(connectionString));
            Assert.Equal(VersionPricingPolicyPerOrganization.RequiredConstraintDefinition, await ScalarAsync<string>(connectionString,
                $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname='{VersionPricingPolicyPerOrganization.RequiredConstraint}'"));
            Assert.True(await ScalarAsync<bool>(connectionString,
                $"SELECT policy_version IS NULL FROM pricing.tariff_rules WHERE id='{LegacyRuleId}'"));

            var missing = await Assert.ThrowsAsync<PostgresException>(() => InsertRuleAsync(connectionString, null));
            Assert.Equal(PostgresErrorCodes.CheckViolation, missing.SqlState);
            var unsafeLabel = await Assert.ThrowsAsync<PostgresException>(() => InsertRuleAsync(connectionString, "not safe"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, unsafeLabel.SqlState);
            await InsertRuleAsync(connectionString, "LEGACY-ORG-v2");

            // Touching a legacy rule without versioning it is refused; versioning it is allowed.
            var untouched = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
                $"UPDATE pricing.tariff_rules SET status='INACTIVE' WHERE id='{LegacyRuleId}'"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, untouched.SqlState);
            await ExecuteAsync(connectionString,
                $"UPDATE pricing.tariff_rules SET policy_version='LEGACY-ORG-v1' WHERE id='{LegacyRuleId}'");

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await new DatabaseBaselineAssertions().AssertAsync(connection, transaction);
                await transaction.RollbackAsync();
            }

            // The loader step rolls back on its own (EF runs each migration in its own transaction) and gives
            // back the published loader function; the column step then refuses and changes nothing, because
            // rules carry versions.
            await MigratePricingAsync(connectionString, VersionPricingPolicyPerOrganization.MigrationId);
            Assert.False(await ScalarAsync<bool>(connectionString,
                $"SELECT position('{StoreTariffPolicyVersionInMasterDataLoader.ImmutableVersionError}' IN prosrc) > 0 FROM pg_proc WHERE oid=to_regprocedure('{AddMasterDataLoader.FunctionSignature}')"));
            var blocked = await Assert.ThrowsAsync<PostgresException>(
                () => MigratePricingAsync(connectionString, AddMasterDataLoader.MigrationId));
            Assert.Equal(VersionPricingPolicyPerOrganization.DowngradeBlocked, blocked.MessageText);
            Assert.True(await ColumnExistsAsync(connectionString));
            Assert.Equal(
                [
                    AdoptCanonicalPricingBaseline.MigrationId,
                    AddMasterDataLoader.MigrationId,
                    VersionPricingPolicyPerOrganization.MigrationId,
                ],
                await HistoryAsync(connectionString));
            await MigratePricingAsync(connectionString, null);
            Assert.Equal(
                [
                    AdoptCanonicalPricingBaseline.MigrationId,
                    AddMasterDataLoader.MigrationId,
                    VersionPricingPolicyPerOrganization.MigrationId,
                    StoreTariffPolicyVersionInMasterDataLoader.MigrationId,
                ],
                await HistoryAsync(connectionString));

            // Second step: once every rule is versioned, the (idempotent) lane SQL makes the column
            // NOT NULL and drops the transitional guard, reaching the canonical AI-06 shape.
            await ExecuteAsMigratorAsync(connectionString, VersionPricingPolicyPerOrganization.UpSql);
            Assert.Equal(("NO", 0L), await ShapeAsync(connectionString));
            await ExecuteAsMigratorAsync(connectionString, VersionPricingPolicyPerOrganization.UpSql);
            Assert.Equal(("NO", 0L), await ShapeAsync(connectionString));

            // Without versioned rules the rollback and a fresh reapply both succeed.
            await ExecuteAsync(connectionString, $"DELETE FROM pricing.tariff_rules WHERE owner_org_id='{OrganizationId}'");
            await MigratePricingAsync(connectionString, Migration.InitialDatabase);
            Assert.False(await ColumnExistsAsync(connectionString));
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.Equal(("NO", 0L), await ShapeAsync(connectionString));
            Assert.Equal(VersionPricingPolicyPerOrganization.FormatConstraintDefinition, await ScalarAsync<string>(connectionString,
                $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname='{VersionPricingPolicyPerOrganization.FormatConstraint}'"));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    /// <summary>(is_nullable, number of transitional NOT VALID guards) of the policy_version column.</summary>
    private static async Task<(string Nullable, long RequiredGuards)> ShapeAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            SELECT c.is_nullable,
                   (SELECT count(*) FROM pg_constraint
                    WHERE conrelid='pricing.tariff_rules'::regclass
                      AND conname='{VersionPricingPolicyPerOrganization.RequiredConstraint}'),
                   (SELECT pg_get_constraintdef(oid) FROM pg_constraint
                    WHERE conrelid='pricing.tariff_rules'::regclass
                      AND conname='{VersionPricingPolicyPerOrganization.FormatConstraint}'),
                   c.data_type
            FROM information_schema.columns c
            WHERE c.table_schema='pricing' AND c.table_name='tariff_rules' AND c.column_name='policy_version';
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(VersionPricingPolicyPerOrganization.FormatConstraintDefinition, reader.GetString(2));
        Assert.Equal("text", reader.GetString(3));
        return (reader.GetString(0), reader.GetInt64(1));
    }

    private static Task<bool> ColumnExistsAsync(string connectionString) =>
        ScalarAsync<bool>(connectionString, """
            SELECT EXISTS (
              SELECT 1 FROM information_schema.columns
              WHERE table_schema='pricing' AND table_name='tariff_rules' AND column_name='policy_version')
            """);

    private static Task InsertRuleAsync(string connectionString, string? version) =>
        ExecuteAsync(connectionString, $"""
            INSERT INTO pricing.tariff_rules(
              id,owner_org_id,city_id,pricing_tier,service_type,amount_cents,tax_mode,active_from,status,policy_version)
              VALUES (gen_random_uuid(),'{OrganizationId}','{CityId}','OCCASIONAL','URGENT',1,'EXEMPT',now(),'INACTIVE',
                {(version is null ? "NULL" : $"'{version}'")});
            """);

    private static async Task<string[]> HistoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"MigrationId\" FROM platform.__ef_migrations_history_pricing ORDER BY \"MigrationId\"",
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

    private static async Task MigratePricingAsync(string connectionString, string? target)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator;", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<PricingDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(PricingDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_pricing", "platform");
            })
            .Options;
        await using var context = new PricingDbContext(options, new TenantDatabaseExecutionState());
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task ExecuteAsMigratorAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator;", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
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
