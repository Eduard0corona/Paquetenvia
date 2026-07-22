using System.Globalization;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class OutboxLifecycleContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Runtime_outbox_access_is_insert_only_without_returning_for_both_lanes()
    {
        var org = Guid.NewGuid();
        await InsertOrganizationAsync(org);
        try
        {
            foreach (var role in new[] { "paqueteria_app", "paqueteria_worker" })
            {
                var dataSource = role == "paqueteria_app" ? fixture.AppDataSource : fixture.WorkerDataSource;
                foreach (var table in new[] { "platform.outbox_events", "platform.location_outbox_events" })
                {
                    foreach (var statement in new[]
                    {
                        $"SELECT * FROM {table}",
                        $"UPDATE {table} SET last_error='forbidden'",
                        $"DELETE FROM {table}",
                    })
                    {
                        var exception = await ExecuteExpectingPostgresErrorAsync(dataSource, role, org, statement);
                        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
                    }
                }
            }

            var businessId = Guid.NewGuid();
            await using (var app = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", Guid.NewGuid(), [org]))
            {
                Assert.Equal(1, await ExecuteAsync(app.Connection, app.Transaction, BusinessInsertSql,
                    BusinessParameters(businessId, org, "PENDING", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
                await app.CommitAsync();
            }

            var locationId = Guid.NewGuid();
            var driverPositionId = Guid.NewGuid();
            await using (var worker = await TenantTransaction.BeginAsync(fixture.WorkerDataSource, "paqueteria_worker", Guid.NewGuid(), [org]))
            {
                Assert.Equal(1, await ExecuteAsync(worker.Connection, worker.Transaction, LocationInsertSql,
                    LocationParameters(locationId, org, driverPositionId, "PENDING", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
                await worker.CommitAsync();
            }

            await using (var returning = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", Guid.NewGuid(), [org]))
            {
                var returnException = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                    returning.Connection,
                    returning.Transaction,
                    BusinessInsertSql + " RETURNING id",
                    BusinessParameters(Guid.NewGuid(), org, "PENDING", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, returnException.SqlState);
            }

            await using (var returning = await TenantTransaction.BeginAsync(fixture.WorkerDataSource, "paqueteria_worker", Guid.NewGuid(), [org]))
            {
                var returnException = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                    returning.Connection,
                    returning.Transaction,
                    LocationInsertSql + " RETURNING id",
                    LocationParameters(Guid.NewGuid(), org, Guid.NewGuid(), "PENDING", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, returnException.SqlState);
            }
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [PostgreSqlContractFact]
    public async Task Business_outbox_claim_settle_requeue_and_purge_enforce_the_full_lease_lifecycle()
    {
        var org = Guid.NewGuid();
        await InsertOrganizationAsync(org);
        var pending = Guid.NewGuid();
        var retry = Guid.NewGuid();
        var future = Guid.NewGuid();
        var processed = Guid.NewGuid();
        var dead = Guid.NewGuid();
        try
        {
            await InsertBusinessAsProducerAsync(org, pending, "PENDING", DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-2));
            await InsertBusinessAsProducerAsync(org, retry, "RETRY", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(-1));
            await InsertBusinessAsProducerAsync(org, future, "PENDING", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow);
            await InsertBusinessAsProducerAsync(org, processed, "PROCESSED", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-10));
            await InsertBusinessAsProducerAsync(org, dead, "DEAD", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-10));

            var claims = await ClaimAsync(location: false, batchSize: 2, workerId: "arc002-worker-a");
            Assert.Equal(2, claims.Count);
            Assert.Equal(new[] { pending, retry }.Order(), claims.Select(claim => claim.Id).Order());
            Assert.All(claims, claim =>
            {
                Assert.NotEqual(Guid.Empty, claim.LeaseToken);
                Assert.NotNull(claim.LeaseExpiresAt);
                Assert.Equal("PROCESSING", claim.Status);
                Assert.Equal("arc002-worker-a", claim.LockedBy);
                Assert.Equal(1, claim.Attempts);
            });
            Assert.DoesNotContain(future, claims.Select(claim => claim.Id));
            Assert.DoesNotContain(processed, claims.Select(claim => claim.Id));
            Assert.DoesNotContain(dead, claims.Select(claim => claim.Id));

            var first = claims[0];
            Assert.False(await SettleAsync(location: false, first.Id, Guid.NewGuid(), "PROCESSED"));
            Assert.True(await SettleAsync(location: false, first.Id, first.LeaseToken, "PROCESSED"));
            Assert.False(await SettleAsync(location: false, first.Id, first.LeaseToken, "PROCESSED"));
            Assert.Equal("PROCESSED", await BusinessStatusAsync(first.Id));

            var stale = claims[1];
            await ExecuteAdminAsync("UPDATE platform.outbox_events SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id=@id", P("id", stale.Id));
            Assert.False(await SettleAsync(location: false, stale.Id, stale.LeaseToken, "PROCESSED"));
            Assert.Equal(1, await RequeueAsync(location: false, maxAttempts: 10));
            Assert.Equal("RETRY", await BusinessStatusAsync(stale.Id));

            var poison = Guid.NewGuid();
            await ExecuteAdminAsync("""
                INSERT INTO platform.outbox_events(id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,payload,priority,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,created_at)
                VALUES (@id,@org,'{}','arc002.poison','Order',@aggregate,'{}',1,'PROCESSING',10,clock_timestamp(),clock_timestamp()-interval '1 minute','dead-worker',@lease,clock_timestamp()-interval '1 second',clock_timestamp()-interval '1 day')
                """, P("id", poison), P("org", org), P("aggregate", Guid.NewGuid()), P("lease", Guid.NewGuid()));
            Assert.Equal(1, await RequeueAsync(location: false, maxAttempts: 10));
            Assert.Equal("DEAD", await BusinessStatusAsync(poison));

            var activeProcessing = Guid.NewGuid();
            await ExecuteAdminAsync("""
                INSERT INTO platform.outbox_events(id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,payload,priority,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,created_at)
                VALUES (@id,@org,'{}','arc002.active','Order',@aggregate,'{}',1,'PROCESSING',1,clock_timestamp(),clock_timestamp(),'active-worker',@lease,clock_timestamp()+interval '1 hour',clock_timestamp()-interval '10 days')
                """, P("id", activeProcessing), P("org", org), P("aggregate", Guid.NewGuid()), P("lease", Guid.NewGuid()));

            var dryRun = await PurgeAsync(location: false, dryRun: true, batchSize: 2);
            Assert.Equal(2, dryRun);
            Assert.Equal(2, await AdminScalarAsync<int>(
                "SELECT count(*)::integer FROM platform.outbox_events WHERE id IN (@processed,@dead)",
                P("processed", processed), P("dead", dead)));
            var maintenanceProbe = Guid.NewGuid();
            await InsertBusinessAsProducerAsync(org, maintenanceProbe, "PROCESSED", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-10));
            Assert.Equal(1, await DeleteDirectlyAsMaintenanceAsync("platform.outbox_events", maintenanceProbe));
            Assert.Equal(1, await PurgeAsync(location: false, dryRun: false, batchSize: 1));
            Assert.Equal(1, await AdminScalarAsync<int>(
                "SELECT count(*)::integer FROM platform.outbox_events WHERE id IN (@processed,@dead)",
                P("processed", processed), P("dead", dead)));
            Assert.Equal(1, await PurgeAsync(location: false, dryRun: false, batchSize: 1));
            Assert.Equal(0, await PurgeAsync(location: false, dryRun: false, batchSize: 100));
            Assert.Equal(0, await AdminScalarAsync<int>(
                "SELECT count(*)::integer FROM platform.outbox_events WHERE id IN (@processed,@dead)",
                P("processed", processed), P("dead", dead)));
            Assert.Equal(1, await AdminScalarAsync<int>("SELECT count(*)::integer FROM platform.outbox_events WHERE id=@future", P("future", future)));
            Assert.Equal("PROCESSED", await BusinessStatusAsync(first.Id));
            Assert.Equal("RETRY", await BusinessStatusAsync(stale.Id));
            Assert.Equal("DEAD", await BusinessStatusAsync(poison));
            Assert.Equal("PROCESSING", await BusinessStatusAsync(activeProcessing));

            var cutoffException = await Assert.ThrowsAsync<PostgresException>(() => PurgeAsync(location: false, dryRun: true, batchSize: 100, invalidCutoff: true));
            Assert.Equal(PostgresErrorCodes.InvalidParameterValue, cutoffException.SqlState);
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [PostgreSqlContractFact]
    public async Task Location_outbox_lifecycle_is_symmetric_and_driver_position_is_unique_without_fk()
    {
        var org = Guid.NewGuid();
        await InsertOrganizationAsync(org);
        var first = Guid.NewGuid();
        var duplicatePosition = Guid.NewGuid();
        var oldProcessed = Guid.NewGuid();
        var activePending = Guid.NewGuid();
        var activeRetry = Guid.NewGuid();
        var activeProcessing = Guid.NewGuid();
        var recentProcessed = Guid.NewGuid();
        var recentDead = Guid.NewGuid();
        try
        {
            await InsertLocationAsProducerAsync(org, first, duplicatePosition, "PENDING", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(-10));
            var duplicateException = await Assert.ThrowsAsync<PostgresException>(() => InsertLocationAsProducerAsync(
                org, Guid.NewGuid(), duplicatePosition, "PENDING", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateException.SqlState);

            Assert.False(await AdminScalarAsync<bool>("""
                SELECT EXISTS (
                  SELECT 1 FROM pg_constraint c
                  JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace
                  WHERE n.nspname='platform' AND t.relname='location_outbox_events' AND c.contype='f'
                    AND pg_get_constraintdef(c.oid) LIKE '%driver_position_id%'
                )
                """));

            var claim = Assert.Single(await ClaimAsync(location: true, batchSize: 1, workerId: "arc002-location-worker"));
            Assert.Equal(first, claim.Id);
            Assert.False(await SettleAsync(location: true, claim.Id, Guid.NewGuid(), "PROCESSED"));
            Assert.True(await SettleAsync(location: true, claim.Id, claim.LeaseToken, "RETRY", DateTimeOffset.UtcNow.AddSeconds(-1)));
            var retryClaim = Assert.Single(await ClaimAsync(location: true, batchSize: 1, workerId: "arc002-location-worker-2"));
            await ExecuteAdminAsync("UPDATE platform.location_outbox_events SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id=@id", P("id", retryClaim.Id));
            Assert.Equal(1, await RequeueAsync(location: true, maxAttempts: 2));
            Assert.Equal("DEAD", await LocationStatusAsync(first));

            await ExecuteAdminAsync("UPDATE platform.location_outbox_events SET processed_at=clock_timestamp()-interval '10 days' WHERE id=@id", P("id", first));
            await InsertLocationAsProducerAsync(org, oldProcessed, Guid.NewGuid(), "PROCESSED", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-10));
            await InsertLocationAsProducerAsync(org, activePending, Guid.NewGuid(), "PENDING", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-10));
            await InsertLocationAsProducerAsync(org, activeRetry, Guid.NewGuid(), "RETRY", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-10));
            await InsertLocationAsProducerAsync(org, recentProcessed, Guid.NewGuid(), "PROCESSED", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await InsertLocationAsProducerAsync(org, recentDead, Guid.NewGuid(), "DEAD", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await ExecuteAdminAsync("""
                INSERT INTO platform.location_outbox_events(id,owner_org_id,driver_position_id,topic,payload,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,created_at)
                VALUES (@id,@org,@position,'arc002.location.active','{}','PROCESSING',1,clock_timestamp(),clock_timestamp(),'active-location-worker',@lease,clock_timestamp()+interval '1 hour',clock_timestamp()-interval '10 days')
                """, P("id", activeProcessing), P("org", org), P("position", Guid.NewGuid()), P("lease", Guid.NewGuid()));

            Assert.Equal(1, await PurgeAsync(location: true, dryRun: true, batchSize: 1));
            Assert.Equal(2, await AdminScalarAsync<int>(
                "SELECT count(*)::integer FROM platform.location_outbox_events WHERE id IN (@dead,@processed)",
                P("dead", first), P("processed", oldProcessed)));
            Assert.Equal(1, await PurgeAsync(location: true, dryRun: false, batchSize: 1));
            Assert.Equal(1, await PurgeAsync(location: true, dryRun: false, batchSize: 1));
            Assert.Equal(0, await PurgeAsync(location: true, dryRun: false, batchSize: 100));
            Assert.Equal(0, await AdminScalarAsync<int>(
                "SELECT count(*)::integer FROM platform.location_outbox_events WHERE id IN (@dead,@processed)",
                P("dead", first), P("processed", oldProcessed)));
            Assert.Equal(5, await AdminScalarAsync<int>("""
                SELECT count(*)::integer FROM platform.location_outbox_events
                WHERE id IN (@pending,@retry,@processing,@recent_processed,@recent_dead)
                """, P("pending", activePending), P("retry", activeRetry), P("processing", activeProcessing),
                P("recent_processed", recentProcessed), P("recent_dead", recentDead)));

            var cutoffException = await Assert.ThrowsAsync<PostgresException>(() => PurgeAsync(location: true, dryRun: true, batchSize: 100, invalidCutoff: true));
            Assert.Equal(PostgresErrorCodes.InvalidParameterValue, cutoffException.SqlState);
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [PostgreSqlContractFact]
    public async Task Two_worker_connections_never_claim_the_same_message()
    {
        var org = Guid.NewGuid();
        var id = Guid.NewGuid();
        await InsertOrganizationAsync(org);
        try
        {
            await InsertBusinessAsProducerAsync(org, id, "PENDING", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
            var firstTask = ClaimAsync(location: false, batchSize: 1, workerId: "concurrent-a");
            var secondTask = ClaimAsync(location: false, batchSize: 1, workerId: "concurrent-b");
            var results = (await Task.WhenAll(firstTask, secondTask)).SelectMany(value => value).ToArray();
            var claim = Assert.Single(results);
            Assert.Equal(id, claim.Id);
            Assert.Contains(claim.LockedBy, new[] { "concurrent-a", "concurrent-b" });
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [PostgreSqlContractFact]
    public async Task Two_worker_connections_purge_each_eligible_row_once_without_leaking_locks()
    {
        var org = Guid.NewGuid();
        var eligible = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        var active = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        await InsertOrganizationAsync(org);
        try
        {
            for (var index = 0; index < eligible.Length; index++)
            {
                var status = index % 2 == 0 ? "PROCESSED" : "DEAD";
                await InsertBusinessAsProducerAsync(
                    org,
                    eligible[index],
                    status,
                    DateTimeOffset.UtcNow.AddDays(-10),
                    DateTimeOffset.UtcNow.AddDays(-10),
                    DateTimeOffset.UtcNow.AddDays(-10));
            }

            await InsertBusinessAsProducerAsync(org, active[0], "PENDING", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-10));
            await InsertBusinessAsProducerAsync(org, active[1], "RETRY", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-10));
            await ExecuteAdminAsync("""
                INSERT INTO platform.outbox_events(id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,payload,priority,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,created_at)
                VALUES (@id,@org,'{}','arc002.concurrent-active','Order',@aggregate,'{}',1,'PROCESSING',1,clock_timestamp(),clock_timestamp(),'concurrent-active-worker',@lease,clock_timestamp()+interval '1 hour',clock_timestamp()-interval '10 days')
                """, P("id", active[2]), P("org", org), P("aggregate", Guid.NewGuid()), P("lease", Guid.NewGuid()));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = PurgeOnPreparedConnectionAsync(firstReady, start, timeout.Token);
            var second = PurgeOnPreparedConnectionAsync(secondReady, start, timeout.Token);

            try
            {
                await Task.WhenAll(firstReady.Task, secondReady.Task).WaitAsync(timeout.Token);
                start.SetResult();
                var purgeResults = await Task.WhenAll(first, second).WaitAsync(timeout.Token);
                Assert.All(purgeResults, result => Assert.InRange(result.Deleted, 0, eligible.Length));
                Assert.Equal(eligible.Length, purgeResults.Sum(result => result.Deleted));
                Assert.Equal(0, await AdminScalarAsync<int>(
                    "SELECT count(*)::integer FROM pg_locks WHERE pid=ANY(@pids) AND granted",
                    new NpgsqlParameter<int[]>("pids", purgeResults.Select(result => result.ProcessId).ToArray())));
            }
            catch (Exception exception)
            {
                var diagnostics = await fixture.GetContainerDiagnosticsAsync();
                throw new InvalidOperationException(
                    $"Concurrent purge failed or timed out. Eligible IDs: {string.Join(',', eligible)}.{Environment.NewLine}{diagnostics}",
                    exception);
            }

            Assert.Equal(0, await AdminScalarAsync<int>(
                "SELECT count(*)::integer FROM platform.outbox_events WHERE id=ANY(@ids)",
                new NpgsqlParameter<Guid[]>("ids", eligible)));
            Assert.Equal(active.Length, await AdminScalarAsync<int>(
                "SELECT count(*)::integer FROM platform.outbox_events WHERE id=ANY(@ids)",
                new NpgsqlParameter<Guid[]>("ids", active)));
            Assert.Equal(0, await AdminScalarAsync<int>("""
                SELECT count(*)::integer FROM pg_stat_activity
                WHERE datname=current_database() AND state='idle in transaction'
                """));
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    private const string BusinessInsertSql = """
        INSERT INTO platform.outbox_events(id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,priority,status,attempts,available_at,created_at,processed_at)
        VALUES (@id,@org,'{}','arc002.business','Order',@aggregate,1,'{}',50,@status,0,@available,@created,@processed)
        """;

    private const string LocationInsertSql = """
        INSERT INTO platform.location_outbox_events(id,owner_org_id,driver_position_id,topic,payload,status,attempts,available_at,created_at,processed_at)
        VALUES (@id,@org,@position,'arc002.location','{}',@status,0,@available,@created,@processed)
        """;

    private async Task InsertOrganizationAsync(Guid org) => await ExecuteAdminAsync(
        "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@id,'ARC-002 Synthetic','ARC-002 Synthetic','BUSINESS')",
        P("id", org));

    private async Task InsertBusinessAsProducerAsync(Guid org, Guid id, string status, DateTimeOffset available, DateTimeOffset created, DateTimeOffset? processed = null)
    {
        await using var tenant = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", Guid.NewGuid(), [org]);
        await ExecuteAsync(tenant.Connection, tenant.Transaction, BusinessInsertSql, BusinessParameters(id, org, status, available, created, processed));
        await tenant.CommitAsync();
    }

    private async Task InsertLocationAsProducerAsync(
        Guid org,
        Guid id,
        Guid position,
        string status,
        DateTimeOffset available,
        DateTimeOffset created,
        DateTimeOffset? processed = null)
    {
        await using var tenant = await TenantTransaction.BeginAsync(fixture.WorkerDataSource, "paqueteria_worker", Guid.NewGuid(), [org]);
        await ExecuteAsync(tenant.Connection, tenant.Transaction, LocationInsertSql, LocationParameters(id, org, position, status, available, created, processed));
        await tenant.CommitAsync();
    }

    private static NpgsqlParameter[] BusinessParameters(Guid id, Guid org, string status, DateTimeOffset available, DateTimeOffset created, DateTimeOffset? processed = null) =>
    [
        P("id", id), P("org", org), P("aggregate", Guid.NewGuid()), P("status", status),
        P("available", available), P("created", created), P("processed", processed ?? (object)DBNull.Value),
    ];

    private static NpgsqlParameter[] LocationParameters(
        Guid id,
        Guid org,
        Guid position,
        string status,
        DateTimeOffset available,
        DateTimeOffset created,
        DateTimeOffset? processed = null) =>
    [
        P("id", id), P("org", org), P("position", position), P("status", status),
        P("available", available), P("created", created), P("processed", processed ?? (object)DBNull.Value),
    ];

    private async Task<PurgeResult> PurgeOnPreparedConnectionAsync(
        TaskCompletionSource ready,
        TaskCompletionSource start,
        CancellationToken cancellationToken)
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync(cancellationToken);
        var processId = connection.ProcessID;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker", connection, transaction))
        {
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        ready.SetResult();
        await start.Task.WaitAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT security.purge_outbox(clock_timestamp()-interval '2 days',clock_timestamp()-interval '8 days',100,false)",
            connection,
            transaction)
        {
            CommandTimeout = 15,
        };
        var deleted = ConvertValue<int>(await command.ExecuteScalarAsync(cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new PurgeResult(deleted, processId);
    }

    private async Task<IReadOnlyList<Claim>> ClaimAsync(bool location, int batchSize, string workerId)
    {
        var function = location ? "security.claim_location_outbox" : "security.claim_outbox";
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_worker");
        await using var command = new NpgsqlCommand(
            $"SELECT id,status,attempts,locked_by,lease_token,lease_expires_at FROM {function}(@worker,@batch,interval '30 seconds')",
            connection,
            transaction);
        command.Parameters.Add(P("worker", workerId));
        command.Parameters.Add(P("batch", batchSize));
        await using var reader = await command.ExecuteReaderAsync();
        var claims = new List<Claim>();
        while (await reader.ReadAsync())
        {
            claims.Add(new Claim(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetGuid(4), reader.GetFieldValue<DateTimeOffset>(5)));
        }

        await reader.DisposeAsync();
        await transaction.CommitAsync();
        return claims;
    }

    private async Task<bool> SettleAsync(bool location, Guid id, Guid token, string status, DateTimeOffset? availableAt = null)
    {
        var function = location ? "security.settle_location_outbox" : "security.settle_outbox";
        return await WorkerScalarAsync<bool>(
            $"SELECT {function}(@id,@token,@status,@error,@available)",
            P("id", id), P("token", token), P("status", status), P("error", status == "PROCESSED" ? DBNull.Value : "synthetic failure"),
            P("available", availableAt ?? (object)DBNull.Value));
    }

    private async Task<int> RequeueAsync(bool location, int maxAttempts)
    {
        var function = location ? "security.requeue_stale_location_outbox" : "security.requeue_stale_outbox";
        return await WorkerScalarAsync<int>($"SELECT {function}(interval '0 seconds',100,@max)", P("max", maxAttempts));
    }

    private async Task<int> PurgeAsync(bool location, bool dryRun, int batchSize, bool invalidCutoff = false)
    {
        var function = location ? "security.purge_location_outbox" : "security.purge_outbox";
        var processed = invalidCutoff ? "clock_timestamp()" : "clock_timestamp()-interval '2 days'";
        var dead = invalidCutoff ? "clock_timestamp()" : "clock_timestamp()-interval '8 days'";
        return await WorkerScalarAsync<int>($"SELECT {function}({processed},{dead},@batch,@dry)", P("batch", batchSize), P("dry", dryRun));
    }

    private async Task<int> DeleteDirectlyAsMaintenanceAsync(string table, Guid id)
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_maintenance");
        var count = await ExecuteAsync(connection, transaction, $"DELETE FROM {table} WHERE id=@id", P("id", id));
        await transaction.CommitAsync();
        return count;
    }

    private async Task<T> WorkerScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_worker");
        var value = await ScalarAsync<T>(connection, transaction, sql, parameters);
        await transaction.CommitAsync();
        return value;
    }

    private async Task<string> BusinessStatusAsync(Guid id) =>
        await AdminScalarAsync<string>("SELECT status FROM platform.outbox_events WHERE id=@id", P("id", id));

    private async Task<string> LocationStatusAsync(Guid id) =>
        await AdminScalarAsync<string>("SELECT status FROM platform.location_outbox_events WHERE id=@id", P("id", id));

    private async Task<PostgresException> ExecuteExpectingPostgresErrorAsync(NpgsqlDataSource dataSource, string role, Guid org, string sql)
    {
        await using var tenant = await TenantTransaction.BeginAsync(dataSource, role, Guid.NewGuid(), [org]);
        return await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(tenant.Connection, tenant.Transaction, sql));
    }

    private async Task CleanupAsync(Guid org)
    {
        await ExecuteAdminAsync(
            "DELETE FROM platform.outbox_events WHERE owner_org_id=@org; DELETE FROM platform.location_outbox_events WHERE owner_org_id=@org; DELETE FROM organizations.organizations WHERE id=@org",
            P("org", org));
    }

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> AdminScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        var result = await command.ExecuteScalarAsync();
        return ConvertValue<T>(result);
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return ConvertValue<T>(await command.ExecuteScalarAsync());
    }

    private static T ConvertValue<T>(object? result) => result is T typed
        ? typed
        : (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);

    private static NpgsqlParameter P(string name, object value) => new(name, value);

    private sealed record Claim(Guid Id, string Status, int Attempts, string LockedBy, Guid LeaseToken, DateTimeOffset? LeaseExpiresAt);

    private sealed record PurgeResult(int Deleted, int ProcessId);
}
