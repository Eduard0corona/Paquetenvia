using System.Collections.Immutable;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Organizations.Infrastructure.Persistence;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Tenancy;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class AppendOnlyAuditContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task General_writer_persists_application_values_and_only_redacted_payload()
    {
        var organizationId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        const string syntheticSecret = "synthetic-audit-password";
        await SeedAsync(organizationId, actorId);
        try
        {
            var redactor = new AuditPayloadRedactor();
            var payload = redactor.Redact(JsonSerializer.SerializeToElement(new
            {
                operation = "activate",
                password = syntheticSecret,
                nested = new { email = "audit-person@example.test" },
            }));
            var occurredAt = new DateTimeOffset(2026, 7, 22, 12, 34, 56, TimeSpan.Zero);

            await WriteWithRoleAsync(
                fixture.AppDataSource,
                "paqueteria_app",
                actorId,
                organizationId,
                new AuditEntry(
                    auditId,
                    organizationId,
                    actorId,
                    "SYNTHETIC_AUDIT_ACTION",
                    "SYNTHETIC_ENTITY",
                    organizationId,
                    "synthetic-request-id",
                    payload,
                    occurredAt));

            await using var command = fixture.AdminDataSource.CreateCommand(
                "SELECT org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted::text,occurred_at FROM platform.audit_logs WHERE id=@id");
            command.Parameters.Add(P("id", auditId));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(organizationId, reader.GetGuid(0));
            Assert.Equal(actorId, reader.GetGuid(1));
            Assert.Equal("SYNTHETIC_AUDIT_ACTION", reader.GetString(2));
            Assert.Equal("SYNTHETIC_ENTITY", reader.GetString(3));
            Assert.Equal(organizationId, reader.GetGuid(4));
            Assert.Equal("synthetic-request-id", reader.GetString(5));
            var storedPayload = reader.GetString(6);
            Assert.DoesNotContain(syntheticSecret, storedPayload, StringComparison.Ordinal);
            Assert.DoesNotContain("audit-person@example.test", storedPayload, StringComparison.Ordinal);
            Assert.Contains(AuditPayloadRedactor.Replacement, storedPayload, StringComparison.Ordinal);
            Assert.Equal(occurredAt, reader.GetFieldValue<DateTimeOffset>(7));
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            await CleanupAsync([organizationId], [actorId]);
        }
    }

    [PostgreSqlContractFact]
    public async Task App_and_worker_can_insert_and_select_but_cannot_update_delete_or_bypass_RLS()
    {
        var organizationA = Guid.NewGuid();
        var organizationB = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var appAuditId = Guid.NewGuid();
        var workerAuditId = Guid.NewGuid();
        await SeedAsync(organizationA, actorId, organizationB);
        try
        {
            var roles = new[]
            {
                (Name: "paqueteria_app", Login: PostgreSqlContractFixture.AppLogin, Source: fixture.AppDataSource, AuditId: appAuditId),
                (Name: "paqueteria_worker", Login: PostgreSqlContractFixture.WorkerLogin, Source: fixture.WorkerDataSource, AuditId: workerAuditId),
            };

            var expectedVisibleRows = 0;
            foreach (var role in roles)
            {
                await WriteWithRoleAsync(
                    role.Source,
                    role.Name,
                    actorId,
                    organizationA,
                    Entry(role.AuditId, organizationA, actorId, $"{role.Name}-request"));

                expectedVisibleRows++;
                Assert.Equal(expectedVisibleRows, await RuntimeCountAsync(role.Source, role.Name, actorId, [organizationA]));
                await AssertMutationRejectedAsync(role.Source, role.Name, actorId, organizationA, role.AuditId, "UPDATE platform.audit_logs SET action=action WHERE id=@id");
                await AssertMutationRejectedAsync(role.Source, role.Name, actorId, organizationA, role.AuditId, "DELETE FROM platform.audit_logs WHERE id=@id");
                await AssertPrivilegedRoleCannotBeAssumedAsync(role.Source, role.Name, actorId, organizationA);

                await using var grants = fixture.AdminDataSource.CreateCommand(
                    "SELECT has_table_privilege(@role,'platform.audit_logs','SELECT'), has_table_privilege(@role,'platform.audit_logs','INSERT'), has_table_privilege(@role,'platform.audit_logs','UPDATE'), has_table_privilege(@role,'platform.audit_logs','DELETE'), rolbypassrls FROM pg_roles WHERE rolname=@role");
                grants.Parameters.Add(P("role", role.Name));
                await using var reader = await grants.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.GetBoolean(0));
                Assert.True(reader.GetBoolean(1));
                Assert.False(reader.GetBoolean(2));
                Assert.False(reader.GetBoolean(3));
                Assert.False(reader.GetBoolean(4));
            }

            Assert.Equal(0, await RuntimeCountAsync(fixture.AppDataSource, "paqueteria_app", actorId, [organizationB]));
            Assert.Equal(0, await RuntimeCountAsync(fixture.AppDataSource, "paqueteria_app", actorId, []));

            await using var forceRls = fixture.AdminDataSource.CreateCommand(
                "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE oid='platform.audit_logs'::regclass");
            await using var forceReader = await forceRls.ExecuteReaderAsync();
            Assert.True(await forceReader.ReadAsync());
            Assert.True(forceReader.GetBoolean(0));
            Assert.True(forceReader.GetBoolean(1));
        }
        finally
        {
            await CleanupAsync([organizationA, organizationB], [actorId]);
        }
    }

    [PostgreSqlContractFact]
    public async Task Sensitive_action_and_audit_commit_or_roll_back_together_for_failures_and_cancellation()
    {
        var organizationId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        await SeedAsync(organizationId, actorId);
        try
        {
            var successfulClient = Guid.NewGuid();
            var successfulAudit = Guid.NewGuid();
            await ExecuteAuditedActionAsync(organizationId, actorId, successfulClient, successfulAudit, FailurePoint.None);
            await AssertAtomicCountsAsync(successfulClient, successfulAudit, 1);

            foreach (var failurePoint in new[]
                     {
                         FailurePoint.BeforeAudit,
                         FailurePoint.AfterAudit,
                         FailurePoint.CancelAfterAudit,
                     })
            {
                var clientId = Guid.NewGuid();
                var auditId = Guid.NewGuid();
                if (failurePoint == FailurePoint.CancelAfterAudit)
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        ExecuteAuditedActionAsync(organizationId, actorId, clientId, auditId, failurePoint));
                }
                else
                {
                    await Assert.ThrowsAsync<SyntheticAuditActionException>(() =>
                        ExecuteAuditedActionAsync(organizationId, actorId, clientId, auditId, failurePoint));
                }

                await AssertAtomicCountsAsync(clientId, auditId, 0);
            }

            var auditFailureClient = Guid.NewGuid();
            var missingActor = Guid.NewGuid();
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAuditedActionAsync(
                organizationId,
                missingActor,
                auditFailureClient,
                Guid.NewGuid(),
                FailurePoint.None));
            Assert.Equal(0, await AdminCountAsync("clients.client_accounts", "id", auditFailureClient));
        }
        finally
        {
            await CleanupAsync([organizationId], [actorId]);
        }
    }

    [PostgreSqlContractFact]
    public async Task Transient_retry_uses_new_transactions_and_commits_exactly_one_action_and_audit()
    {
        var organizationId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var transactionIds = new List<long>();
        await SeedAsync(organizationId, actorId);
        try
        {
            var failOnce = new FailOnceReaderInterceptor();
            await using var scope = CreateRuntimeScope(failOnce);
            var writer = new PostgreSqlAppendOnlyAuditWriter(scope.State);

            await scope.Transaction.ExecuteAsync(
                new TenantDatabaseExecutionContext(actorId, [organizationId]),
                async (dbContext, token) =>
                {
                    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                    var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
                    transactionIds.Add(await ScalarAsync<long>(connection, transaction, "SELECT txid_current()"));
                    await InsertClientAsync(connection, transaction, clientId, organizationId, token);
                    await writer.WriteAsync(connection, transaction, Entry(auditId, organizationId, actorId, "retry-request"), token);
                    _ = await dbContext.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync(token);
                    return true;
                });

            Assert.Equal(2, failOnce.Attempts);
            Assert.Equal(2, transactionIds.Count);
            Assert.NotEqual(transactionIds[0], transactionIds[1]);
            await AssertAtomicCountsAsync(clientId, auditId, 1);
        }
        finally
        {
            await CleanupAsync([organizationId], [actorId]);
        }
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_tenant_audits_do_not_cross_contaminate()
    {
        var organizationA = Guid.NewGuid();
        var organizationB = Guid.NewGuid();
        var actorA = Guid.NewGuid();
        var actorB = Guid.NewGuid();
        var auditA = Guid.NewGuid();
        var auditB = Guid.NewGuid();
        await SeedAsync(organizationA, actorA, organizationB, actorB);
        try
        {
            await Task.WhenAll(
                WriteWithRoleAsync(fixture.AppDataSource, "paqueteria_app", actorA, organizationA, Entry(auditA, organizationA, actorA, "concurrent-a")),
                WriteWithRoleAsync(fixture.AppDataSource, "paqueteria_app", actorB, organizationB, Entry(auditB, organizationB, actorB, "concurrent-b")));

            Assert.Equal(1, await RuntimeCountAsync(fixture.AppDataSource, "paqueteria_app", actorA, [organizationA]));
            Assert.Equal(1, await RuntimeCountAsync(fixture.AppDataSource, "paqueteria_app", actorB, [organizationB]));
        }
        finally
        {
            await CleanupAsync([organizationA, organizationB], [actorA, actorB]);
        }
    }

    private async Task ExecuteAuditedActionAsync(
        Guid organizationId,
        Guid actorId,
        Guid clientId,
        Guid auditId,
        FailurePoint failurePoint)
    {
        using var cancellation = new CancellationTokenSource();
        await using var scope = CreateRuntimeScope();
        var writer = new PostgreSqlAppendOnlyAuditWriter(scope.State);
        await scope.Transaction.ExecuteAsync(
            new TenantDatabaseExecutionContext(actorId, [organizationId]),
            async (dbContext, token) =>
            {
                var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
                await InsertClientAsync(connection, transaction, clientId, organizationId, token);
                if (failurePoint == FailurePoint.BeforeAudit)
                {
                    throw new SyntheticAuditActionException();
                }

                await writer.WriteAsync(connection, transaction, Entry(auditId, organizationId, actorId, "atomic-request"), token);
                if (failurePoint == FailurePoint.AfterAudit)
                {
                    throw new SyntheticAuditActionException();
                }

                if (failurePoint == FailurePoint.CancelAfterAudit)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                return true;
            },
            cancellation.Token);
    }

    private RuntimeScope CreateRuntimeScope(params IInterceptor[] interceptors)
    {
        var state = new TenantDatabaseExecutionState();
        var allInterceptors = new List<IInterceptor>
        {
            new TenantTransactionGuardInterceptor(state),
            new TenantSaveChangesGuardInterceptor(state),
        };
        allInterceptors.AddRange(interceptors);
        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(allInterceptors)
            .Options;
        var context = new OrganizationsDbContext(options, state);
        return new RuntimeScope(context, state, new TenantTransactionContext<OrganizationsDbContext>(context, state));
    }

    private static async Task WriteWithRoleAsync(
        NpgsqlDataSource dataSource,
        string runtimeRole,
        Guid actorId,
        Guid organizationId,
        AuditEntry entry)
    {
        await using var tenant = await TenantTransaction.BeginAsync(dataSource, runtimeRole, actorId, [organizationId]);
        var state = new TenantDatabaseExecutionState();
        state.Enter(actorId, ImmutableArray.Create(organizationId));
        try
        {
            await new PostgreSqlAppendOnlyAuditWriter(state).WriteAsync(
                tenant.Connection,
                tenant.Transaction,
                entry,
                CancellationToken.None);
            await tenant.CommitAsync();
        }
        finally
        {
            state.Exit();
        }
    }

    private static async Task<int> RuntimeCountAsync(
        NpgsqlDataSource dataSource,
        string runtimeRole,
        Guid actorId,
        Guid[] organizationIds)
    {
        await using var tenant = await TenantTransaction.BeginAsync(dataSource, runtimeRole, actorId, organizationIds);
        await using var command = new NpgsqlCommand("SELECT count(*)::integer FROM platform.audit_logs", tenant.Connection, tenant.Transaction);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertMutationRejectedAsync(
        NpgsqlDataSource dataSource,
        string runtimeRole,
        Guid actorId,
        Guid organizationId,
        Guid auditId,
        string sql)
    {
        await using var tenant = await TenantTransaction.BeginAsync(dataSource, runtimeRole, actorId, [organizationId]);
        await using var command = new NpgsqlCommand(sql, tenant.Connection, tenant.Transaction);
        command.Parameters.Add(P("id", auditId));
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private static async Task AssertPrivilegedRoleCannotBeAssumedAsync(
        NpgsqlDataSource dataSource,
        string runtimeRole,
        Guid actorId,
        Guid organizationId)
    {
        await using var tenant = await TenantTransaction.BeginAsync(dataSource, runtimeRole, actorId, [organizationId]);
        await using var command = new NpgsqlCommand("SET LOCAL ROLE paqueteria_migrator", tenant.Connection, tenant.Transaction);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private async Task SeedAsync(Guid organizationA, Guid actorA, Guid? organizationB = null, Guid? actorB = null)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@org_a,'Synthetic Audit A','Synthetic Audit A','BUSINESS');
            INSERT INTO identity.users(id,identity_subject,status) VALUES (@actor_a,@subject_a,'ACTIVE');
            """);
        command.Parameters.Add(P("org_a", organizationA));
        command.Parameters.Add(P("actor_a", actorA));
        command.Parameters.Add(P("subject_a", $"oidc|audit|{actorA:N}"));
        if (organizationB.HasValue)
        {
            command.CommandText += "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@org_b,'Synthetic Audit B','Synthetic Audit B','ALLY');";
            command.Parameters.Add(P("org_b", organizationB.Value));
        }

        if (actorB.HasValue)
        {
            command.CommandText += "INSERT INTO identity.users(id,identity_subject,status) VALUES (@actor_b,@subject_b,'ACTIVE');";
            command.Parameters.Add(P("actor_b", actorB.Value));
            command.Parameters.Add(P("subject_b", $"oidc|audit|{actorB.Value:N}"));
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task CleanupAsync(Guid[] organizationIds, Guid[] actorIds)
    {
        await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var context = new NpgsqlCommand(
                "SELECT set_config('app.current_user_id', @user_id::uuid::text, true), set_config('app.current_org_ids', @organization_ids::uuid[]::text, true)",
                connection,
                transaction))
            {
                context.Parameters.Add(new NpgsqlParameter<Guid>("user_id", NpgsqlDbType.Uuid) { TypedValue = actorIds[0] });
                context.Parameters.Add(new NpgsqlParameter<Guid[]>("organization_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = organizationIds });
                await context.ExecuteNonQueryAsync();
            }

            await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_migrator", connection, transaction))
            {
                await role.ExecuteNonQueryAsync();
            }

            await using (var audits = new NpgsqlCommand(
                "DELETE FROM platform.audit_logs WHERE org_id=ANY(@organization_ids)", connection, transaction))
            {
                audits.Parameters.Add(new NpgsqlParameter<Guid[]>("organization_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = organizationIds });
                await audits.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        await using var cleanup = fixture.AdminDataSource.CreateCommand(
            """
            DELETE FROM clients.client_accounts WHERE owner_org_id=ANY(@organization_ids);
            DELETE FROM organizations.organizations WHERE id=ANY(@organization_ids);
            DELETE FROM identity.users WHERE id=ANY(@actor_ids);
            """);
        cleanup.Parameters.Add(new NpgsqlParameter<Guid[]>("organization_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = organizationIds });
        cleanup.Parameters.Add(new NpgsqlParameter<Guid[]>("actor_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = actorIds });
        await cleanup.ExecuteNonQueryAsync();
    }

    private async Task AssertAtomicCountsAsync(Guid clientId, Guid auditId, int expected)
    {
        Assert.Equal(expected, await AdminCountAsync("clients.client_accounts", "id", clientId));
        Assert.Equal(expected, await AdminCountAsync("platform.audit_logs", "id", auditId));
    }

    private async Task<int> AdminCountAsync(string table, string column, Guid id)
    {
        await using var command = fixture.AdminDataSource.CreateCommand($"SELECT count(*)::integer FROM {table} WHERE {column}=@id");
        command.Parameters.Add(P("id", id));
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task InsertClientAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid clientId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO clients.client_accounts(id,owner_org_id,name,status,created_at) VALUES (@id,@org_id,'Synthetic Audited Client','ACTIVE',@created_at)",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = clientId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("org_id", NpgsqlDbType.Uuid) { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at", NpgsqlDbType.TimestampTz) { TypedValue = DateTimeOffset.UtcNow });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static AuditEntry Entry(Guid auditId, Guid organizationId, Guid actorId, string requestId) => new(
        auditId,
        organizationId,
        actorId,
        "SYNTHETIC_SENSITIVE_ACTION",
        "CLIENT_ACCOUNT",
        organizationId,
        requestId,
        RedactedAuditPayload.Empty,
        DateTimeOffset.UtcNow);

    private static NpgsqlParameter P(string name, object value) => new(name, value);

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
                throw new NpgsqlException("Synthetic transient audit failure.", new TimeoutException());
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

    private sealed class SyntheticAuditActionException : Exception;

    private enum FailurePoint
    {
        None,
        BeforeAudit,
        AfterAudit,
        CancelAfterAudit,
    }
}
