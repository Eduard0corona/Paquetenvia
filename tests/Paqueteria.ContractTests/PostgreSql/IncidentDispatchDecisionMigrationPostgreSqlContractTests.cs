using Incidents.Infrastructure.Persistence.Migrations;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11: the Incidents lane lets <c>incidents.incidents.next_action</c> be
/// NULL (a driver report whose next step dispatch decides). Against a real PostgreSQL, as the migrator: the canonical
/// migrator applies it after the evidence index, both INC-001 checks keep guarding a recorded value, the rollback
/// restores NOT NULL while no incident waits for dispatch, and it refuses once one does.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class IncidentDispatchDecisionMigrationPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task The_shared_fixture_carries_the_dispatch_decision_as_the_latest_incidents_step()
    {
        var verified = Assert.Single(ModuleMigrationCoordinator.VerifySources(), state => state.Module == "Incidents");
        Assert.Equal(AllowDispatchDecidedNextAction.MigrationId, verified.MigrationId);
        Assert.Equal(
            [
                AdoptCanonicalIncidentsBaseline.MigrationId,
                IndexIncidentEvidenceByOrderProof.MigrationId,
                AllowDispatchDecidedNextAction.MigrationId,
            ],
            await IncidentsLaneMigrations.HistoryAsync(fixture.DeploymentConnectionString));

        Assert.Equal("YES", await NextActionNullableAsync(fixture.DeploymentConnectionString));
        await AssertChecksGuardARecordedValueAsync(fixture.DeploymentConnectionString);
    }

    [PostgreSqlContractFact]
    public async Task The_step_rolls_back_only_while_no_incident_waits_for_dispatch()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("incdispatchdecides");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            Assert.Equal(
                DatabaseBaselineApplyStatus.Applied,
                (await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString)).Status);
            var coordinator = new ModuleMigrationCoordinator();
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.Equal("APPLIED", await IncidentsLaneMigrations.LaneStatusAsync(connectionString));
            Assert.Equal("YES", await NextActionNullableAsync(connectionString));

            // Down without any NULL next action: NOT NULL is back and only this step leaves the history.
            await IncidentsLaneMigrations.RollBackAsMigratorAsync(
                connectionString,
                AllowDispatchDecidedNextAction.MigrationId);
            Assert.Equal("NO", await NextActionNullableAsync(connectionString));
            Assert.Equal(
                [AdoptCanonicalIncidentsBaseline.MigrationId, IndexIncidentEvidenceByOrderProof.MigrationId],
                await IncidentsLaneMigrations.HistoryAsync(connectionString));
            Assert.Equal("PENDING", await IncidentsLaneMigrations.LaneStatusAsync(connectionString));
            await AssertChecksGuardARecordedValueAsync(connectionString);

            // Up again through the canonical migrator, and the step is idempotent when its SQL runs again.
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.All(
                await coordinator.AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            Assert.Equal("YES", await NextActionNullableAsync(connectionString));
            await IncidentsLaneMigrations.MigrateSqlAsMigratorAsync(connectionString, AllowDispatchDecidedNextAction.UpSql);
            Assert.Equal("YES", await NextActionNullableAsync(connectionString));

            // Once an incident waits for dispatch, the rollback refuses instead of inventing a decision. The row is
            // invisible to the NOBYPASSRLS migrator; the SET NOT NULL probe sees it anyway.
            var waiting = await InsertDispatchDecidedIncidentAsync(connectionString);
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                IncidentsLaneMigrations.RollBackAsMigratorAsync(
                    connectionString,
                    AllowDispatchDecidedNextAction.MigrationId));
            Assert.Equal(AllowDispatchDecidedNextAction.DowngradeBlocked, refused.MessageText);
            Assert.Equal("P0001", refused.SqlState);

            Assert.Equal("YES", await NextActionNullableAsync(connectionString));
            Assert.Equal(
                [
                    AdoptCanonicalIncidentsBaseline.MigrationId,
                    IndexIncidentEvidenceByOrderProof.MigrationId,
                    AllowDispatchDecidedNextAction.MigrationId,
                ],
                await IncidentsLaneMigrations.HistoryAsync(connectionString));
            Assert.True(await ScalarAsync<bool>(
                connectionString,
                $"SELECT next_action IS NULL FROM incidents.incidents WHERE id='{waiting:D}'"));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    private static Task<string> NextActionNullableAsync(string connectionString) =>
        ScalarAsync<string>(connectionString, """
            SELECT is_nullable FROM information_schema.columns
            WHERE table_schema='incidents' AND table_name='incidents' AND column_name='next_action'
            """);

    /// <summary>Both INC-001 checks still refuse a recorded value outside the state machine and RETURNING without custody.</summary>
    private static async Task AssertChecksGuardARecordedValueAsync(string connectionString)
    {
        var definitions = await ScalarAsync<string>(connectionString, """
            SELECT string_agg(conname || '=' || pg_get_constraintdef(oid), ' | ' ORDER BY conname)
            FROM pg_constraint
            WHERE conrelid='incidents.incidents'::regclass AND contype='c'
              AND conname IN ('incidents_next_action_check','incidents_returning_requires_custody_check')
            """);
        Assert.Contains("incidents_next_action_check=CHECK ((next_action = ANY (ARRAY['RESCHEDULED'::text, 'RETURNING'::text])))", definitions, StringComparison.Ordinal);
        Assert.Contains("incidents_returning_requires_custody_check=CHECK (((next_action <> 'RETURNING'::text) OR custody_acquired))", definitions, StringComparison.Ordinal);
    }

    /// <summary>
    /// A driver report as the incident service writes it: next_action NULL. Only the column's nullability matters to
    /// the rollback, so the seed skips the triggers of its own transaction (the deferred evidence rule and the
    /// references to an order this isolated database does not hold); the check constraints still apply.
    /// </summary>
    private static async Task<Guid> InsertDispatchDecidedIncidentAsync(string connectionString)
    {
        var incidentId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var replica = new NpgsqlCommand("SET LOCAL session_replication_role=replica", connection, transaction))
        {
            await replica.ExecuteNonQueryAsync();
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO incidents.incidents(
              id,order_id,owner_org_id,incident_type,severity,status,custody_acquired,
              created_by,created_at,reason_code,next_action,occurred_at,sla_due_at)
            VALUES (
              @incident,gen_random_uuid(),gen_random_uuid(),'FAILED_DELIVERY_ATTEMPT','HIGH','OPEN',true,
              gen_random_uuid(),clock_timestamp(),'SECURITY_RISK',NULL,
              clock_timestamp(),clock_timestamp()+interval '8 hours')
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("incident", incidentId);
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return incidentId;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
