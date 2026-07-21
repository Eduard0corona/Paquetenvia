using System.Text.Json;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.ContractTests.PostgreSql.Fixtures;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class RlsAndProvisioningContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Tenant_context_is_parameterized_transaction_local_and_fail_closed_with_pooling()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var user = Guid.NewGuid();
        await ExecuteAdminAsync("""
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES
              (@org_a,'Synthetic A','Organization A','BUSINESS'),(@org_b,'Synthetic B','Organization B','BUSINESS');
            INSERT INTO clients.client_accounts(id,owner_org_id,name) VALUES
              (@client_a,@org_a,'Client A'),(@client_b,@org_b,'Client B');
            """,
            P("org_a", orgA), P("org_b", orgB), P("client_a", clientA), P("client_b", clientB));

        try
        {
            Assert.Equal(["Client A"], await ReadClientNamesAsync(user, [orgA]));
            Assert.Equal(["Client B"], await ReadClientNamesAsync(user, [orgB]));
            Assert.Equal(["Client A", "Client B"], await ReadClientNamesAsync(user, [orgA, orgB]));
            Assert.Empty(await ReadClientNamesAsync(user, []));

            await using (var noContextConnection = await fixture.AppDataSource.OpenConnectionAsync())
            await using (var noContextTransaction = await noContextConnection.BeginTransactionAsync())
            {
                await ExecuteAsync(noContextConnection, noContextTransaction, "SET LOCAL ROLE paqueteria_app");
                Assert.Equal(0, await ScalarAsync<int>(noContextConnection, noContextTransaction, "SELECT count(*)::integer FROM clients.client_accounts"));
            }

            await using (var tenantA = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", user, [orgA]))
            {
                Assert.Equal(0, await ExecuteAsync(tenantA.Connection, tenantA.Transaction,
                    "UPDATE clients.client_accounts SET name='forbidden' WHERE id=@id", P("id", clientB)));
                Assert.Equal(0, await ExecuteAsync(tenantA.Connection, tenantA.Transaction,
                    "DELETE FROM clients.client_accounts WHERE id=@id", P("id", clientB)));
                var insertException = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                    tenantA.Connection,
                    tenantA.Transaction,
                    "INSERT INTO clients.client_accounts(id,owner_org_id,name) VALUES (@id,@org,'cross tenant')",
                    P("id", Guid.NewGuid()), P("org", orgB)));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insertException.SqlState);
            }

            await AssertContextDoesNotLeakAfterCompletionAsync(user, orgA, commit: true);
            await AssertContextDoesNotLeakAfterCompletionAsync(user, orgB, commit: false);

            await Assert.ThrowsAsync<ArgumentNullException>(() => TenantTransaction.BeginAsync(
                fixture.AppDataSource,
                "paqueteria_app",
                user,
                null!));

            await using var malformedConnection = await fixture.AppDataSource.OpenConnectionAsync();
            await using var malformedTransaction = await malformedConnection.BeginTransactionAsync();
            await ExecuteAsync(malformedConnection, malformedTransaction, "SET LOCAL ROLE paqueteria_app");
            await using var malformed = new NpgsqlCommand(
                "SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true)",
                malformedConnection,
                malformedTransaction);
            malformed.Parameters.Add(new NpgsqlParameter("organization_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                Value = new[] { "not-a-uuid" },
            });
            await Assert.ThrowsAnyAsync<Exception>(() => malformed.ExecuteNonQueryAsync());
        }
        finally
        {
            await ExecuteAdminAsync(
                "DELETE FROM clients.client_accounts WHERE id IN (@client_a,@client_b); DELETE FROM organizations.organizations WHERE id IN (@org_a,@org_b)",
                P("client_a", clientA), P("client_b", clientB), P("org_a", orgA), P("org_b", orgB));
        }
    }

    [PostgreSqlContractFact]
    public async Task Bootstrap_resolution_returns_only_active_identity_memberships()
    {
        var user = Guid.NewGuid();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var subject = $"oidc|arc002|{Guid.NewGuid():N}";
        await ExecuteAdminAsync("""
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES
              (@org_a,'Synthetic A','Organization A','BUSINESS'),(@org_b,'Synthetic B','Organization B','ALLY');
            INSERT INTO identity.users(id,identity_subject) VALUES (@user_id,@subject);
            INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default) VALUES
              (@membership_a,@user_id,@org_a,'BUSINESS_ADMIN','ACTIVE',true),
              (@membership_b,@user_id,@org_b,'VIEWER','ACTIVE',false),
              (@membership_suspended,@user_id,@org_b,'ALLY_OPERATOR','SUSPENDED',false);
            """,
            P("org_a", orgA), P("org_b", orgB), P("user_id", user), P("subject", subject),
            P("membership_a", Guid.NewGuid()), P("membership_b", Guid.NewGuid()), P("membership_suspended", Guid.NewGuid()));

        try
        {
            await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_app");
            var json = await ScalarAsync<string>(connection, transaction,
                "SELECT security.resolve_identity_context(@subject)::text", P("subject", subject));
            using var result = JsonDocument.Parse(json);
            Assert.Equal(user, result.RootElement.GetProperty("user_id").GetGuid());
            var memberships = result.RootElement.GetProperty("memberships").EnumerateArray().ToArray();
            Assert.Equal(2, memberships.Length);
            Assert.Equal(orgA, memberships[0].GetProperty("organization_id").GetGuid());
            Assert.True(memberships[0].GetProperty("is_default").GetBoolean());
            Assert.Contains(memberships, membership => membership.GetProperty("organization_id").GetGuid() == orgB);
            Assert.Equal("null", await ScalarAsync<string>(connection, transaction,
                "SELECT COALESCE(security.resolve_identity_context('missing-subject')::text,'null')"));
            Assert.Equal(0, await ScalarAsync<int>(connection, transaction, "SELECT count(*)::integer FROM identity.users"));
        }
        finally
        {
            await ExecuteAdminAsync(
                "DELETE FROM organizations.organization_memberships WHERE user_id=@user_id; DELETE FROM identity.users WHERE id=@user_id; DELETE FROM organizations.organizations WHERE id IN (@org_a,@org_b)",
                P("user_id", user), P("org_a", orgA), P("org_b", orgB));
        }
    }

    [PostgreSqlContractFact]
    public async Task First_user_and_organization_provisioning_are_preauthorized_and_atomic()
    {
        var user = Guid.NewGuid();
        var org = Guid.NewGuid();
        var membership = Guid.NewGuid();
        var audit = Guid.NewGuid();
        var subject = $"oidc|provisioned|{Guid.NewGuid():N}";
        try
        {
            await using (var firstUser = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", user, []))
            {
                Assert.Equal("{}", await ScalarAsync<string>(firstUser.Connection, firstUser.Transaction,
                    "SELECT current_setting('app.current_org_ids',true)"));
                Assert.Equal(1, await ExecuteAsync(firstUser.Connection, firstUser.Transaction,
                    "INSERT INTO identity.users(id,identity_subject) VALUES (@id,@subject)", P("id", user), P("subject", subject)));
                await firstUser.CommitAsync();
            }

            await using (var provisioning = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", user, [org]))
            {
                await ExecuteAsync(provisioning.Connection, provisioning.Transaction, """
                    INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@org,'Synthetic Provisioned','Provisioned','BUSINESS');
                    INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,is_default) VALUES (@membership,@user_id,@org,'BUSINESS_ADMIN',true);
                    INSERT INTO platform.audit_logs(id,org_id,actor_id,action,entity_type,entity_id) VALUES (@audit,@org,@user_id,'ORGANIZATION_PROVISIONED','Organization',@org);
                    """, P("org", org), P("membership", membership), P("user_id", user), P("audit", audit));
                await provisioning.CommitAsync();
            }

            Assert.Equal(1, await AdminScalarAsync<int>("SELECT count(*)::integer FROM organizations.organizations WHERE id=@id", P("id", org)));
            Assert.Equal(1, await AdminScalarAsync<int>("SELECT count(*)::integer FROM organizations.organization_memberships WHERE id=@id", P("id", membership)));
            Assert.Equal(1, await AdminScalarAsync<int>("SELECT count(*)::integer FROM platform.audit_logs WHERE id=@id", P("id", audit)));

            var rolledBackOrg = Guid.NewGuid();
            await using (var rolledBack = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", user, [rolledBackOrg]))
            {
                await ExecuteAsync(rolledBack.Connection, rolledBack.Transaction,
                    "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@id,'Rollback','Rollback','BUSINESS')",
                    P("id", rolledBackOrg));
                await rolledBack.RollbackAsync();
            }
            Assert.Equal(0, await AdminScalarAsync<int>("SELECT count(*)::integer FROM organizations.organizations WHERE id=@id", P("id", rolledBackOrg)));

            var unauthorizedUser = Guid.NewGuid();
            await using var denied = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", user, []);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                denied.Connection,
                denied.Transaction,
                "INSERT INTO identity.users(id,identity_subject) VALUES (@id,'oidc|unauthorized')",
                P("id", unauthorizedUser)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }
        finally
        {
            await DeleteAuditAsMigratorAsync(user, org, audit);
            await ExecuteAdminAsync(
                "DELETE FROM organizations.organization_memberships WHERE id=@membership; DELETE FROM organizations.organizations WHERE id=@org; DELETE FROM identity.users WHERE id=@user_id",
                P("membership", membership), P("org", org), P("user_id", user));
        }
    }

    [PostgreSqlContractFact]
    public async Task Runtime_logins_cannot_assume_privileged_roles()
    {
        foreach (var role in new[] { "paqueteria_migrator", "paqueteria_bootstrap", "paqueteria_outbox_executor", "paqueteria_maintenance" })
        {
            await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"SET ROLE {role}", connection);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }
    }

    private async Task<string[]> ReadClientNamesAsync(Guid user, Guid[] organizations)
    {
        await using var tenant = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", user, organizations);
        await using var command = new NpgsqlCommand("SELECT name FROM clients.client_accounts ORDER BY name", tenant.Connection, tenant.Transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
    }

    private async Task AssertContextDoesNotLeakAfterCompletionAsync(Guid user, Guid organization, bool commit)
    {
        await using (var tenant = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", user, [organization]))
        {
            Assert.Contains(organization.ToString(), await ScalarAsync<string>(tenant.Connection, tenant.Transaction,
                "SELECT current_setting('app.current_org_ids',true)"), StringComparison.OrdinalIgnoreCase);
            if (commit)
            {
                await tenant.CommitAsync();
            }
            else
            {
                await tenant.RollbackAsync();
            }
        }

        await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_app");
        Assert.Equal(string.Empty, await ScalarAsync<string>(connection, transaction,
            "SELECT COALESCE(current_setting('app.current_org_ids',true),'')"));
    }

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteAuditAsMigratorAsync(Guid user, Guid organization, Guid audit)
    {
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AdminDataSource,
            "paqueteria_migrator",
            user,
            [organization]);
        await ExecuteAsync(tenant.Connection, tenant.Transaction,
            "DELETE FROM platform.audit_logs WHERE id=@id", P("id", audit));
        await tenant.CommitAsync();
    }

    private static NpgsqlParameter P(string name, object value) => new(name, value);

    private async Task<T> AdminScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
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

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        var result = await command.ExecuteScalarAsync();
        if (result is T typed)
        {
            return typed;
        }

        return (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
    }
}
