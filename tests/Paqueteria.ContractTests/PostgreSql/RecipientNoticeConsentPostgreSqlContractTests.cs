using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Application.Csv;
using Orders.Application.Orders;
using Orders.Application.RecipientNotices;
using Orders.Infrastructure;
using Orders.Infrastructure.Csv;
using Orders.Infrastructure.Orders;
using Orders.Infrastructure.Persistence;
using Orders.Infrastructure.Persistence.Migrations;
using Orders.Infrastructure.RecipientNotices;
using Paqueteria.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-004 required PostgreSQL tests: the consent table append-only and under
/// RLS) and NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23 (N3 choices 2 to 4 and 7) on a real PostgreSQL 18 / PostGIS 3.6: the
/// Orders lane step <c>20261023000200_AddRecipientNoticeConsents</c> (Up, Down and Up again; the Down refuses while a
/// consent exists), the exact catalog AI-06 and AI-18 declare, the createOrder and CSV-001 paths that record a ticked box
/// once (who and when) and nothing for an unticked one, and the Worker reader that answers from the table under FORCE
/// RLS. No phone, e-mail or name is ever part of a consent.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class RecipientNoticeConsentPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Module = "Orders";

    private static readonly DateTimeOffset AcceptedAtClient = new(
        DateTimeOffset.UtcNow.AddHours(-1).UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
        TimeSpan.Zero);

    // ------------------------------------------------------------------ the lane step and its catalog

    [PostgreSqlContractFact]
    public async Task The_consent_step_is_the_latest_orders_migration_with_the_exact_declared_catalog()
    {
        var verified = Assert.Single(ModuleMigrationCoordinator.VerifySources(), state => state.Module == Module);
        Assert.Equal(AddRecipientNoticeConsents.MigrationId, verified.MigrationId);
        Assert.Equal(
            AddRecipientNoticeConsents.MigrationId,
            await ScalarAsync<string>(fixture.DeploymentConnectionString,
                "SELECT max(\"MigrationId\") FROM platform.__ef_migrations_history_orders"));
        await AssertCanonicalShapeAsync(fixture.DeploymentConnectionString);
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await new DatabaseBaselineAssertions().AssertAsync(connection);
    }

    [PostgreSqlContractFact]
    public async Task Up_down_and_up_again_create_the_table_refuse_drift_and_roll_back_only_without_consents()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("ntf004lane");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            Assert.Equal(
                DatabaseBaselineApplyStatus.Applied,
                (await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString)).Status);
            // The AI-06 baseline declares the table in comments only: a fresh installation gets it from the lane step.
            Assert.False(await TableExistsAsync(connectionString));

            var coordinator = new ModuleMigrationCoordinator();
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.Equal(AddRecipientNoticeConsents.MigrationId, (await HistoryAsync(connectionString))[^1]);
            Assert.Equal("APPLIED", await LaneStatusAsync(coordinator, connectionString));
            await AssertCanonicalShapeAsync(connectionString);
            await AssertBaselineAsync(connectionString);

            // A table of that name that already exists is refused, never altered.
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsMigratorInTransactionAsync(connectionString, AddRecipientNoticeConsents.RenderUpSql()));
            Assert.Equal("NTF-004 found orders.recipient_notice_consents already present; it is never altered", refused.MessageText);

            // The recorded step requires the exact shape: the baseline assertions refuse drift.
            foreach (var (drift, repair, violation) in new[]
                     {
                         ("ALTER TABLE orders.recipient_notice_consents NO FORCE ROW LEVEL SECURITY;",
                          "ALTER TABLE orders.recipient_notice_consents FORCE ROW LEVEL SECURITY;",
                          "missing ENABLE/FORCE RLS"),
                         ("GRANT UPDATE ON orders.recipient_notice_consents TO paqueteria_app;",
                          "REVOKE UPDATE ON orders.recipient_notice_consents FROM paqueteria_app;",
                          "privilege UPDATE on orders.recipient_notice_consents"),
                         ("GRANT INSERT ON orders.recipient_notice_consents TO paqueteria_worker;",
                          "REVOKE INSERT ON orders.recipient_notice_consents FROM paqueteria_worker;",
                          "privilege INSERT on orders.recipient_notice_consents"),
                         ("ALTER TABLE orders.recipient_notice_consents DISABLE TRIGGER recipient_notice_consents_append_only;",
                          "ALTER TABLE orders.recipient_notice_consents ENABLE TRIGGER recipient_notice_consents_append_only;",
                          "recipient_notice_consents_append_only"),
                         ("DROP POLICY recipient_notice_consents_tenant ON orders.recipient_notice_consents;",
                          "CREATE POLICY recipient_notice_consents_tenant ON orders.recipient_notice_consents USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));",
                          "tenant policy"),
                     })
            {
                await ExecuteAsMigratorAsync(connectionString, drift);
                var failure = await Assert.ThrowsAsync<DatabaseAssertionException>(() => AssertBaselineAsync(connectionString));
                Assert.Contains(failure.Violations, item => item.Contains(violation, StringComparison.Ordinal));
                await ExecuteAsMigratorAsync(connectionString, repair);
                await AssertBaselineAsync(connectionString);
            }

            // A consent blocks the rollback; the table, the row and the history stay.
            var (org, user, order) = await InsertMinimalOrderAsync(connectionString);
            await ExecuteAsAppAsync(connectionString, org, user,
                """
                INSERT INTO orders.recipient_notice_consents(order_id,owner_org_id,channel,consent_version,attested_by,attested_at)
                VALUES (@order,@org,'WHATSAPP','NTF-RECIPIENT-WHATSAPP-CONSENT-V1',@user,clock_timestamp())
                """,
                new NpgsqlParameter("order", order), new NpgsqlParameter("org", org), new NpgsqlParameter("user", user));
            var blocked = await Assert.ThrowsAnyAsync<Exception>(() => RollBackStepAsync(connectionString));
            var postgres = blocked as PostgresException ?? blocked.InnerException as PostgresException;
            Assert.NotNull(postgres);
            Assert.Equal(AddRecipientNoticeConsents.DowngradeBlockedMessage, postgres.MessageText);
            Assert.True(await TableExistsAsync(connectionString));
            Assert.Equal(AddRecipientNoticeConsents.MigrationId, (await HistoryAsync(connectionString))[^1]);
            Assert.Equal(0L, await ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM pg_constraint WHERE conname='recipient_notice_consents_downgrade_guard'"));

            // The migrator, in the owner's tenant context, is the one role the append-only trigger lets delete.
            await ExecuteAsMigratorAsync(connectionString,
                $$"""
                SELECT set_config('app.current_org_ids','{{{org:D}}}',false);
                DELETE FROM orders.recipient_notice_consents WHERE order_id='{{order:D}}';
                """);

            // Without consents the Down drops exactly the table; the order stays and the lane is pending again.
            await RollBackStepAsync(connectionString);
            Assert.False(await TableExistsAsync(connectionString));
            Assert.DoesNotContain(AddRecipientNoticeConsents.MigrationId, await HistoryAsync(connectionString));
            Assert.Equal("PENDING", await LaneStatusAsync(coordinator, connectionString));
            Assert.Equal(1L, await ScalarAsync<long>(connectionString, $"SELECT count(*) FROM orders.orders WHERE id='{order:D}'"));
            await AssertBaselineAsync(connectionString);

            // Up again reproduces the same catalog.
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.All(
                await coordinator.AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            await AssertCanonicalShapeAsync(connectionString);
            await AssertBaselineAsync(connectionString);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    // ------------------------------------------------------------------ createOrder (quote_snapshot_to_order)

    [PostgreSqlContractFact]
    public async Task CreateOrder_records_a_ticked_box_once_with_its_actor_and_the_server_time_and_audits_only_the_boolean()
    {
        await using var ticked = new SyntheticOrderScenario(fixture);
        await ticked.InitializeAsync(createOrder: false);
        await using var unticked = new SyntheticOrderScenario(fixture);
        await unticked.InitializeAsync(createOrder: false);
        await using var scope = CreateScope();

        var command = Command(ticked, "ntf004-consent-0001") with { WhatsAppConsentVersion = RecipientNoticeConsentPolicy.Version };
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var created = await scope.Orders.CreateAsync(command, CancellationToken.None);
        Assert.Equal(created, await scope.Orders.CreateAsync(command, CancellationToken.None));
        var otherAnswer = await Assert.ThrowsAsync<OrderConflictException>(() => scope.Orders.CreateAsync(
            command with { WhatsAppConsentVersion = null }, CancellationToken.None));
        Assert.Equal(OrderConflictCode.IdempotencyConflict, otherAnswer.Code);
        var otherVersion = await Assert.ThrowsAsync<OrderConflictException>(() => scope.Orders.CreateAsync(
            command with { WhatsAppConsentVersion = "NTF-RECIPIENT-WHATSAPP-CONSENT-V2" }, CancellationToken.None));
        Assert.Equal(OrderConflictCode.InvalidRequest, otherVersion.Code);

        var plain = await scope.Orders.CreateAsync(Command(unticked, "ntf004-consent-0002"), CancellationToken.None);

        await using (var verify = fixture.AdminDataSource.CreateCommand(
            """
            SELECT c.owner_org_id,c.channel,c.consent_version,c.attested_by,c.attested_at,o.created_at,
              (SELECT count(*) FROM orders.recipient_notice_consents x WHERE x.order_id=@order),
              (SELECT count(*) FROM orders.recipient_notice_consents x WHERE x.order_id=@plain),
              (SELECT payload_redacted::text FROM platform.audit_logs a WHERE a.entity_id=@order AND a.action='ORDER_CREATED'),
              (SELECT payload_redacted::text FROM platform.audit_logs a WHERE a.entity_id=@plain AND a.action='ORDER_CREATED'),
              (SELECT count(*) FROM platform.audit_logs a WHERE a.entity_id=@order AND a.action='ORDER_CREATED'),
              (SELECT payload::text FROM orders.order_events e WHERE e.order_id=@order),
              (SELECT string_agg(payload::text,'|') FROM platform.outbox_events x WHERE x.aggregate_id=@order),
              row_to_json(c)::text
            FROM orders.recipient_notice_consents c JOIN orders.orders o ON o.id=c.order_id
            WHERE c.order_id=@order
            """))
        {
            verify.Parameters.AddWithValue("order", created.Id);
            verify.Parameters.AddWithValue("plain", plain.Id);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(ticked.OrganizationId, reader.GetGuid(0));
            Assert.Equal("WHATSAPP", reader.GetString(1));
            Assert.Equal(RecipientNoticeConsentPolicy.Version, reader.GetString(2));
            Assert.Equal(ticked.UserId, reader.GetGuid(3));
            var attestedAt = reader.GetFieldValue<DateTimeOffset>(4);
            Assert.InRange(attestedAt, before, DateTimeOffset.UtcNow.AddSeconds(5));
            // The server time of the creation transaction: the order's own instant.
            Assert.Equal(reader.GetFieldValue<DateTimeOffset>(5), attestedAt);
            Assert.Equal(1L, reader.GetInt64(6));
            Assert.Equal(0L, reader.GetInt64(7));
            using (var audit = JsonDocument.Parse(reader.GetString(8)))
            {
                Assert.True(audit.RootElement.GetProperty("whatsapp_consent_attested").GetBoolean());
            }

            using (var audit = JsonDocument.Parse(reader.GetString(9)))
            {
                Assert.False(audit.RootElement.GetProperty("whatsapp_consent_attested").GetBoolean());
            }

            Assert.Equal(1L, reader.GetInt64(10));
            // Nothing but the boolean leaves the consent row: no version in the audit, the event or the outbox.
            foreach (var json in new[] { reader.GetString(8), reader.GetString(11), reader.GetString(12) })
            {
                Assert.DoesNotContain(RecipientNoticeConsentPolicy.Version, json, StringComparison.Ordinal);
                Assert.DoesNotContain("phone", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
            }

            // The row holds ids, a channel, a version and an instant only.
            using var row = JsonDocument.Parse(reader.GetString(13));
            Assert.Equal(
                ["attested_at", "attested_by", "channel", "consent_version", "order_id", "owner_org_id"],
                row.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        }
    }

    [PostgreSqlContractFact]
    public async Task A_failure_at_or_after_the_consent_rolls_it_back_with_the_order()
    {
        foreach (var stage in Enum.GetValues<OrderCreationStage>().Where(stage => stage >= OrderCreationStage.RecipientNoticeConsentInserted))
        {
            await using var scenario = new SyntheticOrderScenario(fixture);
            await scenario.InitializeAsync(createOrder: false);
            await using var scope = CreateScope(new ThrowAtStage(stage));

            await Assert.ThrowsAsync<InjectedFailure>(() => scope.Orders.CreateAsync(
                Command(scenario, $"ntf004-consent-rollback-{(int)stage:D2}") with
                {
                    WhatsAppConsentVersion = RecipientNoticeConsentPolicy.Version,
                },
                CancellationToken.None));

            Assert.Equal(0L, await ScalarAsync<long>(fixture.DeploymentConnectionString,
                $"SELECT count(*) FROM orders.recipient_notice_consents WHERE owner_org_id='{scenario.OrganizationId:D}'"));
            Assert.Equal(0L, await ScalarAsync<long>(fixture.DeploymentConnectionString,
                $"SELECT count(*) FROM orders.orders WHERE owner_org_id='{scenario.OrganizationId:D}'"));
        }
    }

    // ------------------------------------------------------------------ append-only, RLS and the Worker reader

    [PostgreSqlContractFact]
    public async Task Consents_are_append_only_tenant_isolated_and_read_by_the_Worker_under_RLS()
    {
        await using var owner = new SyntheticOrderScenario(fixture);
        await owner.InitializeAsync(createOrder: false);
        await using var stranger = new SyntheticOrderScenario(fixture);
        await stranger.InitializeAsync(createOrder: false);
        await using var scope = CreateScope();
        var consented = await scope.Orders.CreateAsync(
            Command(owner, "ntf004-append-only-0001") with { WhatsAppConsentVersion = RecipientNoticeConsentPolicy.Version },
            CancellationToken.None);
        var foreign = await scope.Orders.CreateAsync(Command(stranger, "ntf004-append-only-0002"), CancellationToken.None);

        // paqueteria_app: the owner sees its row and no one else's; it cannot change, remove or forge one.
        await using (var app = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", owner.UserId, [owner.OrganizationId]))
        {
            Assert.Equal(1L, await CountAsync(app, consented.Id));
            await app.RollbackAsync();
        }

        await using (var app = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", stranger.UserId, [stranger.OrganizationId]))
        {
            Assert.Equal(0L, await CountAsync(app, consented.Id));
            await app.RollbackAsync();
        }

        foreach (var statement in new[]
                 {
                     $"UPDATE orders.recipient_notice_consents SET attested_at=clock_timestamp() WHERE order_id='{consented.Id:D}'",
                     $"DELETE FROM orders.recipient_notice_consents WHERE order_id='{consented.Id:D}'",
                     "TRUNCATE orders.recipient_notice_consents",
                 })
        {
            await using var app = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", owner.UserId, [owner.OrganizationId]);
            var denied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, statement));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        // A row for another organization's order, or under another organization, is refused by the tenant policy.
        await using (var app = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", owner.UserId, [owner.OrganizationId]))
        {
            var forged = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app,
                $"""
                INSERT INTO orders.recipient_notice_consents(order_id,owner_org_id,channel,consent_version,attested_by,attested_at)
                VALUES ('{foreign.Id:D}','{stranger.OrganizationId:D}','WHATSAPP','NTF-RECIPIENT-WHATSAPP-CONSENT-V1','{owner.UserId:D}',clock_timestamp())
                """));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, forged.SqlState);
        }

        // The checks admit only WHATSAPP and the V1 version, and one row per order and channel.
        foreach (var (values, state) in new[]
                 {
                     ("'EMAIL','NTF-RECIPIENT-WHATSAPP-CONSENT-V1'", PostgresErrorCodes.CheckViolation),
                     ("'WHATSAPP','NTF-RECIPIENT-WHATSAPP-CONSENT-V2'", PostgresErrorCodes.CheckViolation),
                     ("'WHATSAPP','NTF-RECIPIENT-WHATSAPP-CONSENT-V1'", PostgresErrorCodes.UniqueViolation),
                 })
        {
            await using var app = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", owner.UserId, [owner.OrganizationId]);
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app,
                $"""
                INSERT INTO orders.recipient_notice_consents(order_id,owner_org_id,channel,consent_version,attested_by,attested_at)
                VALUES ('{consented.Id:D}','{owner.OrganizationId:D}',{values},'{owner.UserId:D}',clock_timestamp())
                """));
            Assert.Equal(state, refused.SqlState);
        }

        // paqueteria_worker reads under RLS and writes nothing.
        await using (var worker = await TenantTransaction.BeginAsync(fixture.WorkerDataSource, "paqueteria_worker", Guid.Empty, [owner.OrganizationId]))
        {
            Assert.Equal(1L, await CountAsync(worker, consented.Id));
            var denied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(worker,
                $"""
                INSERT INTO orders.recipient_notice_consents(order_id,owner_org_id,channel,consent_version,attested_by,attested_at)
                VALUES ('{consented.Id:D}','{owner.OrganizationId:D}','WHATSAPP','NTF-RECIPIENT-WHATSAPP-CONSENT-V1','{owner.UserId:D}',clock_timestamp())
                """));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        // Even the owner of the table cannot change a consent outside the migrator role's own maintenance.
        await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync())
        {
            await using var update = new NpgsqlCommand(
                $"UPDATE orders.recipient_notice_consents SET attested_at=clock_timestamp() WHERE order_id='{consented.Id:D}'",
                connection);
            var appendOnly = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, appendOnly.SqlState);
            Assert.Contains("orders.recipient_notice_consents is append-only", appendOnly.MessageText, StringComparison.Ordinal);
        }

        // The Worker's own reader, composed as the Worker composes it.
        await using var services = WorkerServices();
        var reader = services.GetRequiredService<IRecipientNoticeConsentReader>();
        Assert.Equal("PostgreSqlRecipientNoticeConsentReader", reader.GetType().Name);
        Assert.True(await reader.HasWhatsAppConsentAsync(owner.OrganizationId, consented.Id, CancellationToken.None));
        Assert.False(await reader.HasWhatsAppConsentAsync(stranger.OrganizationId, consented.Id, CancellationToken.None));
        Assert.False(await reader.HasWhatsAppConsentAsync(stranger.OrganizationId, foreign.Id, CancellationToken.None));
        Assert.False(await reader.HasWhatsAppConsentAsync(owner.OrganizationId, Guid.NewGuid(), CancellationToken.None));
        Assert.False(await reader.HasWhatsAppConsentAsync(Guid.Empty, consented.Id, CancellationToken.None));
    }

    // ------------------------------------------------------------------ CSV-001 commit (one box for the file)

    [PostgreSqlContractFact]
    public async Task A_CSV_commit_records_one_consent_per_created_order_and_binds_the_box_to_the_batch()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(createOrder: false);
        var second = await CloneQuoteAsync(scenario);
        var third = await CloneQuoteAsync(scenario);
        await using var scope = CreateScope();
        var commit = new CsvOrderImportCommitService(scope.Orders, scope.Batches);
        var rows = new[]
        {
            Row(2, scenario.QuoteId),
            Row(3, second),
        };

        var result = await commit.CommitAsync(
            new CsvOrderImportCommitCommand(scenario.UserId, scenario.OrganizationId, "ntf004-csv-batch-0001", "digest-ntf004-a",
                rows, "synthetic-request-id", true, RecipientNoticeConsentPolicy.Version),
            CancellationToken.None);
        Assert.Equal(2, result.CreatedRows);
        var replay = await commit.CommitAsync(
            new CsvOrderImportCommitCommand(scenario.UserId, scenario.OrganizationId, "ntf004-csv-batch-0001", "digest-ntf004-a",
                rows, "synthetic-request-id", true, RecipientNoticeConsentPolicy.Version),
            CancellationToken.None);
        Assert.Equal(result.Rows.Select(row => row.OrderId), replay.Rows.Select(row => row.OrderId));
        await Assert.ThrowsAsync<CsvOrderImportBatchConflictException>(() => commit.CommitAsync(
            new CsvOrderImportCommitCommand(scenario.UserId, scenario.OrganizationId, "ntf004-csv-batch-0001", "digest-ntf004-a",
                rows, "synthetic-request-id", true),
            CancellationToken.None));

        var unticked = await commit.CommitAsync(
            new CsvOrderImportCommitCommand(scenario.UserId, scenario.OrganizationId, "ntf004-csv-batch-0002", "digest-ntf004-b",
                [Row(2, third)], "synthetic-request-id", true),
            CancellationToken.None);
        Assert.Equal(1, unticked.CreatedRows);

        var created = result.Rows.Select(row => row.OrderId!.Value).ToArray();
        Assert.Equal(
            2L,
            await ScalarAsync<long>(fixture.DeploymentConnectionString,
                $"""
                SELECT count(*) FROM orders.recipient_notice_consents
                WHERE order_id IN ('{created[0]:D}','{created[1]:D}') AND owner_org_id='{scenario.OrganizationId:D}'
                  AND attested_by='{scenario.UserId:D}' AND consent_version='NTF-RECIPIENT-WHATSAPP-CONSENT-V1'
                """));
        Assert.Equal(
            2L,
            await ScalarAsync<long>(fixture.DeploymentConnectionString,
                $"SELECT count(*) FROM orders.recipient_notice_consents WHERE owner_org_id='{scenario.OrganizationId:D}'"));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(fixture.DeploymentConnectionString,
                $"SELECT count(*) FROM orders.recipient_notice_consents WHERE order_id='{unticked.Rows[0].OrderId!.Value:D}'"));
    }

    // ------------------------------------------------------------------ helpers

    private static async Task AssertCanonicalShapeAsync(string connectionString)
    {
        Assert.Equal(
            AddRecipientNoticeConsents.Columns,
            await ListAsync(connectionString,
                """
                SELECT column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default,'')
                FROM information_schema.columns
                WHERE table_schema='orders' AND table_name='recipient_notice_consents'
                ORDER BY column_name COLLATE "C"
                """));
        Assert.Equal(
            AddRecipientNoticeConsents.Constraints,
            await ListAsync(connectionString,
                """
                SELECT conname || ':' || pg_get_constraintdef(oid) FROM pg_constraint
                WHERE conrelid='orders.recipient_notice_consents'::regclass AND contype <> 'n' ORDER BY conname COLLATE "C"
                """));
        Assert.Equal(
            "true|true|paqueteria_migrator",
            await ScalarAsync<string>(connectionString,
                "SELECT relrowsecurity::text || '|' || relforcerowsecurity::text || '|' || pg_get_userbyid(relowner) FROM pg_class WHERE oid='orders.recipient_notice_consents'::regclass"));
        Assert.Equal(
            $"recipient_notice_consents_tenant|*|{AddRecipientNoticeConsents.PolicyExpression}|{AddRecipientNoticeConsents.PolicyExpression}",
            await ScalarAsync<string>(connectionString,
                """
                SELECT string_agg(polname || '|' || polcmd::text || '|' || pg_get_expr(polqual,polrelid) || '|' || pg_get_expr(polwithcheck,polrelid), ',')
                FROM pg_policy WHERE polrelid='orders.recipient_notice_consents'::regclass
                """));
        Assert.Equal(
            "CREATE TRIGGER recipient_notice_consents_append_only BEFORE DELETE OR UPDATE ON orders.recipient_notice_consents FOR EACH ROW EXECUTE FUNCTION platform.reject_runtime_mutation()",
            await ScalarAsync<string>(connectionString,
                "SELECT string_agg(pg_get_triggerdef(oid), ',') FROM pg_trigger WHERE tgrelid='orders.recipient_notice_consents'::regclass AND NOT tgisinternal"));
        Assert.Equal(
            AddRecipientNoticeConsents.ExpectedAcl,
            await ScalarAsync<string>(connectionString,
                """
                SELECT string_agg(pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type || ':' || acl.is_grantable::text,
                  ',' ORDER BY pg_get_userbyid(acl.grantee) COLLATE "C", acl.privilege_type COLLATE "C")
                FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) acl
                WHERE c.oid='orders.recipient_notice_consents'::regclass AND acl.grantee <> c.relowner
                """));
        // No runtime role can read or write the table any other way, and only the migrator owns it.
        Assert.False(await ScalarAsync<bool>(connectionString,
            """
            SELECT bool_or(has_table_privilege(r,'orders.recipient_notice_consents',p))
            FROM unnest(ARRAY['paqueteria_realtime','paqueteria_bootstrap','paqueteria_outbox_executor']) r
            CROSS JOIN unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE']) p
            """));
    }

    private async Task<IReadOnlyList<string>> HistoryAsync(string connectionString) =>
        await ListAsync(connectionString, "SELECT \"MigrationId\" FROM platform.__ef_migrations_history_orders ORDER BY 1");

    private static async Task<bool> TableExistsAsync(string connectionString) =>
        await ScalarAsync<bool>(connectionString, "SELECT to_regclass('orders.recipient_notice_consents') IS NOT NULL");

    private static async Task<string> LaneStatusAsync(ModuleMigrationCoordinator coordinator, string connectionString) =>
        (await coordinator.PlanAsync(connectionString, CancellationToken.None))
            .Single(state => state.Module == Module)
            .Status;

    /// <summary>
    /// Rolls the step back exactly as EF Core's migrator does, as <c>paqueteria_migrator</c>: its Down operations and the
    /// removal of its history row, in one transaction.
    /// </summary>
    private static async Task RollBackStepAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(OrdersDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_orders", "platform");
            })
            .Options;
        await using var context = new OrdersDbContext(options, new TenantDatabaseExecutionState());
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(
            assembly.Migrations[AddRecipientNoticeConsents.MigrationId],
            context.Database.ProviderName!);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(migration.DownOperations);
        var history = context.GetService<IHistoryRepository>().GetDeleteScript(AddRecipientNoticeConsents.MigrationId);

        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var sql in commands.Select(command => command.CommandText).Append(history))
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task AssertBaselineAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await new DatabaseBaselineAssertions().AssertAsync(connection, transaction);
        await transaction.RollbackAsync();
    }

    /// <summary>An organization, a user, a city, two locations, a quote and a DRAFT order, written as the deployer.</summary>
    private static async Task<(Guid Org, Guid User, Guid Order)> InsertMinimalOrderAsync(string connectionString)
    {
        var (org, user, city, origin, destination, quote, order) =
            (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await ExecuteAsync(connectionString,
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
              VALUES (@org,'NTF-004 lane','NTF-004 lane','BUSINESS');
            INSERT INTO identity.users(id,identity_subject) VALUES (@user,@subject);
            INSERT INTO locations.cities(id,state_code,name,timezone) VALUES (@city,'SI',@city_name,'America/Mazatlan');
            INSERT INTO locations.locations(id,owner_org_id,city_id,point,address_ciphertext,address_summary,pii_key_version) VALUES
              (@origin,@org,@city,public.ST_SetSRID(public.ST_MakePoint(-107.40,24.80),4326),decode('00','hex'),'Synthetic origin','test-v1'),
              (@destination,@org,@city,public.ST_SetSRID(public.ST_MakePoint(-107.39,24.81),4326),decode('01','hex'),'Synthetic destination','test-v1');
            INSERT INTO pricing.quotes(
              id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,consolidated_route,
              subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
              request_snapshot_redacted,package_snapshot,breakdown,input_hash,status,expires_at)
            VALUES (@quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,10000,0,0,10000,5000,'MXN','ntf004-v1',
              '{}','[{"description":"synthetic package","weight_grams":500,"declared_value_cents":10000}]','{}',@hash,'USED',
              clock_timestamp()+interval '1 day');
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,
              consolidated_route,payer_type,status,subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,
              currency,pricing_policy_version,package_snapshot,cod_expected_cents,version)
            SELECT @order,@public_id,q.id,q.owner_org_id,q.city_id,q.origin_location_id,q.destination_location_id,q.service_type,
              q.pricing_tier,q.consolidated_route,'SENDER','DRAFT',q.subtotal_cents,q.discount_cents,q.tax_cents,q.total_cents,
              q.minimum_total_cents_snapshot,q.currency,q.pricing_policy_version,q.package_snapshot,0,1
            FROM pricing.quotes q WHERE q.id=@quote;
            """,
            new("org", org), new("user", user), new("subject", $"oidc|ntf004|{user:N}"), new("city", city),
            new("city_name", $"NTF-004 {city:N}"), new("origin", origin), new("destination", destination),
            new("quote", quote), new("hash", new byte[32]), new("order", order), new("public_id", $"NTF004-{order:N}"));
        return (org, user, order);
    }

    private async Task<Guid> CloneQuoteAsync(SyntheticOrderScenario scenario)
    {
        var id = Guid.NewGuid();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO pricing.quotes(
              id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,consolidated_route,
              subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
              request_snapshot_redacted,package_snapshot,breakdown,input_hash,status,expires_at)
            SELECT @id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,consolidated_route,
              subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
              request_snapshot_redacted,package_snapshot,breakdown,input_hash,status,expires_at
            FROM pricing.quotes WHERE id=@quote
            """,
            SyntheticOrderScenario.P("id", id),
            SyntheticOrderScenario.P("quote", scenario.QuoteId));
        return id;
    }

    private static CsvOrderImportOrderRow Row(int number, Guid quote) =>
        new(number, quote, "SENDER", "terms-synthetic-v1", "privacy-synthetic-v1", AcceptedAtClient, "WEB");

    private static CreateOrderCommand Command(SyntheticOrderScenario scenario, string key) => new(
        scenario.UserId,
        scenario.OrganizationId,
        key,
        scenario.QuoteId,
        "SENDER",
        new OrderAcceptanceInput("terms-synthetic-v1", "privacy-synthetic-v1", AcceptedAtClient, "WEB"),
        "synthetic-request-id",
        RestrictedGoodsAcknowledged: true);

    private Scope CreateScope(IOrderCreationFailureInjector? failureInjector = null)
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new OrdersDbContext(options, state);
        var transactions = new TenantTransactionContext<OrdersDbContext>(context, state);
        var ordersOptions = Options.Create(new OrdersOptions
        {
            Provider = OrdersProviderKind.PostgreSql,
            CommandTimeoutSeconds = 30,
            PageSize = 2,
            IdempotencyLifetimeMinutes = 60,
            PublicIdCollisionRetryCount = 2,
        });
        var orders = new QuoteSnapshotToOrderCoordinator(
            transactions,
            new CryptographicOrderPublicIdGenerator(),
            failureInjector ?? new NoOpOrderCreationFailureInjector(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            ordersOptions,
            new SystemClock());
        return new Scope(context, orders, new PostgreSqlCsvOrderImportBatchIdempotencyStore(transactions, ordersOptions, new SystemClock()));
    }

    private ServiceProvider WorkerServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PaqueteriaWorker"] = fixture.WorkerConnectionString,
            })
            .Build();
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddOrdersRecipientNoticeReaders(configuration, new TestingEnvironment())
            .BuildServiceProvider();
    }

    private static async Task<long> CountAsync(TenantTransaction tenant, Guid order)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM orders.recipient_notice_consents WHERE order_id=@order", tenant.Connection, tenant.Transaction);
        command.Parameters.AddWithValue("order", order);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(TenantTransaction tenant, string sql)
    {
        await using var command = new NpgsqlCommand(sql, tenant.Connection, tenant.Transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsAppAsync(string connectionString, Guid org, Guid user, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            $$"""
            SET LOCAL ROLE paqueteria_app;
            SELECT set_config('app.current_user_id','{{user:D}}',true);
            SELECT set_config('app.current_org_ids','{{{org:D}}}',true);
            """,
            connection,
            transaction))
        {
            await context.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddRange(parameters);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task ExecuteAsMigratorAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsMigratorInTransactionAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_migrator", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        await transaction.RollbackAsync();
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string[]> ListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private sealed class Scope(OrdersDbContext context, IOrderService orders, ICsvOrderImportBatchIdempotencyStore batches)
        : IAsyncDisposable
    {
        internal IOrderService Orders { get; } = orders;

        internal ICsvOrderImportBatchIdempotencyStore Batches { get; } = batches;

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class ThrowAtStage(OrderCreationStage target) : IOrderCreationFailureInjector
    {
        public Task OnStageAsync(OrderCreationStage stage, CancellationToken cancellationToken) =>
            stage == target ? throw new InjectedFailure(stage) : Task.CompletedTask;
    }

    private sealed class InjectedFailure(OrderCreationStage stage) : Exception($"Injected order creation failure at {stage}.");

    private sealed class TestingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Paqueteria.Worker";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
