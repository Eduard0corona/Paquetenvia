using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Organizations.Infrastructure.Persistence;
using Paqueteria.Application.Tenancy;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Pooling;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// SCL-007 on real PostgreSQL: the shared pool of each role (built by <c>AddRuntimeDataSource</c>, one physical
/// connection, so every lease reuses it) carries no session state from one unit of work to the next. Tenant A then
/// tenant B on the same backend see only their own rows; between leases the role, <c>app.current_user_id</c>,
/// <c>app.current_org_ids</c>, prepared statements, temporary tables, session settings, advisory locks and LISTEN
/// channels are exactly those of a fresh connection; a failed, aborted or abandoned transaction leaves nothing behind.
/// Each case also runs with <c>No Reset On Close=true</c>, so nothing depends on the <c>DISCARD ALL</c> Npgsql sends
/// when a connection returns to its pool (a transaction pooler, GATE-014, would not send it).
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class Scl007SharedPoolSessionStateContractTests(PostgreSqlContractFixture fixture)
{
    private const string SnapshotSql = """
        SELECT pg_backend_pid(),
               current_user::text,
               session_user::text,
               current_setting('role'),
               COALESCE(current_setting('app.current_user_id', true), ''),
               COALESCE(current_setting('app.current_org_ids', true), ''),
               (SELECT count(*) FROM pg_catalog.pg_prepared_statements)::integer,
               (SELECT count(*) FROM pg_catalog.pg_class WHERE relnamespace = pg_catalog.pg_my_temp_schema())::integer,
               COALESCE((SELECT string_agg(name, ',' ORDER BY name) FROM pg_catalog.pg_settings WHERE source = 'session'), ''),
               (SELECT count(*) FROM pg_catalog.pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid())::integer,
               (SELECT count(*) FROM pg_catalog.pg_listening_channels())::integer
        """;

    [PostgreSqlContractFact]
    public async Task Tenant_a_then_tenant_b_on_one_physical_application_connection_leak_no_context_or_role()
    {
        foreach (var noResetOnClose in new[] { true, false })
        {
            var orgA = Guid.NewGuid();
            var orgB = Guid.NewGuid();
            var clientA = Guid.NewGuid();
            var clientB = Guid.NewGuid();
            await SeedTenantsAsync(orgA, orgB, clientA, clientB);
            try
            {
                await using var pools = ComposePools(noResetOnClose);
                var pool = pools.GetRuntimeDataSource(RuntimeDatabaseRole.Application);
                var fresh = await SnapshotAsync(pool);
                AssertFresh(fresh, PostgreSqlContractFixture.AppLogin);

                for (var round = 0; round < 3; round++)
                {
                    var userA = Guid.NewGuid();
                    var a = await ReadTenantAsync(pool, userA, [orgA]);
                    Assert.Equal(fresh.BackendId, a.BackendId);
                    Assert.Equal("paqueteria_app", a.CurrentUser);
                    Assert.Equal(userA.ToString("D"), a.UserSetting);
                    Assert.Equal($"{{{orgA:D}}}", a.OrganizationSetting);
                    Assert.Equal([clientA], a.Clients);
                    Assert.Equal(fresh, await SnapshotAsync(pool));

                    var userB = Guid.NewGuid();
                    var b = await ReadTenantAsync(pool, userB, [orgB]);
                    Assert.Equal(fresh.BackendId, b.BackendId);
                    Assert.Equal(userB.ToString("D"), b.UserSetting);
                    Assert.Equal($"{{{orgB:D}}}", b.OrganizationSetting);
                    Assert.Equal([clientB], b.Clients);
                    Assert.Equal(fresh, await SnapshotAsync(pool));
                }

                // The empty organization context of the next lease reads nothing, on the same backend.
                var empty = await ReadTenantAsync(pool, Guid.NewGuid(), []);
                Assert.Equal(fresh.BackendId, empty.BackendId);
                Assert.Equal("{}", empty.OrganizationSetting);
                Assert.Empty(empty.Clients);
                Assert.Equal(0, await CountClientsWithoutContextAsync(pool));
                Assert.Equal(fresh, await SnapshotAsync(pool));
            }
            finally
            {
                await DeleteTenantsAsync(orgA, orgB, clientA, clientB);
            }
        }
    }

    [PostgreSqlContractFact]
    public async Task Worker_units_of_work_on_the_shared_worker_pool_leak_no_context_or_role()
    {
        foreach (var noResetOnClose in new[] { true, false })
        {
            await using var pools = ComposePools(noResetOnClose);
            var pool = pools.GetRuntimeDataSource(RuntimeDatabaseRole.Worker);
            var fresh = await SnapshotAsync(pool);
            AssertFresh(fresh, PostgreSqlContractFixture.WorkerLogin);

            foreach (var organization in new[] { Guid.NewGuid(), Guid.NewGuid() })
            {
                var user = Guid.NewGuid();
                await using var scope = CreateScope(pool, worker: true);
                var inside = await scope.Worker.ExecuteAsync(
                    new TenantDatabaseExecutionContext(user, [organization]),
                    async (context, token) => await context.Database.SqlQueryRaw<string>(
                            "SELECT pg_backend_pid()::text || '|' || current_user || '|' || " +
                            "current_setting('app.current_user_id', true) || '|' || " +
                            "current_setting('app.current_org_ids', true) AS \"Value\"")
                        .SingleAsync(token));

                Assert.Equal($"{fresh.BackendId}|paqueteria_worker|{user:D}|{{{organization:D}}}", inside);
                Assert.Equal(fresh, await SnapshotAsync(pool));
            }
        }
    }

    [PostgreSqlContractFact]
    public async Task A_failed_aborted_or_abandoned_transaction_leaves_no_state_on_any_role_pool()
    {
        foreach (var noResetOnClose in new[] { true, false })
        {
            await using var pools = ComposePools(noResetOnClose);
            foreach (var (role, login) in new[]
            {
                (RuntimeDatabaseRole.Application, PostgreSqlContractFixture.AppLogin),
                (RuntimeDatabaseRole.Worker, PostgreSqlContractFixture.WorkerLogin),
                (RuntimeDatabaseRole.Realtime, PostgreSqlContractFixture.RealtimeLogin),
            })
            {
                var pool = pools.GetRuntimeDataSource(role);
                var fresh = await SnapshotAsync(pool);
                AssertFresh(fresh, login);
                var roleName = RuntimeDatabaseRoles.PostgreSqlRole(role);

                // A statement error aborts the transaction; the rollback returns a clean connection.
                await using (var connection = await pool.OpenConnectionAsync())
                await using (var transaction = await connection.BeginTransactionAsync())
                {
                    await EnterAsync(connection, transaction, roleName, Guid.NewGuid(), [Guid.NewGuid()]);
                    var division = await Assert.ThrowsAsync<PostgresException>(
                        () => ExecuteAsync(connection, transaction, "SELECT 1 / 0"));
                    Assert.Equal(PostgresErrorCodes.DivisionByZero, division.SqlState);
                    var aborted = await Assert.ThrowsAsync<PostgresException>(
                        () => ExecuteAsync(connection, transaction, "SELECT current_setting('app.current_org_ids', true)"));
                    Assert.Equal(PostgresErrorCodes.InFailedSqlTransaction, aborted.SqlState);
                    await transaction.RollbackAsync();
                }

                Assert.Equal(fresh, await SnapshotAsync(pool));

                // A transaction abandoned without commit or rollback is rolled back when its lease ends.
                await using (var connection = await pool.OpenConnectionAsync())
                {
                    var transaction = await connection.BeginTransactionAsync();
                    await EnterAsync(connection, transaction, roleName, Guid.NewGuid(), [Guid.NewGuid()]);
                    await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(7007)");
                }

                Assert.Equal(fresh, await SnapshotAsync(pool));

                // A transaction cancelled mid-statement leaves the pool usable and clean.
                using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    {
                        await using var connection = await pool.OpenConnectionAsync(cancellation.Token);
                        await using var transaction = await connection.BeginTransactionAsync(cancellation.Token);
                        await EnterAsync(connection, transaction, roleName, Guid.NewGuid(), [Guid.NewGuid()]);
                        await ExecuteAsync(connection, transaction, "SELECT pg_sleep(10)", cancellation.Token);
                    });
                }

                var afterCancellation = await SnapshotAsync(pool);
                Assert.Equal(fresh with { BackendId = afterCancellation.BackendId }, afterCancellation);
            }
        }
    }

    [PostgreSqlContractFact]
    public async Task A_failing_tenant_unit_of_work_rolls_back_and_the_next_tenant_reuses_a_clean_connection()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        await SeedTenantsAsync(orgA, orgB, clientA, clientB);
        try
        {
            await using var pools = ComposePools(noResetOnClose: true);
            var pool = pools.GetRuntimeDataSource(RuntimeDatabaseRole.Application);
            var fresh = await SnapshotAsync(pool);

            await using (var scope = CreateScope(pool, worker: false))
            {
                var failure = await Assert.ThrowsAsync<PostgresException>(() => scope.Application.ExecuteAsync(
                    new TenantDatabaseExecutionContext(Guid.NewGuid(), [orgA]),
                    async (context, token) =>
                    {
                        _ = await context.Database
                            .SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM clients.client_accounts")
                            .SingleAsync(token);
                        return await context.Database.SqlQueryRaw<int>("SELECT (1 / 0) AS \"Value\"").SingleAsync(token);
                    }));
                Assert.Equal(PostgresErrorCodes.DivisionByZero, failure.SqlState);
                Assert.False(scope.State.IsApplied);
            }

            Assert.Equal(fresh, await SnapshotAsync(pool));
            var next = await ReadTenantAsync(pool, Guid.NewGuid(), [orgB]);
            Assert.Equal(fresh.BackendId, next.BackendId);
            Assert.Equal([clientB], next.Clients);
            Assert.Equal(fresh, await SnapshotAsync(pool));
        }
        finally
        {
            await DeleteTenantsAsync(orgA, orgB, clientA, clientB);
        }
    }

    /// <summary>The three runtime pools exactly as a host composes them, each limited to one physical connection.</summary>
    private ServiceProvider ComposePools(bool noResetOnClose)
    {
        string Pooled(string connectionString) =>
            new NpgsqlConnectionStringBuilder(connectionString)
            {
                NoResetOnClose = noResetOnClose,
                ApplicationName = "Paqueteria.SCL007.ContractTests",
            }.ConnectionString;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Paqueteria"] = Pooled(fixture.AppConnectionString),
                ["ConnectionStrings:PaqueteriaWorker"] = Pooled(fixture.WorkerConnectionString),
                ["ConnectionStrings:PaqueteriaRealtime"] = Pooled(fixture.RealtimeConnectionString),
                ["DatabasePools:ApplicationMaximumPoolSize"] = "1",
                ["DatabasePools:WorkerMaximumPoolSize"] = "1",
                ["DatabasePools:RealtimeMaximumPoolSize"] = "1",
            })
            .Build();
        var services = new ServiceCollection();
        foreach (var role in RuntimeDatabaseRoles.All)
        {
            services.AddRuntimeDataSource(configuration, role);
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static Scope CreateScope(NpgsqlDataSource pool, bool worker)
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql(pool, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(new TenantTransactionGuardInterceptor(state), new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new OrganizationsDbContext(options, state);
        return new Scope(
            context,
            state,
            worker ? null : new TenantTransactionContext<OrganizationsDbContext>(context, state),
            worker ? new WorkerTenantTransactionContext<OrganizationsDbContext>(context, state) : null);
    }

    private static async Task<TenantRead> ReadTenantAsync(NpgsqlDataSource pool, Guid userId, Guid[] organizations)
    {
        await using var scope = CreateScope(pool, worker: false);
        return await scope.Application.ExecuteAsync(
            new TenantDatabaseExecutionContext(userId, organizations),
            async (context, token) =>
            {
                var snapshot = await context.Database.SqlQueryRaw<string>(
                        "SELECT pg_backend_pid()::text || '|' || current_user || '|' || " +
                        "current_setting('app.current_user_id', true) || '|' || " +
                        "current_setting('app.current_org_ids', true) AS \"Value\"")
                    .SingleAsync(token);
                var clients = await context.Database.SqlQueryRaw<Guid>(
                        "SELECT id AS \"Value\" FROM clients.client_accounts ORDER BY id")
                    .ToArrayAsync(token);
                var parts = snapshot.Split('|', 4);
                return new TenantRead(
                    int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                    parts[1],
                    parts[2],
                    parts[3],
                    clients);
            });
    }

    /// <summary>The session as the next lease sees it, outside any transaction.</summary>
    private static async Task<SessionSnapshot> SnapshotAsync(NpgsqlDataSource pool)
    {
        await using var connection = await pool.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(SnapshotSql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new SessionSnapshot(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetString(8),
            reader.GetInt32(9),
            reader.GetInt32(10));
    }

    private static void AssertFresh(SessionSnapshot fresh, string login)
    {
        Assert.Equal(login, fresh.CurrentUser);
        Assert.Equal(login, fresh.SessionUser);
        Assert.Equal("none", fresh.Role);
        Assert.Equal(string.Empty, fresh.UserSetting);
        Assert.Equal(string.Empty, fresh.OrganizationSetting);
        Assert.Equal(0, fresh.PreparedStatements);
        Assert.Equal(0, fresh.TemporaryRelations);
        Assert.Equal(string.Empty, fresh.SessionSettings);
        Assert.Equal(0, fresh.AdvisoryLocks);
        Assert.Equal(0, fresh.ListeningChannels);
    }

    /// <summary>The paqueteria_app role with no tenant context reads no tenant row (FORCE RLS fails closed).</summary>
    private static async Task<int> CountClientsWithoutContextAsync(NpgsqlDataSource pool)
    {
        await using var connection = await pool.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_app");
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::integer FROM clients.client_accounts",
            connection,
            transaction);
        var count = (int)(await command.ExecuteScalarAsync())!;
        await transaction.RollbackAsync();
        return count;
    }

    /// <summary>The unit-of-work prologue every runtime transaction runs after BEGIN.</summary>
    private static async Task EnterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string role,
        Guid userId,
        Guid[] organizations)
    {
        await using (var user = new NpgsqlCommand(
            "SELECT set_config('app.current_user_id', @user_id::uuid::text, true)", connection, transaction))
        {
            user.Parameters.Add(new NpgsqlParameter<Guid>("user_id", NpgsqlDbType.Uuid) { TypedValue = userId });
            await user.ExecuteScalarAsync();
        }

        await using (var organizationIds = new NpgsqlCommand(
            "SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true)", connection, transaction))
        {
            organizationIds.Parameters.Add(new NpgsqlParameter<Guid[]>(
                "organization_ids",
                NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                TypedValue = organizations,
            });
            await organizationIds.ExecuteScalarAsync();
        }

        await ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {role}");
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SeedTenantsAsync(Guid orgA, Guid orgB, Guid clientA, Guid clientB) =>
        await ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES
              (@org_a,'SCL-007 A','SCL-007 A','BUSINESS'),(@org_b,'SCL-007 B','SCL-007 B','BUSINESS');
            INSERT INTO clients.client_accounts(id,owner_org_id,name) VALUES
              (@client_a,@org_a,'SCL-007 Client A'),(@client_b,@org_b,'SCL-007 Client B');
            """,
            orgA,
            orgB,
            clientA,
            clientB);

    private async Task DeleteTenantsAsync(Guid orgA, Guid orgB, Guid clientA, Guid clientB) =>
        await ExecuteAdminAsync(
            """
            DELETE FROM clients.client_accounts WHERE id IN (@client_a,@client_b);
            DELETE FROM organizations.organizations WHERE id IN (@org_a,@org_b);
            """,
            orgA,
            orgB,
            clientA,
            clientB);

    private async Task ExecuteAdminAsync(string sql, Guid orgA, Guid orgB, Guid clientA, Guid clientB)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter("org_a", orgA));
        command.Parameters.Add(new NpgsqlParameter("org_b", orgB));
        command.Parameters.Add(new NpgsqlParameter("client_a", clientA));
        command.Parameters.Add(new NpgsqlParameter("client_b", clientB));
        await command.ExecuteNonQueryAsync();
    }

    private sealed record SessionSnapshot(
        int BackendId,
        string CurrentUser,
        string SessionUser,
        string Role,
        string UserSetting,
        string OrganizationSetting,
        int PreparedStatements,
        int TemporaryRelations,
        string SessionSettings,
        int AdvisoryLocks,
        int ListeningChannels);

    private sealed record TenantRead(
        int BackendId,
        string CurrentUser,
        string UserSetting,
        string OrganizationSetting,
        Guid[] Clients);

    private sealed class Scope(
        OrganizationsDbContext context,
        TenantDatabaseExecutionState state,
        TenantTransactionContext<OrganizationsDbContext>? application,
        WorkerTenantTransactionContext<OrganizationsDbContext>? worker) : IAsyncDisposable
    {
        public TenantDatabaseExecutionState State { get; } = state;

        public TenantTransactionContext<OrganizationsDbContext> Application =>
            application ?? throw new InvalidOperationException("Not an API scope.");

        public WorkerTenantTransactionContext<OrganizationsDbContext> Worker =>
            worker ?? throw new InvalidOperationException("Not a Worker scope.");

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
