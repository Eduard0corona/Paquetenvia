using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Paqueteria.Application.Scaling;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.DataProtection;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// SCL-001 acceptance: two API replicas serve interchangeably without losing protected state,
/// two Worker replicas never duplicate effects, and a node restart never loses a confirmed job.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class Scl001StatelessDistributedContractTests(PostgreSqlContractFixture fixture)
{
    private const string Purpose = "scl001.session";

    [PostgreSqlContractFact]
    public async Task Two_api_replicas_protect_and_unprotect_each_other_payloads()
    {
        var applicationName = $"scl001-{Guid.NewGuid():N}";
        await using var replicaA = CreateReplica(applicationName);
        await using var replicaB = CreateReplica(applicationName);

        var protectedByA = Protector(replicaA).Protect("session-issued-by-replica-a");
        var protectedByB = Protector(replicaB).Protect("session-issued-by-replica-b");

        // Either replica can serve a request the other one started.
        Assert.Equal("session-issued-by-replica-a", Protector(replicaB).Unprotect(protectedByA));
        Assert.Equal("session-issued-by-replica-b", Protector(replicaA).Unprotect(protectedByB));
        Assert.NotEqual(protectedByA, protectedByB);
        Assert.True(await CountKeysAsync(applicationName) >= 1);
    }

    [PostgreSqlContractFact]
    public async Task A_restarted_replica_still_reads_payloads_protected_before_the_restart()
    {
        var applicationName = $"scl001-{Guid.NewGuid():N}";
        string payload;
        await using (var before = CreateReplica(applicationName))
        {
            payload = Protector(before).Protect("survives-the-restart");
        }

        // A brand new process with no local key material resolves the ring from PostgreSQL.
        await using var after = CreateReplica(applicationName);
        Assert.Equal("survives-the-restart", Protector(after).Unprotect(payload));
    }

    [PostgreSqlContractFact]
    public async Task An_api_replica_and_a_worker_replica_share_one_key_ring()
    {
        var applicationName = $"scl001-{Guid.NewGuid():N}";
        await using var api = CreateReplica(applicationName, DataProtectionRuntimeRoles.Application);
        await using var worker = CreateReplica(applicationName, DataProtectionRuntimeRoles.Worker);

        var protectedByApi = Protector(api).Protect("handed-to-the-worker");
        var protectedByWorker = Protector(worker).Protect("handed-to-the-api");

        Assert.Equal("handed-to-the-worker", Protector(worker).Unprotect(protectedByApi));
        Assert.Equal("handed-to-the-api", Protector(api).Unprotect(protectedByWorker));
    }

    [PostgreSqlContractFact]
    public async Task Replicas_of_different_deployments_never_share_a_key_ring()
    {
        await using var first = CreateReplica($"scl001-{Guid.NewGuid():N}");
        await using var second = CreateReplica($"scl001-{Guid.NewGuid():N}");

        var protectedByFirst = Protector(first).Protect("deployment-scoped");

        Assert.Throws<CryptographicException>(() => Protector(second).Unprotect(protectedByFirst));
        Assert.Equal(1, await CountKeysAsync(ApplicationNameOf(first)));
    }

    [PostgreSqlContractFact]
    public async Task Runtime_roles_may_publish_keys_but_never_rewrite_another_replica_key()
    {
        var applicationName = $"scl001-{Guid.NewGuid():N}";
        await using var replica = CreateReplica(applicationName);
        Protector(replica).Protect("append-only");
        Assert.True(await CountKeysAsync(applicationName) >= 1);

        foreach (var role in new[] { "paqueteria_app", "paqueteria_worker" })
        {
            var dataSource = role == "paqueteria_app" ? fixture.AppDataSource : fixture.WorkerDataSource;
            foreach (var statement in new[]
            {
                "UPDATE platform.data_protection_keys SET xml='<tampered/>'",
                "DELETE FROM platform.data_protection_keys",
            })
            {
                await using var connection = await dataSource.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await using (var roleCommand = new NpgsqlCommand($"SET LOCAL ROLE {role}", connection, transaction))
                {
                    await roleCommand.ExecuteNonQueryAsync();
                }

                await using var command = new NpgsqlCommand(statement, connection, transaction);
                var exception = await Assert.ThrowsAsync<PostgresException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
                await transaction.RollbackAsync();
            }
        }

        Assert.True(await CountKeysAsync(applicationName) >= 1);
    }

    [PostgreSqlContractFact]
    public async Task Two_worker_replicas_never_claim_the_same_outbox_event()
    {
        var org = Guid.NewGuid();
        await InsertOrganizationAsync(org);
        var events = Enumerable.Range(0, 24).Select(_ => Guid.NewGuid()).ToArray();
        try
        {
            foreach (var id in events)
            {
                await InsertUnownedAsync(org, id);
            }

            // Both replicas poll the same lane at the same time, exactly as two Workers would.
            var replicaA = ClaimRepeatedlyAsync("ntf001:replica-a", events);
            var replicaB = ClaimRepeatedlyAsync("ntf001:replica-b", events);
            var claimed = (await Task.WhenAll(replicaA, replicaB))
                .SelectMany(batch => batch)
                .ToArray();

            Assert.Equal(events.Length, claimed.Length);
            Assert.Equal(events.Length, claimed.Select(claim => claim.Id).Distinct().Count());
            Assert.Equal(events.Length, claimed.Select(claim => claim.LeaseToken).Distinct().Count());
            Assert.All(claimed, claim => Assert.Equal(1, claim.Attempts));
            Assert.All(claimed, claim => Assert.Equal("PROCESSING", claim.Status));

            // Only the lease holder settles; a competing replica's token is refused.
            var first = claimed[0];
            Assert.False(await SettleAsync(first.Id, Guid.NewGuid()));
            Assert.True(await SettleAsync(first.Id, first.LeaseToken));
            Assert.False(await SettleAsync(first.Id, first.LeaseToken));
            Assert.Equal("PROCESSED", await StatusAsync(first.Id));
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [PostgreSqlContractFact]
    public async Task A_job_confirmed_before_a_node_restart_is_processed_exactly_once_afterwards()
    {
        var org = Guid.NewGuid();
        await InsertOrganizationAsync(org);
        var id = Guid.NewGuid();
        try
        {
            await InsertUnownedAsync(org, id);

            // The replica claims the job and then dies without settling it.
            var lost = Assert.Single(await ClaimAsync("ntf001:replica-before-restart", 10, events: [id]));
            Assert.Equal("PROCESSING", lost.Status);
            await ExecuteAdminAsync(
                "UPDATE platform.outbox_events SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id=@id",
                P("id", id));

            // The job is still confirmed, so a surviving replica recovers and completes it.
            Assert.False(await SettleAsync(id, lost.LeaseToken));
            Assert.True(await RequeueStaleAsync() >= 1);
            Assert.Equal("RETRY", await StatusAsync(id));

            var recovered = Assert.Single(await ClaimAsync("ntf001:replica-after-restart", 10, events: [id]));
            Assert.NotEqual(lost.LeaseToken, recovered.LeaseToken);
            Assert.Equal(2, recovered.Attempts);
            Assert.True(await SettleAsync(id, recovered.LeaseToken));
            Assert.Equal("PROCESSED", await StatusAsync(id));

            // The replica that lost the race can never re-apply its effect.
            Assert.False(await SettleAsync(id, lost.LeaseToken));
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [PostgreSqlContractFact]
    public async Task Worker_identity_is_qualified_per_replica_without_losing_the_configured_lane()
    {
        var qualified = InstanceIdentity.QualifyWorkerId("ntf001-local");

        Assert.StartsWith("ntf001-local:", qualified, StringComparison.Ordinal);
        Assert.Equal(qualified, InstanceIdentity.QualifyWorkerId("ntf001-local"));
        Assert.True(qualified.Length <= InstanceIdentity.MaximumWorkerIdLength);
        Assert.True(
            InstanceIdentity.QualifyWorkerId(new string('x', 250)).Length
                <= InstanceIdentity.MaximumWorkerIdLength);
        await Task.CompletedTask;
    }

    private ServiceProvider CreateReplica(
        string applicationName,
        string runtimeRole = DataProtectionRuntimeRoles.Application)
    {
        var connectionString = runtimeRole == DataProtectionRuntimeRoles.Worker
            ? fixture.WorkerConnectionString
            : fixture.AppConnectionString;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:Provider"] = "PostgreSql",
                ["DataProtection:ApplicationName"] = applicationName,
                ["DataProtection:ConnectionStringName"] = "Paqueteria",
                ["DataProtection:RuntimeRole"] = runtimeRole,
                ["DataProtection:KeyLifetimeDays"] = "90",
                ["DataProtection:CommandTimeoutSeconds"] = "15",
                ["ConnectionStrings:Paqueteria"] = connectionString,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPlatformDataProtection(configuration);
        return services.BuildServiceProvider();
    }

    private static string ApplicationNameOf(IServiceProvider replica) =>
        replica.GetRequiredService<IConfiguration>()["DataProtection:ApplicationName"]!;

    private static IDataProtector Protector(IServiceProvider replica) =>
        replica.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    private async Task<int> CountKeysAsync(string applicationName)
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::integer FROM platform.data_protection_keys WHERE application_name=@name",
            connection);
        command.Parameters.Add(P("name", applicationName));
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task<IReadOnlyList<Claim>> ClaimRepeatedlyAsync(string workerId, Guid[] events)
    {
        var claims = new List<Claim>();
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var batch = await ClaimAsync(workerId, 4, events);
            if (batch.Count == 0 && claims.Count > 0)
            {
                break;
            }

            claims.AddRange(batch);
        }

        return claims;
    }

    private async Task<IReadOnlyList<Claim>> ClaimAsync(
        string workerId,
        int batchSize,
        Guid[]? events = null)
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT id,status,attempts,locked_by,lease_token
            FROM security.claim_unowned_outbox(@worker,@batch,interval '30 seconds')
            """,
            connection,
            transaction);
        command.Parameters.Add(P("worker", workerId));
        command.Parameters.Add(P("batch", batchSize));
        var claims = new List<Claim>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                claims.Add(new Claim(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetGuid(4)));
            }
        }

        await transaction.CommitAsync();
        return events is null
            ? claims
            : claims.Where(claim => events.Contains(claim.Id)).ToList();
    }

    private async Task<bool> SettleAsync(Guid id, Guid leaseToken) =>
        await WorkerScalarAsync<bool>(
            "SELECT security.settle_outbox(@id,@token,'PROCESSED',NULL,NULL)",
            P("id", id),
            P("token", leaseToken));

    private async Task<int> RequeueStaleAsync() =>
        await WorkerScalarAsync<int>(
            "SELECT security.requeue_stale_unowned_outbox(interval '0 seconds',100,10)");

    private async Task<string> StatusAsync(Guid id)
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM platform.outbox_events WHERE id=@id",
            connection);
        command.Parameters.Add(P("id", id));
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<T> WorkerScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        var value = (T)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return value;
    }

    private async Task InsertOrganizationAsync(Guid org) => await ExecuteAdminAsync(
        """
        INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
        VALUES (@id,'SCL-001 Synthetic','SCL-001 Synthetic','BUSINESS')
        """,
        P("id", org));

    private async Task InsertUnownedAsync(Guid org, Guid id)
    {
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            Guid.NewGuid(),
            [org]);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
              payload,priority,status,attempts,available_at,created_at)
            VALUES (@id,@org,'{}','scl001.unrouted','Order',@aggregate,1,'{}',50,'PENDING',0,
              clock_timestamp()-interval '1 minute',clock_timestamp()-interval '1 minute')
            """,
            tenant.Connection,
            tenant.Transaction);
        command.Parameters.Add(P("id", id));
        command.Parameters.Add(P("org", org));
        command.Parameters.Add(P("aggregate", Guid.NewGuid()));
        await command.ExecuteNonQueryAsync();
        await tenant.CommitAsync();
    }

    private async Task CleanupAsync(Guid org)
    {
        await ExecuteAdminAsync(
            "DELETE FROM platform.outbox_events WHERE owner_org_id=@org",
            P("org", org));
        await ExecuteAdminAsync(
            "DELETE FROM organizations.organizations WHERE id=@org",
            P("org", org));
    }

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlParameter P(string name, object value) => new(name, value);

    private sealed record Claim(Guid Id, string Status, int Attempts, string LockedBy, Guid LeaseToken);
}
