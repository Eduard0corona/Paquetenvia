using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Organizations.Infrastructure.Auditing;
using Organizations.Domain;
using Organizations.Infrastructure.Persistence;
using Paqueteria.Application.Tenancy;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Tenancy;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class TransactionalRlsPoolingContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Productive_context_initialization_is_the_first_application_work_after_begin_and_is_ordered()
    {
        var logs = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(logs));
        await using var dataSource = fixture.CreateAppDataSource(loggerFactory);
        await using var scope = CreateRuntimeScope(dataSource);

        var userId = Guid.NewGuid();
        var firstOrganization = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondOrganization = Guid.Parse("00000000-0000-0000-0000-000000000002");

        var observed = await scope.Transaction.ExecuteAsync(
            new TenantDatabaseExecutionContext(userId, [secondOrganization, firstOrganization]),
            async (context, token) => await context.Database
                .SqlQueryRaw<string>(
                    "SELECT current_user || '|' || current_setting('app.current_user_id',true) || '|' || current_setting('app.current_org_ids',true) AS \"Value\"")
                .SingleAsync(token));

        Assert.Equal($"paqueteria_app|{userId:D}|{{{firstOrganization:D},{secondOrganization:D}}}", observed);

        var evidence = logs.Entries
            .Where(entry => entry.Category.StartsWith("Npgsql.", StringComparison.Ordinal))
            .Select(entry => entry.Message)
            .ToArray();
        var begin = FindIndex(evidence, "Starting transaction");
        var user = FindIndex(evidence, "app.current_user_id");
        var organizations = FindIndex(evidence, "app.current_org_ids");
        var role = FindIndex(evidence, "SET LOCAL ROLE paqueteria_app");
        var business = FindIndex(evidence, "current_user ||");

        Assert.True(begin >= 0, Dump(evidence));
        Assert.True(begin < user, Dump(evidence));
        Assert.True(user < organizations, Dump(evidence));
        Assert.True(organizations < role, Dump(evidence));
        Assert.True(role < business, Dump(evidence));
        var organizationCommand = Assert.Single(
            evidence,
            message => message.Contains("Executing command: SELECT set_config('app.current_org_ids'", StringComparison.Ordinal));
        Assert.Contains("$1::uuid[]::text", organizationCommand, StringComparison.Ordinal);
        Assert.Contains(firstOrganization.ToString("D"), organizationCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(secondOrganization.ToString("D"), organizationCommand, StringComparison.OrdinalIgnoreCase);
    }

    [PostgreSqlContractFact]
    public async Task Empty_organization_context_is_exactly_braces_and_never_null()
    {
        var logs = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(logs));
        await using var dataSource = fixture.CreateAppDataSource(loggerFactory);
        await using var scope = CreateRuntimeScope(dataSource);

        var observed = await scope.Transaction.ExecuteAsync(
            new TenantDatabaseExecutionContext(Guid.NewGuid(), []),
            async (context, token) => await context.Database
                .SqlQueryRaw<string>("SELECT current_setting('app.current_org_ids',true) AS \"Value\"")
                .SingleAsync(token));

        Assert.Equal("{}", observed);
        Assert.NotNull(observed);
        var organizationCommand = Assert.Single(
            logs.Entries.Select(entry => entry.Message),
            message => message.Contains("Executing command: SELECT set_config('app.current_org_ids'", StringComparison.Ordinal));
        Assert.Contains("$1::uuid[]::text", organizationCommand, StringComparison.Ordinal);
        Assert.Contains("Parameters: [[]]", organizationCommand, StringComparison.Ordinal);
    }

    [PostgreSqlContractFact]
    public async Task Tenant_queries_commands_and_save_changes_fail_before_useful_sql_outside_transaction()
    {
        var logs = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(logs));
        await using var dataSource = fixture.CreateAppDataSource(loggerFactory);
        await using var scope = CreateRuntimeScope(dataSource);

        await Assert.ThrowsAsync<TenantTransactionRequiredException>(() =>
            scope.Context.Organizations.AsNoTracking().ToArrayAsync());
        await Assert.ThrowsAsync<TenantTransactionRequiredException>(() =>
            scope.Context.Organizations
                .FromSqlRaw("SELECT * FROM organizations.organizations /* ten002_sql_query */")
                .AsNoTracking()
                .ToArrayAsync());
        await Assert.ThrowsAsync<TenantTransactionRequiredException>(() =>
            scope.Context.Database.ExecuteSqlRawAsync(
                "DELETE FROM organizations.organizations WHERE false /* ten002_command */"));

        scope.Context.Organizations.Add(new Organization(
            Guid.NewGuid(), "TEN-002 guard", "TEN-002 guard", OrganizationType.Business,
            OrganizationStatus.Active, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<TenantTransactionRequiredException>(() => scope.Context.SaveChangesAsync());

        scope.Context.ChangeTracker.Clear();
        await scope.Transaction.ExecuteAsync(
            new TenantDatabaseExecutionContext(Guid.NewGuid(), []),
            async (context, token) => await context.Organizations.AsNoTracking().CountAsync(token));
        await Assert.ThrowsAsync<TenantTransactionRequiredException>(() =>
            scope.Context.Organizations.AsNoTracking().ToArrayAsync());

        var executedSql = logs.Entries.Select(entry => entry.Message).ToArray();
        Assert.DoesNotContain(executedSql, message => message.Contains("ten002_sql_query", StringComparison.Ordinal));
        Assert.DoesNotContain(executedSql, message => message.Contains("ten002_command", StringComparison.Ordinal));
        Assert.DoesNotContain(executedSql, message => message.Contains("TEN-002 guard", StringComparison.Ordinal));
    }

    [PostgreSqlContractFact]
    public async Task Retry_strategy_creates_new_transactions_and_reapplies_the_complete_context_before_business_sql()
    {
        var logs = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(logs));
        await using var dataSource = fixture.CreateAppDataSource(loggerFactory);
        await using var scope = CreateRuntimeScope(dataSource);
        var attempts = 0;
        var transactionIds = new List<long>();

        var result = await scope.Transaction.ExecuteAsync(
            new TenantDatabaseExecutionContext(Guid.NewGuid(), [Guid.NewGuid()]),
            async (context, token) =>
            {
                var transactionId = await context.Database
                    .SqlQueryRaw<long>("SELECT txid_current()::bigint AS \"Value\" /* ten002_retry_business */")
                    .SingleAsync(token);
                transactionIds.Add(transactionId);
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new NpgsqlException("Synthetic TEN-002 transient failure.", new TimeoutException());
                }

                return transactionId;
            });

        Assert.Equal(2, attempts);
        Assert.Equal(2, transactionIds.Count);
        Assert.NotEqual(transactionIds[0], transactionIds[1]);
        Assert.Equal(transactionIds[1], result);
        Assert.False(scope.State.IsApplied);

        var evidence = logs.Entries.Select(entry => entry.Message).ToArray();
        Assert.Equal(2, Count(evidence, "Starting transaction"));
        Assert.Equal(2, Count(evidence, "Executing command: SELECT set_config('app.current_user_id'"));
        Assert.Equal(2, Count(evidence, "Executing command: SELECT set_config('app.current_org_ids'"));
        Assert.Equal(2, Count(evidence, "Executing command: SET LOCAL ROLE paqueteria_app"));
        Assert.Equal(2, Count(evidence, "Executing command: SELECT s.\"Value\"", "ten002_retry_business"));
        Assert.Equal(1, Count(evidence, "Rolled back transaction"));
        Assert.Equal(1, Count(evidence, "Committed transaction"));

        var starts = FindIndexes(evidence, "Starting transaction");
        for (var attempt = 0; attempt < starts.Length; attempt++)
        {
            var end = attempt + 1 < starts.Length ? starts[attempt + 1] : evidence.Length;
            var segment = evidence[starts[attempt]..end];
            Assert.True(FindIndex(segment, "app.current_user_id") < FindIndex(segment, "app.current_org_ids"), Dump(segment));
            Assert.True(FindIndex(segment, "app.current_org_ids") < FindIndex(segment, "SET LOCAL ROLE paqueteria_app"), Dump(segment));
            Assert.True(FindIndex(segment, "SET LOCAL ROLE paqueteria_app") < FindIndex(segment, "ten002_retry_business"), Dump(segment));
        }
    }

    [PostgreSqlContractFact]
    public async Task Single_connection_pool_is_clean_across_users_orgs_empty_context_exceptions_and_cancellation()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var privilegedRequestId = $"ten002-{Guid.NewGuid():N}";
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var membershipA = Guid.NewGuid();
        var membershipB = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES
              (@org_a,'TEN-002 A','TEN-002 A','BUSINESS'),(@org_b,'TEN-002 B','TEN-002 B','BUSINESS');
            INSERT INTO identity.users(id,identity_subject) VALUES
              (@user_a,@subject_a),(@user_b,@subject_b);
            INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default) VALUES
              (@membership_a,@user_a,@org_a,'PLATFORM_ADMIN','ACTIVE',true),
              (@membership_b,@user_b,@org_b,'VIEWER','ACTIVE',true);
            INSERT INTO clients.client_accounts(id,owner_org_id,name) VALUES
              (@client_a,@org_a,'TEN-002 Client A'),(@client_b,@org_b,'TEN-002 Client B');
            """,
            P("org_a", orgA), P("org_b", orgB), P("user_a", userA), P("user_b", userB),
            P("subject_a", $"ten002|{userA:N}"), P("subject_b", $"ten002|{userB:N}"),
            P("membership_a", membershipA), P("membership_b", membershipB),
            P("client_a", clientA), P("client_b", clientB));

        try
        {
            await using var dataSource = fixture.CreateAppDataSource(maxPoolSize: 1);
            var backendIds = new List<int>();

            for (var iteration = 0; iteration < 4; iteration++)
            {
                var a = await ReadTenantLeaseAsync(dataSource, userA, [orgA]);
                var b = await ReadTenantLeaseAsync(dataSource, userB, [orgB]);
                backendIds.Add(a.BackendId);
                backendIds.Add(b.BackendId);
                Assert.Equal(["TEN-002 Client A"], a.ClientNames);
                Assert.Equal(["TEN-002 Client B"], b.ClientNames);
                Assert.Equal(userA.ToString("D"), a.UserSetting);
                Assert.Equal(userB.ToString("D"), b.UserSetting);
                Assert.Equal($"{{{orgA:D}}}", a.OrganizationSetting);
                Assert.Equal($"{{{orgB:D}}}", b.OrganizationSetting);
            }

            Assert.Single(backendIds.Distinct());

            var empty = await ReadTenantLeaseAsync(dataSource, Guid.NewGuid(), []);
            Assert.Equal(backendIds[0], empty.BackendId);
            Assert.Equal("{}", empty.OrganizationSetting);
            Assert.Empty(empty.ClientNames);

            await using (var privilegedScope = CreateRuntimeScope(dataSource))
            {
                var audit = new PostgreSqlPlatformAdminTenantActivationAudit(
                    privilegedScope.Transaction,
                    new PostgreSqlAppendOnlyAuditWriter(privilegedScope.State),
                    new SystemClock());
                await audit.RecordAsync(userA, orgA, privilegedRequestId, CancellationToken.None);
            }
            await AssertCleanLeaseAsync(dataSource, backendIds[0]);

            await Assert.ThrowsAsync<SyntheticTransactionException>(() => ExecuteThrowingTransactionAsync(
                dataSource,
                new TenantDatabaseExecutionContext(userA, [orgA])));
            await AssertCleanLeaseAsync(dataSource, backendIds[0]);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteCancelledTransactionAsync(
                dataSource,
                new TenantDatabaseExecutionContext(userB, [orgB]),
                cancellation.Token));
            await AssertCleanLeaseAsync(dataSource);
        }
        finally
        {
            await DeleteAuditAsync(privilegedRequestId, orgA);
            await ExecuteAdminAsync(
                """
                DELETE FROM clients.client_accounts WHERE id IN (@client_a,@client_b);
                DELETE FROM organizations.organization_memberships WHERE id IN (@membership_a,@membership_b);
                DELETE FROM organizations.organizations WHERE id IN (@org_a,@org_b);
                DELETE FROM identity.users WHERE id IN (@user_a,@user_b);
                """,
                P("client_a", clientA), P("client_b", clientB),
                P("membership_a", membershipA), P("membership_b", membershipB),
                P("org_a", orgA), P("org_b", orgB), P("user_a", userA), P("user_b", userB));
        }
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_pooled_transactions_do_not_cross_contaminate_tenants()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES
              (@org_a,'TEN-002 concurrent A','TEN-002 concurrent A','BUSINESS'),(@org_b,'TEN-002 concurrent B','TEN-002 concurrent B','BUSINESS');
            INSERT INTO clients.client_accounts(id,owner_org_id,name) VALUES
              (@client_a,@org_a,'Concurrent A'),(@client_b,@org_b,'Concurrent B');
            """,
            P("org_a", orgA), P("org_b", orgB), P("client_a", clientA), P("client_b", clientB));

        try
        {
            await using var dataSource = fixture.CreateAppDataSource(maxPoolSize: 4);
            var tasks = Enumerable.Range(0, 16).Select(async index =>
            {
                var organization = index % 2 == 0 ? orgA : orgB;
                var expected = index % 2 == 0 ? "Concurrent A" : "Concurrent B";
                var lease = await ReadTenantLeaseAsync(dataSource, Guid.NewGuid(), [organization]);
                Assert.Equal([expected], lease.ClientNames);
            });

            await Task.WhenAll(tasks);
        }
        finally
        {
            await ExecuteAdminAsync(
                "DELETE FROM clients.client_accounts WHERE id IN (@client_a,@client_b); DELETE FROM organizations.organizations WHERE id IN (@org_a,@org_b)",
                P("client_a", clientA), P("client_b", clientB), P("org_a", orgA), P("org_b", orgB));
        }
    }

    private RuntimeScope CreateRuntimeScope(NpgsqlDataSource dataSource, params IInterceptor[] additionalInterceptors)
    {
        var state = new TenantDatabaseExecutionState();
        var interceptors = new List<IInterceptor>
        {
            new TenantTransactionGuardInterceptor(state),
            new TenantSaveChangesGuardInterceptor(state),
        };
        interceptors.AddRange(additionalInterceptors);
        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(interceptors)
            .Options;
        var context = new OrganizationsDbContext(options, state);
        return new RuntimeScope(context, state, new TenantTransactionContext<OrganizationsDbContext>(context, state));
    }

    private async Task<TenantLeaseEvidence> ReadTenantLeaseAsync(
        NpgsqlDataSource dataSource,
        Guid userId,
        Guid[] organizationIds)
    {
        await using var scope = CreateRuntimeScope(dataSource);
        return await scope.Transaction.ExecuteAsync(
            new TenantDatabaseExecutionContext(userId, organizationIds),
            async (context, token) =>
            {
                var snapshot = await context.Database.SqlQueryRaw<string>(
                        "SELECT pg_backend_pid()::text || '|' || current_user || '|' || current_setting('app.current_user_id',true) || '|' || current_setting('app.current_org_ids',true) AS \"Value\"")
                    .SingleAsync(token);
                var parts = snapshot.Split('|', 4);
                var clients = await context.Database.SqlQueryRaw<string>(
                        "SELECT name AS \"Value\" FROM clients.client_accounts ORDER BY name")
                    .ToArrayAsync(token);
                return new TenantLeaseEvidence(
                    int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                    parts[1],
                    parts[2],
                    parts[3],
                    clients);
            });
    }

    private async Task ExecuteThrowingTransactionAsync(
        NpgsqlDataSource dataSource,
        TenantDatabaseExecutionContext executionContext)
    {
        await using var scope = CreateRuntimeScope(dataSource);
        await scope.Transaction.ExecuteAsync<int>(
            executionContext,
            async (context, token) =>
            {
                _ = await context.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(token);
                throw new SyntheticTransactionException();
            });
    }

    private async Task ExecuteCancelledTransactionAsync(
        NpgsqlDataSource dataSource,
        TenantDatabaseExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        await using var scope = CreateRuntimeScope(dataSource);
        await scope.Transaction.ExecuteAsync(
            executionContext,
            async (context, token) => await context.Database.ExecuteSqlRawAsync("SELECT pg_sleep(10)", token),
            cancellationToken);
    }

    private static async Task AssertCleanLeaseAsync(NpgsqlDataSource dataSource, int? expectedBackendId = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pg_backend_pid()::text || '|' || current_user || '|' || COALESCE(current_setting('app.current_user_id',true),'') || '|' || COALESCE(current_setting('app.current_org_ids',true),'')",
            connection);
        var snapshot = (string)(await command.ExecuteScalarAsync())!;
        var parts = snapshot.Split('|', 4);
        if (expectedBackendId.HasValue)
        {
            Assert.Equal(expectedBackendId.Value, int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Equal(PostgreSqlContractFixture.AppLogin, parts[1]);
        Assert.Equal(string.Empty, parts[2]);
        Assert.Equal(string.Empty, parts[3]);

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_app", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var count = new NpgsqlCommand(
            "SELECT count(*)::integer FROM clients.client_accounts",
            connection,
            transaction);
        Assert.Equal(0, (int)(await count.ExecuteScalarAsync())!);
        await transaction.RollbackAsync();
    }

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteAuditAsync(string requestId, Guid organizationId)
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var user = new NpgsqlCommand(
            "SELECT set_config('app.current_user_id', @user_id::uuid::text, true)", connection, transaction))
        {
            user.Parameters.Add(P("user_id", Guid.NewGuid()));
            await user.ExecuteScalarAsync();
        }
        await using (var organizations = new NpgsqlCommand(
            "SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true)", connection, transaction))
        {
            organizations.Parameters.Add(new NpgsqlParameter<Guid[]>("organization_ids", [organizationId]));
            await organizations.ExecuteScalarAsync();
        }
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_migrator", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }
        await using (var delete = new NpgsqlCommand(
            "DELETE FROM platform.audit_logs WHERE request_id=@request_id", connection, transaction))
        {
            delete.Parameters.Add(P("request_id", requestId));
            await delete.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static NpgsqlParameter P(string name, object value) => new(name, value);

    private static int FindIndex(IReadOnlyList<string> values, string fragment)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index].Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static int[] FindIndexes(IReadOnlyList<string> values, string fragment) => values
        .Select((value, index) => (value, index))
        .Where(item => item.value.Contains(fragment, StringComparison.OrdinalIgnoreCase))
        .Select(item => item.index)
        .ToArray();

    private static int Count(IReadOnlyList<string> values, string fragment, string? requiredFragment = null) => values.Count(
        value => value.Contains(fragment, StringComparison.OrdinalIgnoreCase) &&
                 (requiredFragment is null || value.Contains(requiredFragment, StringComparison.OrdinalIgnoreCase)));

    private static string Dump(IEnumerable<string> values) => string.Join(Environment.NewLine, values);

    private sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();

        public IReadOnlyCollection<LogEntry> Entries => entries.ToArray();

        public void Clear() => entries.Clear();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(
            string category,
            ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(category, logLevel, eventId, formatter(state, exception)));
        }
    }

    private sealed record TenantLeaseEvidence(
        int BackendId,
        string EffectiveRole,
        string UserSetting,
        string OrganizationSetting,
        string[] ClientNames);

    private sealed class SyntheticTransactionException : Exception;

    private sealed class RuntimeScope(
        OrganizationsDbContext context,
        TenantDatabaseExecutionState state,
        TenantTransactionContext<OrganizationsDbContext> transaction) : IAsyncDisposable
    {
        public OrganizationsDbContext Context { get; } = context;
        public TenantDatabaseExecutionState State { get; } = state;
        public TenantTransactionContext<OrganizationsDbContext> Transaction { get; } = transaction;
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}
