using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Database.Evolution;
using Paqueteria.Infrastructure.Database.Evolution.Migrations;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// The owner decisions of 2026-09-27 that live in PostgreSQL, on a real PostgreSQL 18/PostGIS installation:
/// IDENTITY-ORG-ACTIVE-REQUIRED, AI05-TIMELINE-ORDER and AI06-PILOT-INDEXES, plus the platform evolution
/// lane that carries them to installations whose baseline predates them, up and down.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed partial class PilotContractDeltasPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Module = "PlatformEvolution";
    private const string HistoryTable = "platform.__ef_migrations_history_platform_evolution";

    [PostgreSqlContractFact]
    public async Task A_suspended_or_closed_organization_never_resolves_an_identity_context()
    {
        var user = Guid.NewGuid();
        var active = Guid.NewGuid();
        var suspended = Guid.NewGuid();
        var closed = Guid.NewGuid();
        var onlySuspendedUser = Guid.NewGuid();
        var subject = $"oidc|pilot-org-active|{Guid.NewGuid():N}";
        var onlySuspendedSubject = $"oidc|pilot-org-suspended|{Guid.NewGuid():N}";
        await AdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type,status) VALUES
              (@active,'Synthetic active','Active','BUSINESS','ACTIVE'),
              (@suspended,'Synthetic suspended','Suspended','ALLY','SUSPENDED'),
              (@closed,'Synthetic closed','Closed','BUSINESS','CLOSED');
            INSERT INTO identity.users(id,identity_subject) VALUES (@user,@subject),(@only_user,@only_subject);
            INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default) VALUES
              (gen_random_uuid(),@user,@suspended,'ALLY_ADMIN','ACTIVE',true),
              (gen_random_uuid(),@user,@closed,'VIEWER','ACTIVE',false),
              (gen_random_uuid(),@user,@active,'FINANCE','ACTIVE',false),
              (gen_random_uuid(),@only_user,@suspended,'DISPATCHER','ACTIVE',true);
            """,
            P("active", active), P("suspended", suspended), P("closed", closed), P("user", user),
            P("subject", subject), P("only_user", onlySuspendedUser), P("only_subject", onlySuspendedSubject));

        try
        {
            using (var resolved = await ResolveAsAppAsync(subject))
            {
                var memberships = resolved!.RootElement.GetProperty("memberships").EnumerateArray().ToArray();
                var membership = Assert.Single(memberships);
                Assert.Equal(active, membership.GetProperty("organization_id").GetGuid());
                Assert.Equal("FINANCE", membership.GetProperty("role").GetString());
            }

            // A user whose only organization is suspended is still a known identity with no tenant to select.
            using (var resolved = await ResolveAsAppAsync(onlySuspendedSubject))
            {
                Assert.Equal(onlySuspendedUser, resolved!.RootElement.GetProperty("user_id").GetGuid());
                Assert.Empty(resolved.RootElement.GetProperty("memberships").EnumerateArray());
            }

            // Reactivation is read at resolution time; nothing is cached in the function.
            await AdminAsync(
                "UPDATE organizations.organizations SET status='ACTIVE' WHERE id=@suspended",
                P("suspended", suspended));
            using (var resolved = await ResolveAsAppAsync(onlySuspendedSubject))
            {
                Assert.Equal(
                    suspended,
                    Assert.Single(resolved!.RootElement.GetProperty("memberships").EnumerateArray())
                        .GetProperty("organization_id").GetGuid());
            }
        }
        finally
        {
            await AdminAsync(
                """
                DELETE FROM organizations.organization_memberships WHERE user_id IN (@user,@only_user);
                DELETE FROM identity.users WHERE id IN (@user,@only_user);
                DELETE FROM organizations.organizations WHERE id IN (@active,@suspended,@closed);
                """,
                P("user", user), P("only_user", onlySuspendedUser), P("active", active),
                P("suspended", suspended), P("closed", closed));
        }
    }

    [PostgreSqlContractFact]
    public async Task Bootstrap_reads_exactly_the_organization_status_and_the_event_version_it_needs()
    {
        var granted = await StringsAsync(
            """
            SELECT table_schema || '.' || table_name || '.' || column_name
            FROM information_schema.column_privileges
            WHERE grantee='paqueteria_bootstrap' AND privilege_type='SELECT'
              AND table_schema || '.' || table_name IN ('organizations.organizations','orders.order_events')
            ORDER BY 1
            """);
        Assert.Equal(
        [
            "orders.order_events.aggregate_version",
            "orders.order_events.occurred_at",
            "orders.order_events.order_id",
            "orders.order_events.public_event_code",
            "organizations.organizations.id",
            "organizations.organizations.status",
        ], granted);

        foreach (var column in new[] { "legal_name", "display_name", "organization_type", "created_at" })
        {
            Assert.False(await ScalarAsync<bool>(
                $"SELECT has_column_privilege('paqueteria_bootstrap','organizations.organizations','{column}','SELECT')"));
        }

        Assert.False(await ScalarAsync<bool>(
            "SELECT has_column_privilege('paqueteria_bootstrap','orders.order_events','payload','SELECT')"));
    }

    [PostgreSqlContractFact]
    public async Task Public_timeline_breaks_equal_occurred_at_by_aggregate_version()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        // Unique per run and obviously synthetic, like the ARC-002 tracking fixtures.
        var token = $"timeline-order-token-{Guid.NewGuid():N}";

        // Inserted out of version order and with random ids, all at one instant: only the version orders them.
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO orders.public_tracking_tokens(id,order_id,owner_org_id,token_hash,expires_at) VALUES
              (gen_random_uuid(),@order,@org,extensions.digest(pg_catalog.convert_to(@token,'UTF8'),'sha256'),
               clock_timestamp()+interval '1 day');
            INSERT INTO orders.order_events(id,order_id,owner_org_id,aggregate_version,event_type,public_event_code,payload,occurred_at) VALUES
              ('ffffffff-0000-0000-0000-000000000004',@order,@org,4,'ORDER_STATUS_CHANGED','OUT_FOR_DELIVERY','{}',@at),
              ('00000000-0000-0000-0000-000000000002',@order,@org,2,'ORDER_STATUS_CHANGED','PICKED_UP','{}',@at),
              ('88888888-0000-0000-0000-000000000003',@order,@org,3,'ORDER_STATUS_CHANGED','IN_TRANSIT','{}',@at),
              ('44444444-0000-0000-0000-000000000001',@order,@org,1,'ORDER_CREATED','ORDER_CREATED','{}',@at);
            """,
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("token", token),
            SyntheticOrderScenario.P("at", new DateTimeOffset(2026, 9, 27, 15, 0, 0, TimeSpan.Zero)));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var projection = await ProjectionAsAppAsync(token);
            Assert.Equal(
                ["ORDER_CREATED", "PICKED_UP", "IN_TRANSIT", "OUT_FOR_DELIVERY"],
                projection!.RootElement.GetProperty("timeline").EnumerateArray()
                    .Select(item => item.GetProperty("code").GetString()));
        }

        var source = await ScalarAsync<string>(
            "SELECT prosrc FROM pg_proc WHERE oid='security.get_public_tracking_projection(text)'::regprocedure");
        Assert.Contains("ORDER BY e.occurred_at, e.aggregate_version", source, StringComparison.Ordinal);
    }

    [PostgreSqlContractFact]
    public async Task Business_outbox_purge_uses_both_partial_indexes() =>
        await AssertPurgePlanAsync(
            "purge_outbox",
            "platform.outbox_events",
            "outbox_purge_processed_idx",
            "outbox_purge_dead_idx",
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,payload,priority,status,attempts,
              available_at,created_at,processed_at)
            SELECT gen_random_uuid(),@org,'{}','pilot.plan','Plan',gen_random_uuid(),'{}',0,
                   CASE WHEN g % 200 = 0 THEN 'PROCESSED' WHEN g % 201 = 0 THEN 'DEAD' ELSE 'PENDING' END,
                   0,clock_timestamp(),clock_timestamp()-interval '30 days',
                   CASE WHEN g % 200 = 0 THEN clock_timestamp()-interval '10 days' END
            FROM generate_series(1,20000) g
            """);

    [PostgreSqlContractFact]
    public async Task Location_outbox_purge_uses_both_partial_indexes() =>
        await AssertPurgePlanAsync(
            "purge_location_outbox",
            "platform.location_outbox_events",
            "location_outbox_purge_processed_idx",
            "location_outbox_purge_dead_idx",
            """
            INSERT INTO platform.location_outbox_events(
              id,owner_org_id,driver_position_id,topic,payload,status,attempts,available_at,created_at,processed_at)
            SELECT gen_random_uuid(),@org,gen_random_uuid(),'pilot.plan','{}',
                   CASE WHEN g % 200 = 0 THEN 'PROCESSED' WHEN g % 201 = 0 THEN 'DEAD' ELSE 'PENDING' END,
                   0,clock_timestamp(),clock_timestamp()-interval '30 days',
                   CASE WHEN g % 200 = 0 THEN clock_timestamp()-interval '10 days' END
            FROM generate_series(1,20000) g
            """);

    [PostgreSqlContractFact]
    public void Verified_sources_carry_the_platform_evolution_lane_last()
    {
        var verified = ModuleMigrationCoordinator.VerifySources();
        var lane = Assert.Single(verified, state => state.Module == Module);

        Assert.Equal(HistoryTable, lane.HistoryTable);
        // TRK-002-AUTO-LINK: the lane's latest step bounds public tracking links to the order lifecycle.
        Assert.Equal(BoundTrackingLinksToOrderLifecycle.MigrationId, lane.MigrationId);
        Assert.Equal("VERIFIED", lane.Status);

        // It must run after the Orders lane, whose RTM-002 migration rewrites the tracking projection.
        Assert.Equal(Module, verified[^1].Module);
    }

    [PostgreSqlContractFact]
    public async Task Platform_evolution_lane_applies_rolls_back_and_reapplies_on_real_postgresql()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("pilotdeltas");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            Assert.Equal(
                DatabaseBaselineApplyStatus.Applied,
                (await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString)).Status);
            var coordinator = new ModuleMigrationCoordinator();
            Assert.Equal("PENDING", await LaneStatusAsync(coordinator, connectionString));

            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.All(
                await coordinator.AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            await AssertDeltasAsync(connectionString, present: true);
            await AssertBaselineAsync(connectionString);

            // The TRK-002-AUTO-LINK step after the pilot deltas never rolls back (its Down fails closed), so the
            // pilot deltas' own Down is exercised from an installation that predates that step.
            var blocked = await Assert.ThrowsAnyAsync<Exception>(
                () => MigrateLaneAsync(connectionString, ApplyPilotContractDeltas.MigrationId));
            Assert.Equal("TRK002_LIFECYCLE_DOWNGRADE_NOT_SUPPORTED", FindPostgresException(blocked).MessageText);
            Assert.Equal("APPLIED", await LaneStatusAsync(coordinator, connectionString));
            await ExecuteAsync(
                connectionString,
                $"""
                DELETE FROM {HistoryTable} WHERE "MigrationId"='{BoundTrackingLinksToOrderLifecycle.MigrationId}';
                """);

            // Down returns the installation to the pre-decision contract, exactly.
            await MigrateLaneAsync(connectionString, Migration.InitialDatabase);
            Assert.Equal("PENDING", await LaneStatusAsync(coordinator, connectionString));
            await AssertDeltasAsync(connectionString, present: false);

            // That pre-decision installation is what a populated deployment looks like: Up adopts it.
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.Equal("APPLIED", await LaneStatusAsync(coordinator, connectionString));
            await AssertDeltasAsync(connectionString, present: true);
            await AssertBaselineAsync(connectionString);
            Assert.Equal(
                "paqueteria_migrator",
                await ScalarAsync<string>(
                    connectionString,
                    """
                    SELECT pg_get_userbyid(c.relowner) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname='platform' AND c.relname='__ef_migrations_history_platform_evolution'
                    """));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Platform_evolution_lane_refuses_a_same_named_index_with_another_shape()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("pilotdrift");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            await ExecuteAsync(
                connectionString,
                """
                DROP INDEX orders.orders_updated_at_idx;
                CREATE INDEX orders_updated_at_idx ON orders.orders(created_at);
                """);

            var failure = await Assert.ThrowsAnyAsync<Exception>(
                () => new ModuleMigrationCoordinator().ApplyAsync(connectionString, CancellationToken.None));
            var postgres = FindPostgresException(failure);
            Assert.Contains("PILOT-DELTAS index set differs", postgres.MessageText, StringComparison.Ordinal);
            Assert.Equal(
                "PENDING",
                await LaneStatusAsync(new ModuleMigrationCoordinator(), connectionString));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    private async Task AssertPurgePlanAsync(
        string function,
        string table,
        string processedIndex,
        string deadIndex,
        string seedSql)
    {
        // The candidate statement is taken from the installed OPS-004 purge function itself, so the plan
        // proven here is the plan the function runs, with its parameters as generic plan parameters.
        var source = await ScalarAsync<string>(
            $"SELECT prosrc FROM pg_proc WHERE oid='security.{function}(timestamptz,timestamptz,integer,boolean)'::regprocedure");
        var candidates = CandidatesPattern().Match(source!);
        Assert.True(candidates.Success, $"{function} lost its candidate CTE.");
        var candidateSql = candidates.Groups["sql"].Value
            .Replace("p_processed_before", "$1", StringComparison.Ordinal)
            .Replace("p_dead_before", "$2", StringComparison.Ordinal)
            .Replace("p_batch_size", "$3", StringComparison.Ordinal);
        Assert.Contains(table, candidateSql, StringComparison.Ordinal);

        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var organization = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand(
            $"""
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
            VALUES (@org,'Plan','Plan','BUSINESS');
            {seedSql};
            ANALYZE {table};
            SET LOCAL plan_cache_mode = force_generic_plan;
            """,
            connection,
            transaction))
        {
            seed.Parameters.AddWithValue("org", organization);
            await seed.ExecuteNonQueryAsync();
        }

        // PREPARE is session state, not transactional: a unique name keeps a pooled session clean.
        var statement = $"pilot_purge_plan_{Guid.NewGuid():N}";
        await using (var prepare = new NpgsqlCommand(
            $"PREPARE {statement}(timestamptz,timestamptz,integer) AS {candidateSql}",
            connection,
            transaction))
        {
            await prepare.ExecuteNonQueryAsync();
        }

        var plan = new List<string>();
        await using (var explain = new NpgsqlCommand(
            $"EXPLAIN (COSTS OFF) EXECUTE {statement}(clock_timestamp()-interval '1 day',clock_timestamp()-interval '7 days',1000)",
            connection,
            transaction))
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(0));
            }
        }

        var text = string.Join('\n', plan);
        Assert.Contains($"Index Scan on {processedIndex}", text, StringComparison.Ordinal);
        Assert.Contains($"Index Scan on {deadIndex}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan", text, StringComparison.Ordinal);
        await transaction.RollbackAsync();
        await using var deallocate = new NpgsqlCommand($"DEALLOCATE {statement}", connection);
        await deallocate.ExecuteNonQueryAsync();
    }

    private static async Task AssertDeltasAsync(string connectionString, bool present)
    {
        var identity = await ScalarAsync<string>(
            connectionString,
            "SELECT prosrc FROM pg_proc WHERE oid='security.resolve_identity_context(text)'::regprocedure");
        var tracking = await ScalarAsync<string>(
            connectionString,
            "SELECT prosrc FROM pg_proc WHERE oid='security.get_public_tracking_projection(text)'::regprocedure");
        Assert.Equal(present, identity!.Contains("org.status='ACTIVE'", StringComparison.Ordinal));
        Assert.Equal(present, tracking!.Contains("ORDER BY e.occurred_at, e.aggregate_version", StringComparison.Ordinal));
        Assert.Equal(
            ["paqueteria_bootstrap", "paqueteria_bootstrap"],
            await StringsAsync(
                connectionString,
                """
                SELECT pg_get_userbyid(proowner) FROM pg_proc
                WHERE oid IN ('security.resolve_identity_context(text)'::regprocedure,
                              'security.get_public_tracking_projection(text)'::regprocedure)
                """));
        Assert.Equal(present, await ScalarAsync<bool>(
            connectionString,
            "SELECT has_column_privilege('paqueteria_bootstrap','organizations.organizations','status','SELECT')"));
        Assert.Equal(present, await ScalarAsync<bool>(
            connectionString,
            "SELECT has_column_privilege('paqueteria_bootstrap','orders.order_events','aggregate_version','SELECT')"));
        var indexes = await StringsAsync(
            connectionString,
            "SELECT pg_get_indexdef(to_regclass(name)) FROM unnest(@names) AS listed(name) WHERE to_regclass(name) IS NOT NULL",
            new NpgsqlParameter<string[]>("names", ApplyPilotContractDeltas.Indexes.Select(index => index.Name).ToArray()));
        Assert.Equal(
            present ? ApplyPilotContractDeltas.Indexes.Select(index => index.Definition).Order(StringComparer.Ordinal).ToArray() : [],
            indexes.Order(StringComparer.Ordinal).ToArray());
    }

    private static async Task AssertBaselineAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await new DatabaseBaselineAssertions().AssertAsync(connection);
    }

    private static async Task MigrateLaneAsync(string connectionString, string target)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var context = new PlatformEvolutionDbContext(
            ModuleMigrationCoordinator.PlatformEvolutionOptions(connection));
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task<string> LaneStatusAsync(ModuleMigrationCoordinator coordinator, string connectionString) =>
        (await coordinator.PlanAsync(connectionString, CancellationToken.None))
            .Single(state => state.Module == Module).Status;

    private async Task<JsonDocument?> ResolveAsAppAsync(string subject) =>
        await AsAppAsync("SELECT security.resolve_identity_context(@value)::text", subject);

    private async Task<JsonDocument?> ProjectionAsAppAsync(string token) =>
        await AsAppAsync("SELECT security.get_public_tracking_projection(@value)::text", token);

    private async Task<JsonDocument?> AsAppAsync(string sql, string value)
    {
        await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_app", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("value", value);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : JsonDocument.Parse((string)result);
    }

    private async Task AdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private async Task<List<string>> StringsAsync(string sql)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static async Task<List<string>> StringsAsync(
        string connectionString,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static PostgresException FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        throw new Xunit.Sdk.XunitException($"No PostgresException in {exception}");
    }

    private static NpgsqlParameter P(string name, object value) => new(name, value);

    [GeneratedRegex(
        @"WITH candidates AS \(\s*(?<sql>SELECT id FROM platform\.[a-z_]+\s+WHERE[\s\S]*?LIMIT LEAST\(GREATEST\(p_batch_size,1\),\d+\))\s*\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CandidatesPattern();
}
