using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Outbox.Retention;
using Paqueteria.Infrastructure.Scheduling;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// OPS-004 against the canonical AI-06/AI-18 baseline: the retention job reaches the outbox only
/// through the approved purge functions with the Worker role, deletes only old PROCESSED/DEAD
/// rows, never touches PENDING/RETRY/PROCESSING, and stays bounded per run and per lane.
/// </summary>
/// <remarks>
/// Purge is global, so each behavioural test first clears terminal rows left by other tests of
/// this non-parallel collection; otherwise shared leftovers would consume batch capacity.
/// </remarks>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class OutboxRetentionContractTests(PostgreSqlContractFixture fixture)
{
    private const string Maintenance = "paqueteria_maintenance";
    private const string BusinessPurge = "security.purge_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)";
    private const string LocationPurge = "security.purge_location_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)";

    [PostgreSqlContractFact]
    public async Task Maintenance_role_owns_only_the_purge_functions_and_no_lifecycle_capability()
    {
        Assert.Equal(
            [LocationPurge, BusinessPurge],
            await AdminListAsync($"SELECT p.oid::regprocedure::text FROM pg_proc p WHERE p.proowner='{Maintenance}'::regrole ORDER BY 1"));
        Assert.Equal(0, await AdminScalarAsync<int>($"SELECT count(*)::integer FROM pg_class WHERE relowner='{Maintenance}'::regrole"));
        Assert.Equal(0, await AdminScalarAsync<int>($"SELECT count(*)::integer FROM pg_namespace WHERE nspowner='{Maintenance}'::regrole"));
        Assert.Equal("false|true|false", await AdminScalarAsync<string>(
            $"SELECT rolcanlogin::text||'|'||rolbypassrls::text||'|'||rolsuper::text FROM pg_roles WHERE rolname='{Maintenance}'"));

        var lifecycle = await AdminListAsync($"""
            SELECT p.oid::regprocedure::text||'|'||pg_get_userbyid(p.proowner)||'|'||has_function_privilege('{Maintenance}',p.oid,'EXECUTE')::text
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname IN ('platform','security') AND p.proname ~ '^(claim|settle|requeue)'
            ORDER BY 1
            """);
        foreach (var function in new[]
        {
            "security.claim_outbox(", "security.settle_outbox(", "security.requeue_stale_outbox(",
            "security.claim_location_outbox(", "security.settle_location_outbox(", "security.requeue_stale_location_outbox(",
        })
        {
            var row = Assert.Single(lifecycle, value => value.StartsWith(function, StringComparison.Ordinal));
            Assert.Contains("|paqueteria_outbox_executor|", row, StringComparison.Ordinal);
        }

        Assert.All(lifecycle, row =>
        {
            Assert.DoesNotContain($"|{Maintenance}|", row, StringComparison.Ordinal);
            Assert.EndsWith("|false", row, StringComparison.Ordinal);
        });

        foreach (var statement in new[]
        {
            "SELECT * FROM security.claim_outbox('ops004-maintenance',1,interval '30 seconds')",
            "SELECT security.settle_outbox(gen_random_uuid(),gen_random_uuid(),'PROCESSED',NULL,NULL)",
            "SELECT security.requeue_stale_outbox(interval '0 seconds',1,1)",
            "SELECT * FROM security.claim_location_outbox('ops004-maintenance',1,interval '30 seconds')",
            "SELECT security.settle_location_outbox(gen_random_uuid(),gen_random_uuid(),'PROCESSED',NULL,NULL)",
            "SELECT security.requeue_stale_location_outbox(interval '0 seconds',1,1)",
        })
        {
            var exception = await ExecuteAsRoleExpectingErrorAsync(fixture.AdminDataSource, Maintenance, statement);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }
    }

    [PostgreSqlContractFact]
    public async Task Maintenance_role_is_limited_to_select_and_delete_on_the_two_outbox_tables()
    {
        const string relations = """
            FROM pg_class c
            JOIN pg_namespace n ON n.oid=c.relnamespace
            CROSS JOIN unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER']) AS p(privilege)
            WHERE c.relkind IN ('r','p','v','m','f')
              AND n.nspname <> 'information_schema' AND n.nspname NOT LIKE 'pg\_%'
            """;

        // No mutation anywhere, extension schemas included, beyond DELETE on the two outboxes.
        Assert.Equal(
            ["platform.location_outbox_events:DELETE", "platform.outbox_events:DELETE"],
            await AdminListAsync($"""
                SELECT n.nspname||'.'||c.relname||':'||p.privilege
                {relations}
                  AND p.privilege <> 'SELECT'
                  AND has_table_privilege('{Maintenance}',c.oid,p.privilege)
                ORDER BY 1
                """));

        // Reads are limited to the two outboxes. What every role already receives through PUBLIC
        // (PostGIS reference, topology and geocoder data) is not a capability of this role.
        Assert.Equal(
            ["platform.location_outbox_events", "platform.outbox_events"],
            await AdminListAsync($"""
                SELECT n.nspname||'.'||c.relname
                {relations}
                  AND p.privilege = 'SELECT'
                  AND has_table_privilege('{Maintenance}',c.oid,p.privilege)
                  AND NOT has_table_privilege('public',c.oid,p.privilege)
                ORDER BY 1
                """));
        Assert.Empty(await AdminListAsync($"""
            SELECT n.nspname||'.'||c.relname
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE c.relkind='S' AND n.nspname NOT LIKE 'pg\_%'
              AND (has_sequence_privilege('{Maintenance}',c.oid,'USAGE') OR has_sequence_privilege('{Maintenance}',c.oid,'UPDATE'))
            """));
        Assert.Equal(
            ["platform", "security"],
            await AdminListAsync($"""
                SELECT nspname FROM pg_namespace
                WHERE nspname NOT LIKE 'pg\_%' AND nspname NOT IN ('information_schema','public')
                  AND has_schema_privilege('{Maintenance}',oid,'USAGE')
                  AND NOT has_schema_privilege('public',oid,'USAGE')
                ORDER BY 1
                """));
        Assert.Empty(await AdminListAsync($"""
            SELECT nspname FROM pg_namespace
            WHERE nspname NOT LIKE 'pg\_%' AND has_schema_privilege('{Maintenance}',oid,'CREATE')
            """));

        foreach (var statement in new[]
        {
            "UPDATE platform.outbox_events SET last_error='ops004' WHERE false",
            "UPDATE platform.location_outbox_events SET last_error='ops004' WHERE false",
            "INSERT INTO platform.outbox_events SELECT * FROM platform.outbox_events WHERE false",
            "TRUNCATE platform.location_outbox_events",
            "DELETE FROM platform.audit_logs WHERE false",
            "DELETE FROM platform.idempotency_keys WHERE false",
            "UPDATE orders.orders SET version=version WHERE false",
            "DELETE FROM finance.settlements WHERE false",
            "DELETE FROM organizations.organizations WHERE false",
        })
        {
            var exception = await ExecuteAsRoleExpectingErrorAsync(fixture.AdminDataSource, Maintenance, statement);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }
    }

    [PostgreSqlContractFact]
    public async Task Worker_executes_purge_but_cannot_touch_outbox_tables_and_app_cannot_purge()
    {
        foreach (var function in new[] { BusinessPurge, LocationPurge })
        {
            Assert.True(await AdminScalarAsync<bool>($"SELECT has_function_privilege('paqueteria_worker','{function}','EXECUTE')"));
            Assert.False(await AdminScalarAsync<bool>($"SELECT has_function_privilege('paqueteria_app','{function}','EXECUTE')"));
            Assert.Equal(
                "true|pg_catalog, platform, security, pg_temp",
                await AdminScalarAsync<string>($"""
                    SELECT p.prosecdef::text||'|'||substring(array_to_string(p.proconfig,',') FROM 'search_path=(.*)$')
                    FROM pg_proc p WHERE p.oid='{function}'::regprocedure
                    """));
        }

        foreach (var role in new[] { "paqueteria_worker", "paqueteria_app" })
        {
            foreach (var table in new[] { "platform.outbox_events", "platform.location_outbox_events" })
            {
                foreach (var privilege in new[] { "SELECT", "UPDATE", "DELETE", "TRUNCATE" })
                {
                    Assert.False(
                        await AdminScalarAsync<bool>($"SELECT has_table_privilege('{role}','{table}','{privilege}')"),
                        $"{role} must not hold {privilege} on {table}.");
                }
            }
        }

        var dataSource = new OutboxRetentionDataSource(fixture.WorkerConnectionString);
        await using (dataSource)
        {
            var gateway = new PostgreSqlOutboxPurgeGateway(dataSource, commandTimeoutSeconds: 30);
            var now = await DatabaseNowAsync();
            foreach (var contract in OutboxRetentionLaneContract.All)
            {
                var eligible = await gateway.PurgeAsync(
                    new OutboxPurgeRequest(
                        contract.Lane,
                        now - contract.MinimumProcessedRetention - TimeSpan.FromHours(1),
                        now - contract.MinimumDeadRetention - TimeSpan.FromHours(1),
                        1,
                        DryRun: true),
                    CancellationToken.None);
                Assert.InRange(eligible, 0, 1);
            }
        }

        foreach (var table in new[] { "platform.outbox_events", "platform.location_outbox_events" })
        {
            foreach (var statement in new[] { $"SELECT 1 FROM {table}", $"DELETE FROM {table} WHERE false" })
            {
                var exception = await ExecuteAsRoleExpectingErrorAsync(fixture.WorkerDataSource, "paqueteria_worker", statement);
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
            }
        }

        foreach (var function in new[] { "security.purge_outbox", "security.purge_location_outbox" })
        {
            var exception = await ExecuteAsRoleExpectingErrorAsync(
                fixture.AppDataSource,
                "paqueteria_app",
                $"SELECT {function}(clock_timestamp()-interval '30 days',clock_timestamp()-interval '30 days',1,true)");
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }
    }

    [PostgreSqlContractFact]
    public async Task Runtime_roles_cannot_assume_the_maintenance_role()
    {
        foreach (var role in new[] { "paqueteria_worker", "paqueteria_app", PostgreSqlContractFixture.WorkerLogin, PostgreSqlContractFixture.AppLogin })
        {
            Assert.False(await AdminScalarAsync<bool>($"SELECT pg_has_role('{role}','{Maintenance}','MEMBER')"));
        }

        var worker = await ExecuteAsRoleExpectingErrorAsync(fixture.WorkerDataSource, "paqueteria_worker", $"SET LOCAL ROLE {Maintenance}");
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, worker.SqlState);
        var app = await ExecuteAsRoleExpectingErrorAsync(fixture.AppDataSource, "paqueteria_app", $"SET LOCAL ROLE {Maintenance}");
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, app.SqlState);
    }

    [PostgreSqlContractFact]
    public async Task Scheduled_run_deletes_only_old_terminal_rows_preserves_active_states_and_is_idempotent()
    {
        var org = await BeginScenarioAsync();
        try
        {
            var business = new Rows(
                Eligible:
                [
                    await InsertAsync(Lane.Business, org, "PROCESSED", created: "12 days", processed: "10 days"),
                    await InsertAsync(Lane.Business, org, "DEAD", created: "12 days"),
                    await InsertAsync(Lane.Business, org, "DEAD", created: "20 days", processed: "9 days"),
                ],
                Retained:
                [
                    // Terminal rows inside the configured retention, including an old message
                    // processed recently: PROCESSED age is measured from processed_at.
                    await InsertAsync(Lane.Business, org, "PROCESSED", created: "13 days", processed: "12 hours"),
                    await InsertAsync(Lane.Business, org, "DEAD", created: "3 days"),
                    .. await InsertActiveStatesAsync(Lane.Business, org),
                ]);
            var location = new Rows(
                Eligible:
                [
                    await InsertAsync(Lane.Location, org, "PROCESSED", created: "6 hours", processed: "5 hours"),
                    await InsertAsync(Lane.Location, org, "DEAD", created: "4 days"),
                ],
                Retained:
                [
                    await InsertAsync(Lane.Location, org, "PROCESSED", created: "3 days", processed: "10 minutes"),
                    await InsertAsync(Lane.Location, org, "DEAD", created: "6 hours"),
                    .. await InsertActiveStatesAsync(Lane.Location, org),
                ]);
            var before = await SnapshotAsync(org);

            await using var harness = await HarnessAsync(Options(dryRun: false));
            var first = await harness.Service.RunOnceAsync(CancellationToken.None);

            AssertLane(first, OutboxRetentionLane.Business, affected: 3, batches: 1, exhausted: true);
            AssertLane(first, OutboxRetentionLane.Location, affected: 2, batches: 1, exhausted: true);
            var after = await SnapshotAsync(org);
            Assert.All(business.Eligible.Concat(location.Eligible), id => Assert.False(after.ContainsKey(id)));
            Assert.All(business.Retained.Concat(location.Retained), id => Assert.Equal(before[id], after[id]));

            var second = await harness.Service.RunOnceAsync(CancellationToken.None);

            AssertLane(second, OutboxRetentionLane.Business, affected: 0, batches: 1, exhausted: true);
            AssertLane(second, OutboxRetentionLane.Location, affected: 0, batches: 1, exhausted: true);
            Assert.Equal(after, await SnapshotAsync(org));
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [PostgreSqlContractFact]
    public async Task Business_and_location_cutoffs_are_independent()
    {
        var org = await BeginScenarioAsync();
        try
        {
            // Same ages in both lanes: only the location retention (2h/2d) makes them eligible.
            var businessRecent = new[]
            {
                await InsertAsync(Lane.Business, org, "PROCESSED", created: "6 hours", processed: "5 hours"),
                await InsertAsync(Lane.Business, org, "DEAD", created: "3 days"),
            };
            var locationOld = new[]
            {
                await InsertAsync(Lane.Location, org, "PROCESSED", created: "6 hours", processed: "5 hours"),
                await InsertAsync(Lane.Location, org, "DEAD", created: "3 days"),
            };

            await using (var harness = await HarnessAsync(Options(dryRun: false)))
            {
                var report = await harness.Service.RunOnceAsync(CancellationToken.None);
                AssertLane(report, OutboxRetentionLane.Business, affected: 0, batches: 1, exhausted: true);
                AssertLane(report, OutboxRetentionLane.Location, affected: 2, batches: 1, exhausted: true);
            }

            var snapshot = await SnapshotAsync(org);
            Assert.All(businessRecent, id => Assert.True(snapshot.ContainsKey(id)));
            Assert.All(locationOld, id => Assert.False(snapshot.ContainsKey(id)));

            // And the other way round: a longer location retention protects rows that the
            // business retention already releases.
            var businessOld = await InsertAsync(Lane.Business, org, "PROCESSED", created: "4 days", processed: "3 days");
            var locationKept = await InsertAsync(Lane.Location, org, "PROCESSED", created: "4 days", processed: "3 days");
            var inverted = Options(dryRun: false);
            inverted.Location.ProcessedRetention = TimeSpan.FromDays(5);
            await using (var harness = await HarnessAsync(inverted))
            {
                var report = await harness.Service.RunOnceAsync(CancellationToken.None);
                AssertLane(report, OutboxRetentionLane.Business, affected: 1, batches: 1, exhausted: true);
                AssertLane(report, OutboxRetentionLane.Location, affected: 0, batches: 1, exhausted: true);
            }

            snapshot = await SnapshotAsync(org);
            Assert.False(snapshot.ContainsKey(businessOld));
            Assert.True(snapshot.ContainsKey(locationKept));
            Assert.All(businessRecent, id => Assert.True(snapshot.ContainsKey(id)));
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [PostgreSqlContractFact]
    public async Task Dry_run_reports_bounded_counts_without_mutation_and_matches_the_next_destructive_call()
    {
        var org = await BeginScenarioAsync();
        try
        {
            // Distinct creation times make the purge order (ORDER BY created_at) deterministic.
            var business = new List<Guid>();
            foreach (var created in new[] { 20, 19, 18, 17, 16 })
            {
                business.Add(await InsertAsync(Lane.Business, org, "PROCESSED", created: $"{created} days", processed: "10 days"));
            }

            business.Add(await InsertAsync(Lane.Business, org, "DEAD", created: "15 days"));
            business.Add(await InsertAsync(Lane.Business, org, "DEAD", created: "14 days"));
            var location = new List<Guid>();
            foreach (var created in new[] { 9, 8, 7, 6 })
            {
                location.Add(await InsertAsync(Lane.Location, org, "PROCESSED", created: $"{created} hours", processed: "5 hours"));
            }

            await InsertActiveStatesAsync(Lane.Business, org);
            await InsertActiveStatesAsync(Lane.Location, org);
            var before = await SnapshotAsync(org);

            var options = Options(dryRun: true);
            options.Business.BatchSize = 3;
            options.Location.BatchSize = 10;
            await using var harness = await HarnessAsync(options);

            var scheduled = await harness.Service.RunOnceAsync(CancellationToken.None);
            var repeated = await harness.Service.RunOnceAsync(CancellationToken.None);

            // The explicit path stays a dry-run even when the configuration is destructive.
            options.DryRun = false;
            var explicitDryRun = await harness.Service.DryRunAsync(CancellationToken.None);

            foreach (var report in new[] { scheduled, repeated, explicitDryRun })
            {
                AssertLane(report, OutboxRetentionLane.Business, affected: 3, batches: 1, exhausted: false, dryRun: true);
                AssertLane(report, OutboxRetentionLane.Location, affected: 4, batches: 1, exhausted: true, dryRun: true);
            }

            Assert.Equal(before, await SnapshotAsync(org));

            // The same call with p_dry_run=false deletes exactly the counted candidates.
            var businessDryRun = Assert.Single(explicitDryRun.Lanes, lane => lane.Lane == OutboxRetentionLane.Business);
            var locationDryRun = Assert.Single(explicitDryRun.Lanes, lane => lane.Lane == OutboxRetentionLane.Location);
            Assert.Equal(3, await harness.Gateway.PurgeAsync(Destructive(businessDryRun), CancellationToken.None));
            Assert.Equal(4, await harness.Gateway.PurgeAsync(Destructive(locationDryRun), CancellationToken.None));

            var after = await SnapshotAsync(org);
            Assert.All(business.Take(3), id => Assert.False(after.ContainsKey(id)));
            Assert.All(business.Skip(3), id => Assert.Equal(before[id], after[id]));
            Assert.All(location, id => Assert.False(after.ContainsKey(id)));
            Assert.Equal(before.Count - 7, after.Count);
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [PostgreSqlContractFact]
    public async Task Per_run_ceiling_bounds_deletion_and_the_backlog_drains_over_later_runs()
    {
        var org = await BeginScenarioAsync();
        try
        {
            var business = new List<Guid>();
            for (var day = 30; day > 23; day--)
            {
                business.Add(await InsertAsync(Lane.Business, org, "PROCESSED", created: $"{day} days", processed: "10 days"));
            }

            var location = new List<Guid>();
            for (var day = 12; day > 7; day--)
            {
                location.Add(await InsertAsync(Lane.Location, org, "DEAD", created: $"{day} days"));
            }

            var active = (await InsertActiveStatesAsync(Lane.Business, org))
                .Concat(await InsertActiveStatesAsync(Lane.Location, org))
                .ToArray();
            var before = await SnapshotAsync(org);

            var options = Options(dryRun: false);
            options.Business.BatchSize = 2;
            options.Business.MaxBatchesPerRun = 2;
            options.Location.BatchSize = 2;
            options.Location.MaxBatchesPerRun = 1;
            await using var harness = await HarnessAsync(options);

            var first = await harness.Service.RunOnceAsync(CancellationToken.None);
            AssertLane(first, OutboxRetentionLane.Business, affected: 4, batches: 2, exhausted: false);
            AssertLane(first, OutboxRetentionLane.Location, affected: 2, batches: 1, exhausted: false);
            var snapshot = await SnapshotAsync(org);
            Assert.All(business.Take(4), id => Assert.False(snapshot.ContainsKey(id)));
            Assert.All(business.Skip(4), id => Assert.True(snapshot.ContainsKey(id)));
            Assert.All(location.Take(2), id => Assert.False(snapshot.ContainsKey(id)));
            Assert.All(location.Skip(2), id => Assert.True(snapshot.ContainsKey(id)));

            var second = await harness.Service.RunOnceAsync(CancellationToken.None);
            AssertLane(second, OutboxRetentionLane.Business, affected: 3, batches: 2, exhausted: true);
            AssertLane(second, OutboxRetentionLane.Location, affected: 2, batches: 1, exhausted: false);

            var third = await harness.Service.RunOnceAsync(CancellationToken.None);
            AssertLane(third, OutboxRetentionLane.Business, affected: 0, batches: 1, exhausted: true);
            AssertLane(third, OutboxRetentionLane.Location, affected: 1, batches: 1, exhausted: true);

            var fourth = await harness.Service.RunOnceAsync(CancellationToken.None);
            AssertLane(fourth, OutboxRetentionLane.Location, affected: 0, batches: 1, exhausted: true);

            var after = await SnapshotAsync(org);
            Assert.Equal(active.Order(), after.Keys.Order());
            Assert.All(active, id => Assert.Equal(before[id], after[id]));
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [PostgreSqlContractFact]
    public async Task Database_rejects_cutoffs_inside_the_normative_window_for_both_lanes_without_mutation()
    {
        var org = await BeginScenarioAsync();
        try
        {
            var eligible = new[]
            {
                await InsertAsync(Lane.Business, org, "PROCESSED", created: "30 days", processed: "20 days"),
                await InsertAsync(Lane.Location, org, "PROCESSED", created: "30 days", processed: "20 days"),
            };
            var recent = new[]
            {
                await InsertAsync(Lane.Business, org, "PROCESSED", created: "2 minutes", processed: "1 minute"),
                await InsertAsync(Lane.Business, org, "DEAD", created: "1 minute"),
                await InsertAsync(Lane.Location, org, "PROCESSED", created: "2 minutes", processed: "1 minute"),
                await InsertAsync(Lane.Location, org, "DEAD", created: "1 minute"),
            };
            var before = await SnapshotAsync(org);
            await using var harness = await HarnessAsync(Options(dryRun: false));
            var margin = TimeSpan.FromMinutes(5);

            foreach (var contract in OutboxRetentionLaneContract.All)
            {
                var now = await DatabaseNowAsync();
                var processedOutside = now - contract.MinimumProcessedRetention - margin;
                var deadOutside = now - contract.MinimumDeadRetention - margin;
                foreach (var (processedBefore, deadBefore) in new[]
                {
                    (now - contract.MinimumProcessedRetention + margin, deadOutside),
                    (processedOutside, now - contract.MinimumDeadRetention + margin),
                    (now, now),
                })
                {
                    foreach (var dryRun in new[] { true, false })
                    {
                        var exception = await Assert.ThrowsAsync<PostgresException>(() => harness.Gateway.PurgeAsync(
                            new OutboxPurgeRequest(contract.Lane, processedBefore, deadBefore, 100, dryRun),
                            CancellationToken.None));
                        Assert.Equal(PostgresErrorCodes.InvalidParameterValue, exception.SqlState);
                    }
                }

                // Just outside the minimum window the same function accepts the call.
                Assert.Equal(1, await harness.Gateway.PurgeAsync(
                    new OutboxPurgeRequest(contract.Lane, processedOutside, deadOutside, 100, DryRun: true),
                    CancellationToken.None));
            }

            Assert.Equal(before, await SnapshotAsync(org));
            Assert.All(eligible.Concat(recent), id => Assert.True(before.ContainsKey(id)));
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [PostgreSqlContractFact]
    public async Task A_rejected_lane_is_observable_and_does_not_block_or_corrupt_the_other_lane()
    {
        var org = await BeginScenarioAsync();
        try
        {
            var businessEligible = await InsertAsync(Lane.Business, org, "PROCESSED", created: "30 days", processed: "20 days");
            var locationEligible = await InsertAsync(Lane.Location, org, "PROCESSED", created: "30 days", processed: "20 days");
            var active = (await InsertActiveStatesAsync(Lane.Business, org))
                .Concat(await InsertActiveStatesAsync(Lane.Location, org))
                .ToArray();
            var before = await SnapshotAsync(org);

            // Defence in depth: configuration below the normative minimum never passes startup
            // validation, but if it reached the database the function still refuses it.
            var unsafeOptions = Options(dryRun: false);
            unsafeOptions.Business.ProcessedRetention = TimeSpan.FromHours(1);
            Assert.NotEmpty(OutboxRetentionOptionsValidator.Errors(unsafeOptions));
            await using var harness = await HarnessAsync(unsafeOptions);

            var report = await harness.Service.RunOnceAsync(CancellationToken.None);

            var business = Assert.Single(report.Lanes, lane => lane.Lane == OutboxRetentionLane.Business);
            Assert.Equal(OutboxRetentionOutcomes.Failure, business.Outcome);
            Assert.Equal(PostgresErrorCodes.InvalidParameterValue, business.ErrorClass);
            Assert.Equal(0, business.Batches);
            Assert.Equal(0, business.AffectedRows);
            AssertLane(report, OutboxRetentionLane.Location, affected: 1, batches: 1, exhausted: true);

            var failure = Assert.Single(harness.Logger.Entries, entry => entry.Level == LogLevel.Error);
            Assert.Contains("business", failure.Message, StringComparison.Ordinal);
            Assert.Contains("error_class=22023", failure.Message, StringComparison.Ordinal);

            var after = await SnapshotAsync(org);
            Assert.Equal(before[businessEligible], after[businessEligible]);
            Assert.False(after.ContainsKey(locationEligible));
            Assert.All(active, id => Assert.Equal(before[id], after[id]));
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    [PostgreSqlContractFact]
    public async Task Hosted_job_is_inert_when_disabled_counts_in_dry_run_and_bounds_destructive_cycles()
    {
        var org = await BeginScenarioAsync();
        try
        {
            var eligible = new List<Guid>();
            for (var day = 30; day > 25; day--)
            {
                eligible.Add(await InsertAsync(Lane.Business, org, "PROCESSED", created: $"{day} days", processed: "10 days"));
            }

            var active = (await InsertActiveStatesAsync(Lane.Business, org))
                .Concat(await InsertActiveStatesAsync(Lane.Location, org))
                .ToArray();
            var before = await SnapshotAsync(org);

            var disabled = Options(dryRun: false);
            disabled.Enabled = false;
            await using (var harness = await HarnessAsync(disabled))
            {
                await harness.RunHostedCycleAsync(expectCycle: false);
                Assert.Empty(harness.Gateway.Requests);
            }

            Assert.Equal(before, await SnapshotAsync(org));

            await using (var harness = await HarnessAsync(Options(dryRun: true)))
            {
                await harness.RunHostedCycleAsync(expectCycle: true);
                Assert.Equal(2, harness.Gateway.Requests.Count);
                Assert.All(harness.Gateway.Requests, request => Assert.True(request.DryRun));
            }

            Assert.Equal(before, await SnapshotAsync(org));

            var destructive = Options(dryRun: false);
            destructive.Business.BatchSize = 2;
            destructive.Business.MaxBatchesPerRun = 2;
            await using (var harness = await HarnessAsync(destructive))
            {
                await harness.RunHostedCycleAsync(expectCycle: true);
                var snapshot = await SnapshotAsync(org);
                Assert.All(eligible.Take(4), id => Assert.False(snapshot.ContainsKey(id)));
                Assert.True(snapshot.ContainsKey(eligible[4]));

                // A repeated cycle continues the bounded backlog and then becomes a no-op.
                await harness.RunHostedCycleAsync(expectCycle: true);
                await harness.RunHostedCycleAsync(expectCycle: true);
            }

            var after = await SnapshotAsync(org);
            Assert.Equal(active.Order(), after.Keys.Order());
            Assert.All(active, id => Assert.Equal(before[id], after[id]));
        }
        finally
        {
            await CleanupAsync(org);
        }
    }

    private enum Lane
    {
        Business,
        Location,
    }

    private sealed record Rows(Guid[] Eligible, Guid[] Retained);

    private static OutboxRetentionOptions Options(bool dryRun) => new()
    {
        Enabled = true,
        DryRun = dryRun,
        PollInterval = TimeSpan.FromMinutes(1),
        Business = new()
        {
            ProcessedRetention = TimeSpan.FromDays(2),
            DeadRetention = TimeSpan.FromDays(8),
            BatchSize = 100,
            MaxBatchesPerRun = 10,
        },
        Location = new()
        {
            ProcessedRetention = TimeSpan.FromHours(2),
            DeadRetention = TimeSpan.FromDays(2),
            BatchSize = 100,
            MaxBatchesPerRun = 10,
        },
    };

    private static OutboxPurgeRequest Destructive(OutboxRetentionLaneReport dryRun) =>
        new(dryRun.Lane, dryRun.ProcessedBefore, dryRun.DeadBefore, dryRun.BatchSize, DryRun: false);

    private static void AssertLane(
        OutboxRetentionRunReport report,
        OutboxRetentionLane lane,
        long affected,
        int batches,
        bool exhausted,
        bool dryRun = false)
    {
        var value = Assert.Single(report.Lanes, item => item.Lane == lane);
        Assert.Equal(OutboxRetentionOutcomes.Success, value.Outcome);
        Assert.Equal(dryRun, value.DryRun);
        Assert.Equal(affected, value.AffectedRows);
        Assert.Equal(batches, value.Batches);
        Assert.Equal(exhausted, value.Exhausted);
        Assert.True(value.ProcessedBefore < value.StartedAt && value.DeadBefore < value.StartedAt);
        Assert.True(value.CompletedAt >= value.StartedAt);
    }

    private async Task<Guid> BeginScenarioAsync()
    {
        await ExecuteAdminAsync("""
            DELETE FROM platform.outbox_events WHERE status IN ('PROCESSED','DEAD');
            DELETE FROM platform.location_outbox_events WHERE status IN ('PROCESSED','DEAD');
            """);
        var org = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@id,'OPS-004 Synthetic','OPS-004 Synthetic','BUSINESS')",
            new NpgsqlParameter("id", org));
        return org;
    }

    private async Task<Guid[]> InsertActiveStatesAsync(Lane lane, Guid org) =>
    [
        await InsertAsync(lane, org, "PENDING", created: "60 days"),
        await InsertAsync(lane, org, "RETRY", created: "60 days", attempts: 3),
        await InsertAsync(lane, org, "PROCESSING", created: "60 days", attempts: 1, leaseIn: "1 hour"),
        await InsertAsync(lane, org, "PROCESSING", created: "60 days", attempts: 2, leaseIn: "-1 minute"),
    ];

    private async Task<Guid> InsertAsync(
        Lane lane,
        Guid org,
        string status,
        string created,
        string? processed = null,
        int attempts = 0,
        string? leaseIn = null)
    {
        var id = Guid.NewGuid();
        var sql = lane == Lane.Business
            ? """
              INSERT INTO platform.outbox_events(id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,payload,priority,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,created_at,processed_at)
              SELECT @id,@org,'{}','ops004.business','Order',gen_random_uuid(),'{}',50,@status,@attempts,v.created_at,v.locked_at,v.locked_by,v.lease_token,v.lease_expires_at,v.created_at,v.processed_at
              FROM (SELECT
                clock_timestamp()-@created::interval AS created_at,
                CASE WHEN @lease_in IS NULL THEN NULL ELSE clock_timestamp()-interval '1 minute' END AS locked_at,
                CASE WHEN @lease_in IS NULL THEN NULL ELSE 'ops004-active-worker' END AS locked_by,
                CASE WHEN @lease_in IS NULL THEN NULL ELSE gen_random_uuid() END AS lease_token,
                CASE WHEN @lease_in IS NULL THEN NULL ELSE clock_timestamp()+@lease_in::interval END AS lease_expires_at,
                CASE WHEN @processed IS NULL THEN NULL ELSE clock_timestamp()-@processed::interval END AS processed_at) v
              """
            : """
              INSERT INTO platform.location_outbox_events(id,owner_org_id,driver_position_id,topic,payload,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,created_at,processed_at)
              SELECT @id,@org,gen_random_uuid(),'ops004.location','{}',@status,@attempts,v.created_at,v.locked_at,v.locked_by,v.lease_token,v.lease_expires_at,v.created_at,v.processed_at
              FROM (SELECT
                clock_timestamp()-@created::interval AS created_at,
                CASE WHEN @lease_in IS NULL THEN NULL ELSE clock_timestamp()-interval '1 minute' END AS locked_at,
                CASE WHEN @lease_in IS NULL THEN NULL ELSE 'ops004-active-worker' END AS locked_by,
                CASE WHEN @lease_in IS NULL THEN NULL ELSE gen_random_uuid() END AS lease_token,
                CASE WHEN @lease_in IS NULL THEN NULL ELSE clock_timestamp()+@lease_in::interval END AS lease_expires_at,
                CASE WHEN @processed IS NULL THEN NULL ELSE clock_timestamp()-@processed::interval END AS processed_at) v
              """;
        await ExecuteAdminAsync(
            sql,
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id },
            new NpgsqlParameter("org", NpgsqlDbType.Uuid) { Value = org },
            new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = status },
            new NpgsqlParameter("attempts", NpgsqlDbType.Integer) { Value = attempts },
            new NpgsqlParameter("created", NpgsqlDbType.Text) { Value = created },
            new NpgsqlParameter("processed", NpgsqlDbType.Text) { Value = (object?)processed ?? DBNull.Value },
            new NpgsqlParameter("lease_in", NpgsqlDbType.Text) { Value = (object?)leaseIn ?? DBNull.Value });
        return id;
    }

    /// <summary>Every lifecycle column of both lanes, so any state change is detected.</summary>
    private async Task<Dictionary<Guid, string>> SnapshotAsync(Guid org)
    {
        const string columns = "status||'|'||attempts||'|'||coalesce(lease_token::text,'-')||'|'||coalesce(lease_expires_at::text,'-')||'|'||coalesce(locked_by,'-')||'|'||coalesce(locked_at::text,'-')||'|'||coalesce(processed_at::text,'-')||'|'||coalesce(last_error,'-')||'|'||available_at::text";
        await using var command = fixture.AdminDataSource.CreateCommand($"""
            SELECT id,'business|'||{columns} FROM platform.outbox_events WHERE owner_org_id=@org
            UNION ALL
            SELECT id,'location|'||{columns} FROM platform.location_outbox_events WHERE owner_org_id=@org
            """);
        command.Parameters.Add(new NpgsqlParameter("org", NpgsqlDbType.Uuid) { Value = org });
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new Dictionary<Guid, string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetGuid(0), reader.GetString(1));
        }

        return rows;
    }

    private async Task CleanupAsync(Guid org) => await ExecuteAdminAsync(
        "DELETE FROM platform.outbox_events WHERE owner_org_id=@org; DELETE FROM platform.location_outbox_events WHERE owner_org_id=@org; DELETE FROM organizations.organizations WHERE id=@org",
        new NpgsqlParameter("org", NpgsqlDbType.Uuid) { Value = org });

    private async Task<DateTimeOffset> DatabaseNowAsync() =>
        new(DateTime.SpecifyKind(await AdminScalarAsync<DateTime>("SELECT clock_timestamp()"), DateTimeKind.Utc));

    private async Task<Harness> HarnessAsync(OutboxRetentionOptions options)
    {
        // Row ages are relative to the database clock, so the job's clock is aligned to it; a
        // container clock drift must not turn a retained row into an eligible one.
        var skew = await DatabaseNowAsync() - DateTimeOffset.UtcNow;
        return new Harness(fixture.WorkerConnectionString, options, new SkewedTimeProvider(skew));
    }

    private static async Task<PostgresException> ExecuteAsRoleExpectingErrorAsync(NpgsqlDataSource dataSource, string role, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setRole = new NpgsqlCommand($"SET LOCAL ROLE {role}", connection, transaction))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> AdminScalarAsync<T>(string sql)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        var result = await command.ExecuteScalarAsync();
        return result is T typed ? typed : (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
    }

    private async Task<string[]> AdminListAsync(string sql)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private sealed class SkewedTimeProvider(TimeSpan skew) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + skew;
    }

    private sealed class RecordingGateway(IOutboxPurgeGateway inner) : IOutboxPurgeGateway
    {
        public ConcurrentQueue<OutboxPurgeRequest> Requests { get; } = new();

        public Task<int> PurgeAsync(OutboxPurgeRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return inner.PurgeAsync(request, cancellationToken);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private TaskCompletionSource _locationLane = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        /// <summary>The location lane runs last, so its evidence record closes a cycle.</summary>
        public Task NextCycle => _locationLane.Task;

        public void ResetCycle() =>
            _locationLane = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Entries.Enqueue((logLevel, message));
            if (eventId.Id == 4004 && message.StartsWith("Outbox retention lane location ", StringComparison.Ordinal))
            {
                _locationLane.TrySetResult();
            }
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly OutboxRetentionDataSource _dataSource;
        private readonly OutboxRetentionTelemetry _telemetry = new();
        private readonly OutboxRetentionOptions _options;
        private readonly TimeProvider _time;

        public Harness(string workerConnectionString, OutboxRetentionOptions options, TimeProvider time)
        {
            _dataSource = new OutboxRetentionDataSource(workerConnectionString);
            _options = options;
            _time = time;
            Gateway = new RecordingGateway(new PostgreSqlOutboxPurgeGateway(_dataSource, commandTimeoutSeconds: 30));
            Service = new OutboxRetentionService(Gateway, Microsoft.Extensions.Options.Options.Create(options), _telemetry, time, Logger);
        }

        public RecordingGateway Gateway { get; }

        public CapturingLogger<OutboxRetentionService> Logger { get; } = new();

        public OutboxRetentionService Service { get; }

        /// <summary>
        /// Starts the hosted job exactly as the Worker does, lets it run its first scheduled cycle
        /// and stops it before the next poll interval.
        /// </summary>
        public async Task RunHostedCycleAsync(bool expectCycle)
        {
            Logger.ResetCycle();
            using var job = new OutboxRetentionHostedService(
                new PeriodicJobScheduler(_time, new CapturingLogger<PeriodicJobScheduler>()),
                new OutboxRetentionJob(Service, Microsoft.Extensions.Options.Options.Create(_options)),
                Microsoft.Extensions.Options.Options.Create(_options),
                new CapturingLogger<OutboxRetentionHostedService>());
            await job.StartAsync(CancellationToken.None);
            if (expectCycle)
            {
                await Logger.NextCycle.WaitAsync(TimeSpan.FromSeconds(30));
            }
            else
            {
                await job.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
            }

            await job.StopAsync(CancellationToken.None);
            Assert.True(job.ExecuteTask!.IsCompletedSuccessfully);
        }

        public async ValueTask DisposeAsync()
        {
            _telemetry.Dispose();
            await _dataSource.DisposeAsync();
        }
    }
}
