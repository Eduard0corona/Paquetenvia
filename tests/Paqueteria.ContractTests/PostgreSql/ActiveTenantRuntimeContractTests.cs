using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Organizations.Application.OrganizationContexts;
using Organizations.Application.Provisioning;
using Organizations.Domain;
using Organizations.Infrastructure.OrganizationContexts;
using Organizations.Infrastructure.Persistence;
using Organizations.Infrastructure.Provisioning;
using Paqueteria.Application.Tenancy;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Domain.Tenancy;
using Paqueteria.Infrastructure.Tenancy;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class ActiveTenantRuntimeContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Independent_migrator_plans_applies_reapplies_asserts_and_rejects_history_drift()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("ten001migrator");
        const string environmentName = "PAQUETERIA_TEN001_MIGRATOR_TEST_DB";
        try
        {
            var plan = await RunMigratorAsync("plan", environmentName, connectionString);
            Assert.Equal(0, plan.ExitCode);
            Assert.Contains("State: Clean", plan.Output, StringComparison.Ordinal);
            Assert.Contains("Identity: PENDING", plan.Output, StringComparison.Ordinal);
            Assert.Contains("Organizations: PENDING", plan.Output, StringComparison.Ordinal);
            Assert.Contains("Locations: PENDING", plan.Output, StringComparison.Ordinal);
            Assert.Contains("Pricing: PENDING", plan.Output, StringComparison.Ordinal);

            var apply = await RunMigratorAsync("apply", environmentName, connectionString, "--confirm-initial-baseline");
            Assert.Equal(0, apply.ExitCode);
            Assert.Contains("Identity: APPLIED", apply.Output, StringComparison.Ordinal);
            Assert.Contains("Organizations: APPLIED", apply.Output, StringComparison.Ordinal);
            Assert.Contains("Locations: APPLIED", apply.Output, StringComparison.Ordinal);
            Assert.Contains("Pricing: APPLIED", apply.Output, StringComparison.Ordinal);

            var reapply = await RunMigratorAsync("apply", environmentName, connectionString, "--confirm-initial-baseline");
            Assert.True(reapply.ExitCode == 0, reapply.Output);
            Assert.Contains("Result: AlreadyApplied", reapply.Output, StringComparison.Ordinal);

            var assert = await RunMigratorAsync("assert", environmentName, connectionString);
            Assert.Equal(0, assert.ExitCode);
            Assert.Contains("Result: ASSERTIONS_OK", assert.Output, StringComparison.Ordinal);

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    "INSERT INTO platform.\"__ef_migrations_history_identity\" (\"MigrationId\",\"ProductVersion\") VALUES ('99999999_Unexpected','10.0.10')",
                    connection);
                await command.ExecuteNonQueryAsync();
            }

            var drift = await RunMigratorAsync("plan", environmentName, connectionString);
            Assert.Equal(4, drift.ExitCode);
            Assert.Contains("Identity: DRIFT", drift.Output, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Adoption_histories_are_independent_migrator_owned_and_runtime_inaccessible()
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT c.relname, pg_get_userbyid(c.relowner)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='platform'
              AND c.relname IN ('__ef_migrations_history_identity','__ef_migrations_history_organizations','__ef_migrations_history_locations','__ef_migrations_history_pricing')
            ORDER BY c.relname;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        var histories = new List<(string Name, string Owner)>();
        while (await reader.ReadAsync())
        {
            histories.Add((reader.GetString(0), reader.GetString(1)));
        }

        Assert.Equal(4, histories.Count);
        Assert.All(histories, history => Assert.Equal("paqueteria_migrator", history.Owner));

        await using var runtime = await fixture.AppDataSource.OpenConnectionAsync();
        await using var runtimeTransaction = await runtime.BeginTransactionAsync();
        await ExecuteAsync(runtime, runtimeTransaction, "SET LOCAL ROLE paqueteria_app");
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            runtime,
            runtimeTransaction,
            "SELECT * FROM platform.__ef_migrations_history_identity"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [PostgreSqlContractFact]
    public async Task Productive_transaction_filters_organizations_and_guard_fails_before_unscoped_query()
    {
        var userId = Guid.NewGuid();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES
              (@org_a,'TEN A','TEN A','BUSINESS'),(@org_b,'TEN B','TEN B','ALLY');
            """,
            P("org_a", orgA), P("org_b", orgB));

        try
        {
            await using var scope = CreateRuntimeScope();
            Assert.Equal(["TEN A"], await scope.Transaction.ExecuteAsync(
                new TenantDatabaseExecutionContext(userId, [orgA]),
                async (context, token) => await context.Organizations.AsNoTracking()
                    .OrderBy(value => value.DisplayName).Select(value => value.DisplayName).ToArrayAsync(token)));
            Assert.Equal(["TEN B"], await scope.Transaction.ExecuteAsync(
                new TenantDatabaseExecutionContext(userId, [orgB]),
                async (context, token) => await context.Organizations.AsNoTracking()
                    .OrderBy(value => value.DisplayName).Select(value => value.DisplayName).ToArrayAsync(token)));
            Assert.Equal(["TEN A", "TEN B"], await scope.Transaction.ExecuteAsync(
                new TenantDatabaseExecutionContext(userId, [orgB, orgA, orgA]),
                async (context, token) => await context.Organizations.AsNoTracking()
                    .OrderBy(value => value.DisplayName).Select(value => value.DisplayName).ToArrayAsync(token)));
            Assert.Empty(await scope.Transaction.ExecuteAsync(
                new TenantDatabaseExecutionContext(userId, []),
                async (context, token) => await context.Organizations.AsNoTracking().ToArrayAsync(token)));

            Assert.False(scope.State.IsApplied);
            await Assert.ThrowsAsync<TenantTransactionRequiredException>(() =>
                scope.Context.Organizations.AsNoTracking().ToArrayAsync());
        }
        finally
        {
            await ExecuteAdminAsync(
                "DELETE FROM organizations.organizations WHERE id IN (@org_a,@org_b)",
                P("org_a", orgA), P("org_b", orgB));
        }
    }

    [PostgreSqlContractFact]
    public async Task Product_context_reader_uses_all_active_memberships_without_an_active_header()
    {
        var userId = Guid.NewGuid();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES
              (@org_a,'Reader A','Alpha','BUSINESS'),(@org_b,'Reader B','Beta','ALLY');
            """,
            P("org_a", orgA), P("org_b", orgB));

        try
        {
            await using var scope = CreateRuntimeScope();
            var reader = new PostgreSqlOrganizationContextReader(
                scope.Transaction,
                NullLogger<PostgreSqlOrganizationContextReader>.Instance);

            var result = await reader.ReadAsync(
                userId,
                [
                    new AuthorizedOrganizationMembership(orgA, OrganizationRole.BusinessAdmin, true),
                    new AuthorizedOrganizationMembership(orgB, OrganizationRole.Viewer, false),
                ],
                CancellationToken.None);

            Assert.Collection(
                result,
                first =>
                {
                    Assert.Equal(orgA, first.OrganizationId);
                    Assert.Equal("Alpha", first.DisplayName);
                    Assert.Equal("BUSINESS_ADMIN", first.Role);
                    Assert.True(first.IsDefault);
                },
                second =>
                {
                    Assert.Equal(orgB, second.OrganizationId);
                    Assert.Equal("Beta", second.DisplayName);
                    Assert.Equal("VIEWER", second.Role);
                    Assert.False(second.IsDefault);
                });
        }
        finally
        {
            await ExecuteAdminAsync(
                "DELETE FROM organizations.organizations WHERE id IN (@org_a,@org_b)",
                P("org_a", orgA), P("org_b", orgB));
        }
    }

    [PostgreSqlContractFact]
    public async Task Transient_failure_retries_the_entire_transaction_and_reapplies_tenant_context()
    {
        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@id,'Retry','Retry','BUSINESS')",
            P("id", organizationId));

        try
        {
            var failOnce = new FailOnceReaderInterceptor();
            await using var scope = CreateRuntimeScope(failOnce);

            var names = await scope.Transaction.ExecuteAsync(
                new TenantDatabaseExecutionContext(userId, [organizationId]),
                async (context, token) => await context.Organizations.AsNoTracking()
                    .Select(value => value.DisplayName).ToArrayAsync(token));

            Assert.Equal(["Retry"], names);
            Assert.Equal(2, failOnce.Attempts);
            Assert.False(scope.State.IsApplied);
        }
        finally
        {
            await ExecuteAdminAsync(
                "DELETE FROM organizations.organizations WHERE id=@id",
                P("id", organizationId));
        }
    }

    [PostgreSqlContractFact]
    public async Task Productive_provisioning_is_preauthorized_atomic_and_conflict_safe()
    {
        var subject = $"oidc|ten001|{Guid.NewGuid():N}";
        InitialOrganizationProvisioningResult? created = null;
        try
        {
            async Task<(InitialOrganizationProvisioningResult? Result, Exception? Error)> AttemptAsync()
            {
                await using var scope = CreateRuntimeScope();
                var provisioner = new PostgreSqlInitialOrganizationProvisioner(
                    new AllowAuthorizer(),
                    new NoOpProvisioningFailureInjector(),
                    scope.Transaction,
                    new PostgreSqlAppendOnlyAuditWriter(scope.State),
                    new SystemClock());
                try
                {
                    return (await provisioner.ProvisionAsync(
                        new InitialOrganizationProvisioningCommand(
                            subject, "TEN-001 Legal", "TEN-001", OrganizationType.Business,
                            OrganizationRole.BusinessAdmin, "contract-test"),
                        CancellationToken.None), null);
                }
                catch (Exception exception)
                {
                    return (null, exception);
                }
            }

            var attempts = await Task.WhenAll(AttemptAsync(), AttemptAsync());
            created = Assert.Single(attempts, attempt => attempt.Result is not null).Result!;
            Assert.IsType<InitialOrganizationProvisioningConflictException>(
                Assert.Single(attempts, attempt => attempt.Error is not null).Error);

            Assert.Equal(1, await AdminCountAsync("identity.users", "id", created.UserId));
            Assert.Equal(1, await AdminCountAsync("organizations.organizations", "id", created.OrganizationId));
            Assert.Equal(1, await AdminCountAsync("organizations.organization_memberships", "id", created.MembershipId));
            Assert.Equal(1, await AdminCountAsync("platform.audit_logs", "id", created.AuditId));

            Assert.Equal(1, await AdminSubjectCountAsync(subject));
        }
        finally
        {
            if (created is not null)
            {
                await CleanupProvisioningAsync(created);
            }
        }
    }

    [PostgreSqlContractFact]
    public async Task Provisioning_failure_after_each_insert_rolls_back_every_cross_schema_row()
    {
        foreach (var stage in Enum.GetValues<ProvisioningStage>())
        {
            var subject = $"oidc|rollback|{stage}|{Guid.NewGuid():N}";
            await using var scope = CreateRuntimeScope();
            var provisioner = new PostgreSqlInitialOrganizationProvisioner(
                new AllowAuthorizer(),
                new ThrowAtStage(stage),
                scope.Transaction,
                new PostgreSqlAppendOnlyAuditWriter(scope.State),
                new SystemClock());

            await Assert.ThrowsAsync<SyntheticProvisioningException>(() => provisioner.ProvisionAsync(
                new InitialOrganizationProvisioningCommand(
                    subject, "Rollback", "Rollback", OrganizationType.Business,
                    OrganizationRole.BusinessAdmin, null),
                CancellationToken.None));

            Assert.Equal(0, await AdminSubjectCountAsync(subject));
            Assert.Equal(0, await AdminNamedOrganizationCountAsync("Rollback"));
        }
    }

    private RuntimeScope CreateRuntimeScope(IInterceptor? additionalInterceptor = null)
    {
        var state = new TenantDatabaseExecutionState();
        var optionsBuilder = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure());
        if (additionalInterceptor is not null)
        {
            optionsBuilder.AddInterceptors(additionalInterceptor);
        }
        var options = optionsBuilder.AddInterceptors(
            new TenantTransactionGuardInterceptor(state),
            new TenantSaveChangesGuardInterceptor(state)).Options;
        var context = new OrganizationsDbContext(options, state);
        return new RuntimeScope(context, state, new TenantTransactionContext<OrganizationsDbContext>(context, state));
    }

    private async Task CleanupProvisioningAsync(InitialOrganizationProvisioningResult result)
    {
        await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var userContext = new NpgsqlCommand(
                "SELECT set_config('app.current_user_id', @user_id::uuid::text, true)", connection, transaction))
            {
                userContext.Parameters.Add(P("user_id", result.UserId));
                await userContext.ExecuteScalarAsync();
            }
            await using (var organizationContext = new NpgsqlCommand(
                "SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true)", connection, transaction))
            {
                organizationContext.Parameters.Add(new NpgsqlParameter<Guid[]>("organization_ids", [result.OrganizationId]));
                await organizationContext.ExecuteScalarAsync();
            }
            await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_migrator");
            await ExecuteAsync(connection, transaction, "DELETE FROM platform.audit_logs WHERE id=@id", P("id", result.AuditId));
            await transaction.CommitAsync();
        }

        await ExecuteAdminAsync(
            """
            DELETE FROM organizations.organization_memberships WHERE id=@membership;
            DELETE FROM organizations.organizations WHERE id=@organization;
            DELETE FROM identity.users WHERE id=@user_id;
            """,
            P("membership", result.MembershipId), P("organization", result.OrganizationId), P("user_id", result.UserId));
    }

    private async Task<int> AdminCountAsync(string table, string column, Guid value)
    {
        await using var command = fixture.AdminDataSource.CreateCommand($"SELECT count(*)::integer FROM {table} WHERE {column}=@value");
        command.Parameters.Add(P("value", value));
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<int> AdminSubjectCountAsync(string subject)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT count(*)::integer FROM identity.users WHERE identity_subject=@subject");
        command.Parameters.Add(P("subject", subject));
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<int> AdminNamedOrganizationCountAsync(string displayName)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT count(*)::integer FROM organizations.organizations WHERE display_name=@display_name");
        command.Parameters.Add(P("display_name", displayName));
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlParameter P(string name, object value) => new(name, value);

    private static async Task<(int ExitCode, string Output)> RunMigratorAsync(
        string command,
        string environmentName,
        string connectionString,
        params string[] additionalArguments)
    {
        var root = RepositoryRootLocator.Find();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(root, "tools", "Paqueteria.DatabaseMigrator"));
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add("--connection-env");
        startInfo.ArgumentList.Add(environmentName);
        foreach (var argument in additionalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment[environmentName] = connectionString;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the independent database migrator.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await standardOutput) + Environment.NewLine + (await standardError));
    }

    private sealed class AllowAuthorizer : IInitialOrganizationProvisioningAuthorizer
    {
        public ValueTask<bool> IsAuthorizedAsync(
            InitialOrganizationProvisioningCommand command,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class ThrowAtStage(ProvisioningStage expected) : IProvisioningFailureInjector
    {
        public ValueTask AfterAsync(ProvisioningStage stage, CancellationToken cancellationToken)
        {
            if (stage == expected)
            {
                throw new SyntheticProvisioningException();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class SyntheticProvisioningException : Exception;

    private sealed class FailOnceReaderInterceptor : DbCommandInterceptor
    {
        private int attempts;

        public int Attempts => attempts;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new NpgsqlException("Synthetic transient failure.", new TimeoutException());
            }

            return ValueTask.FromResult(result);
        }
    }

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
