using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Organizations.Infrastructure.Persistence;
using Organizations.Infrastructure.Persistence.Migrations;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// POLICY-VERSIONS-PER-ORG-2026-10-02: the Organizations lane adopts
/// <c>organizations.organizations.assignment_policy_version</c> and <c>driver_eligibility_policy_version</c>
/// on a fresh AI-06 baseline, creates them on an installation that predates the decision (every existing
/// organization starts at <c>piloto-2026-10-v1</c> without any row being rewritten), and rolls back and
/// forward as the migrator on a real PostgreSQL, refusing to discard a raised version.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class OrganizationPolicyVersionsMigrationPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Module = "Organizations";
    private const string LegacyOrganizationId = "0d0d0d0d-0000-4000-8000-000000000001";
    private const string LaterOrganizationId = "0d0d0d0d-0000-4000-8000-000000000002";

    private static readonly string[] FullHistory =
    [
        AdoptCanonicalOrganizationsBaseline.MigrationId,
        AddSelfServiceRegistration.MigrationId,
        AddPendingMemberships.MigrationId,
        VersionDispatchPoliciesPerOrganization.MigrationId,
    ];

    [PostgreSqlContractFact]
    public async Task The_shared_fixture_carries_the_policy_version_columns_as_the_latest_organizations_migration()
    {
        var verified = Assert.Single(ModuleMigrationCoordinator.VerifySources(), state => state.Module == Module);
        Assert.Equal(VersionDispatchPoliciesPerOrganization.MigrationId, verified.MigrationId);

        var applied = Assert.Single(
            await new ModuleMigrationCoordinator().AssertAsync(fixture.DeploymentConnectionString, CancellationToken.None),
            state => state.Module == Module);
        Assert.Equal("APPLIED", applied.Status);
        await AssertCanonicalShapeAsync(fixture.DeploymentConnectionString);
    }

    [PostgreSqlContractFact]
    public async Task The_policy_version_lane_starts_every_organization_at_the_owner_version_and_refuses_to_discard_versions()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("orgpolicyver");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            var coordinator = new ModuleMigrationCoordinator();
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);

            // Fresh installation: AI-06 already carries both columns, the lane adopts them.
            Assert.Equal(FullHistory, await HistoryAsync(connectionString));
            await AssertCanonicalShapeAsync(connectionString);

            // Down while every organization is at the starting version: columns and checks gone, lane pending.
            await MigrateOrganizationsAsync(connectionString, AddPendingMemberships.MigrationId);
            Assert.Equal(0L, await ColumnCountAsync(connectionString));
            Assert.Equal(0L, await ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM pg_constraint WHERE conrelid='organizations.organizations'::regclass AND conname LIKE '%policy_version%'"));
            Assert.Equal(FullHistory[..3], await HistoryAsync(connectionString));
            Assert.Equal("PENDING", await LaneStatusAsync(coordinator, connectionString));

            // An installation that predates the decision already holds an organization.
            await ExecuteAsync(connectionString, $"""
                INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
                  VALUES ('{LegacyOrganizationId}','Policy legacy','Policy legacy','BUSINESS');
                """);

            // Up through the canonical migrator: the existing organization starts at the owner's version.
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.All(
                await coordinator.AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            await AssertCanonicalShapeAsync(connectionString);
            Assert.Equal(("piloto-2026-10-v1", "piloto-2026-10-v1"), await VersionsAsync(connectionString, LegacyOrganizationId));

            // An organization created later, without naming the columns, starts there too.
            await ExecuteAsync(connectionString, $"""
                INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
                  VALUES ('{LaterOrganizationId}','Policy later','Policy later','ALLY');
                """);
            Assert.Equal(("piloto-2026-10-v1", "piloto-2026-10-v1"), await VersionsAsync(connectionString, LaterOrganizationId));

            // The format and NOT NULL are enforced on both columns.
            foreach (var column in new[] { VersionDispatchPoliciesPerOrganization.AssignmentColumn, VersionDispatchPoliciesPerOrganization.EligibilityColumn })
            {
                var unsafeLabel = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
                    $"UPDATE organizations.organizations SET {column}='not safe' WHERE id='{LegacyOrganizationId}'"));
                Assert.Equal(PostgresErrorCodes.CheckViolation, unsafeLabel.SqlState);
                var tooLong = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
                    $"UPDATE organizations.organizations SET {column}='{new string('a', 65)}' WHERE id='{LegacyOrganizationId}'"));
                Assert.Equal(PostgresErrorCodes.CheckViolation, tooLong.SqlState);
                var missing = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
                    $"UPDATE organizations.organizations SET {column}=NULL WHERE id='{LegacyOrganizationId}'"));
                Assert.Equal(PostgresErrorCodes.NotNullViolation, missing.SqlState);
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await new DatabaseBaselineAssertions().AssertAsync(connection, transaction);
                await transaction.RollbackAsync();
            }

            // A raised version is the organization's policy history: the rollback refuses and changes nothing.
            await ExecuteAsync(connectionString,
                $"UPDATE organizations.organizations SET driver_eligibility_policy_version='legacy-elig-v2' WHERE id='{LegacyOrganizationId}'");
            var blocked = await Assert.ThrowsAsync<PostgresException>(
                () => MigrateOrganizationsAsync(connectionString, AddPendingMemberships.MigrationId));
            Assert.Equal(VersionDispatchPoliciesPerOrganization.DowngradeBlocked, blocked.MessageText);
            Assert.Equal(2L, await ColumnCountAsync(connectionString));
            Assert.Equal(FullHistory, await HistoryAsync(connectionString));
            Assert.Equal(("piloto-2026-10-v1", "legacy-elig-v2"), await VersionsAsync(connectionString, LegacyOrganizationId));

            await ExecuteAsync(connectionString,
                $"UPDATE organizations.organizations SET assignment_policy_version='legacy-asg-v2', driver_eligibility_policy_version='piloto-2026-10-v1' WHERE id='{LegacyOrganizationId}'");
            blocked = await Assert.ThrowsAsync<PostgresException>(
                () => MigrateOrganizationsAsync(connectionString, AddPendingMemberships.MigrationId));
            Assert.Equal(VersionDispatchPoliciesPerOrganization.DowngradeBlocked, blocked.MessageText);
            Assert.Equal(2L, await ColumnCountAsync(connectionString));

            // The lane SQL is idempotent on the canonical shape and keeps every stored version.
            await ExecuteAsMigratorAsync(connectionString, VersionDispatchPoliciesPerOrganization.UpSql);
            await ExecuteAsMigratorAsync(connectionString, VersionDispatchPoliciesPerOrganization.UpSql);
            await AssertCanonicalShapeAsync(connectionString);
            Assert.Equal(("legacy-asg-v2", "piloto-2026-10-v1"), await VersionsAsync(connectionString, LegacyOrganizationId));

            // Back at the starting version the rollback and a fresh reapply both succeed.
            await ExecuteAsync(connectionString,
                $"UPDATE organizations.organizations SET assignment_policy_version='piloto-2026-10-v1' WHERE id='{LegacyOrganizationId}'");
            await MigrateOrganizationsAsync(connectionString, AddPendingMemberships.MigrationId);
            Assert.Equal(0L, await ColumnCountAsync(connectionString));
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.Equal(FullHistory, await HistoryAsync(connectionString));
            await AssertCanonicalShapeAsync(connectionString);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task The_policy_version_lane_refuses_a_drifted_column_shape()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("orgpolicydrift");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            await new ModuleMigrationCoordinator().ApplyAsync(connectionString, CancellationToken.None);
            await MigrateOrganizationsAsync(connectionString, AddPendingMemberships.MigrationId);

            // A column with another default would silently start organizations elsewhere.
            await ExecuteAsync(connectionString,
                "ALTER TABLE organizations.organizations ADD COLUMN assignment_policy_version text NOT NULL DEFAULT 'other-v1'");
            var wrongDefault = await Assert.ThrowsAsync<PostgresException>(
                () => ExecuteAsMigratorAsync(connectionString, VersionDispatchPoliciesPerOrganization.UpSql));
            Assert.Contains("assignment_policy_version does not match", wrongDefault.MessageText, StringComparison.Ordinal);

            // A check with another pattern is refused as well.
            await ExecuteAsync(connectionString, """
                ALTER TABLE organizations.organizations ALTER COLUMN assignment_policy_version SET DEFAULT 'piloto-2026-10-v1';
                ALTER TABLE organizations.organizations ADD CONSTRAINT organizations_assignment_policy_version_check
                  CHECK (assignment_policy_version ~ '^.*$');
                """);
            var wrongCheck = await Assert.ThrowsAsync<PostgresException>(
                () => ExecuteAsMigratorAsync(connectionString, VersionDispatchPoliciesPerOrganization.UpSql));
            Assert.Contains("organizations_assignment_policy_version_check does not match", wrongCheck.MessageText, StringComparison.Ordinal);
            Assert.Equal(1L, await ColumnCountAsync(connectionString));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    private static async Task AssertCanonicalShapeAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT c.column_name,c.data_type,c.is_nullable,c.column_default,
                   (SELECT pg_get_constraintdef(k.oid) FROM pg_constraint k
                    WHERE k.conrelid='organizations.organizations'::regclass
                      AND k.conname='organizations_' || c.column_name || '_check')
            FROM information_schema.columns c
            WHERE c.table_schema='organizations' AND c.table_name='organizations'
              AND c.column_name IN ('assignment_policy_version','driver_eligibility_policy_version')
            ORDER BY c.column_name;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string Column, string Type, string Nullable, string Default, string Check)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        Assert.Equal(
            [
                (VersionDispatchPoliciesPerOrganization.AssignmentColumn, "text", "NO",
                    VersionDispatchPoliciesPerOrganization.ColumnDefault,
                    VersionDispatchPoliciesPerOrganization.AssignmentFormatConstraintDefinition),
                (VersionDispatchPoliciesPerOrganization.EligibilityColumn, "text", "NO",
                    VersionDispatchPoliciesPerOrganization.ColumnDefault,
                    VersionDispatchPoliciesPerOrganization.EligibilityFormatConstraintDefinition),
            ],
            rows);
    }

    private static Task<long> ColumnCountAsync(string connectionString) =>
        ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema='organizations' AND table_name='organizations'
              AND column_name IN ('assignment_policy_version','driver_eligibility_policy_version')
            """);

    private static async Task<(string Assignment, string Eligibility)> VersionsAsync(string connectionString, string organizationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT assignment_policy_version,driver_eligibility_policy_version FROM organizations.organizations WHERE id='{organizationId}'",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<string[]> HistoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"MigrationId\" FROM platform.__ef_migrations_history_organizations ORDER BY \"MigrationId\"",
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

    private static async Task MigrateOrganizationsAsync(string connectionString, string? target)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator;", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(OrganizationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_organizations", "platform");
            })
            .Options;
        await using var context = new OrganizationsDbContext(options, new TenantDatabaseExecutionState());
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
