using Finance.Infrastructure.Persistence.Migrations;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// SET-001 Slice 1 as deployment runs it: the Finance lane of the canonical migrator adopts the AI-06
/// settlement tables, records itself in its own history table, preserves every existing row, can be
/// applied again without effect, and refuses — without touching anything — a ledger that already
/// breaks one of the invariants it is about to enforce.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class SettlementLedgerMigrationPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Module = "Finance";
    private const string HistoryTable = "platform.__ef_migrations_history_finance";

    private static readonly Guid OrganizationId = Guid.Parse("5e7001a0-0000-4000-8000-000000000001");
    private static readonly Guid OtherOrganizationId = Guid.Parse("5e7001a0-0000-4000-8000-000000000002");
    private static readonly Guid OrderId = Guid.Parse("5e7001a0-0000-4000-8000-000000000003");
    private static readonly Guid DriverId = Guid.Parse("5e7001a0-0000-4000-8000-000000000004");

    [PostgreSqlContractFact]
    public async Task Canonical_migrator_verifies_applies_and_records_the_finance_lane()
    {
        var verified = Assert.Single(ModuleMigrationCoordinator.VerifySources(), state => state.Module == Module);
        Assert.Equal(HistoryTable, verified.HistoryTable);
        Assert.Equal(EnforceSettlementLedgerIntegrity.MigrationId, verified.MigrationId);
        Assert.Equal("VERIFIED", verified.Status);

        // The shared contract fixture was built by the same migrator, so it carries the lane as well.
        var applied = Assert.Single(
            await new ModuleMigrationCoordinator().AssertAsync(fixture.DeploymentConnectionString, CancellationToken.None),
            state => state.Module == Module);
        Assert.Equal(HistoryTable, applied.HistoryTable);
        Assert.Equal("APPLIED", applied.Status);

        await AssertLedgerInstalledAsync(fixture.DeploymentConnectionString);
    }

    [PostgreSqlContractFact]
    public async Task A_populated_pre_SET001_installation_adopts_the_ledger_without_losing_a_row()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("set001upgrade");
        try
        {
            await DeployBaselineAsync(connectionString);
            await SeedInstallationAsync(connectionString);

            // Rows as a pre-SET-001 installation holds them, written by the cluster administrator: a
            // reconciled DRAFT, a PAID settlement with a signed adjustment, a VOID settlement that
            // shares an economic source with the DRAFT, and a legacy line of reserved vocabulary.
            await ExecuteAsync(connectionString, """
                INSERT INTO finance.settlements(
                  id,owner_org_id,payee_type,payee_id,status,total_cents,period_from,period_to,created_at) VALUES
                  ('5e7001a0-0000-4000-8000-0000000000a1',@org,'DRIVER',@driver,'DRAFT',1500,'2026-09-01','2026-09-07',@created),
                  ('5e7001a0-0000-4000-8000-0000000000a2',@org,'DRIVER',@driver,'PAID',700,'2026-08-25','2026-08-31',@created),
                  ('5e7001a0-0000-4000-8000-0000000000a3',@org,'DRIVER',@driver,'VOID',900,'2026-08-25','2026-08-31',@created),
                  ('5e7001a0-0000-4000-8000-0000000000a4',@org,'ALLY',@driver,'CALCULATED',250,'2026-09-01','2026-09-07',@created);
                INSERT INTO finance.settlement_lines(
                  settlement_id,owner_org_id,order_id,line_type,amount_cents,source_reference,created_at) VALUES
                  ('5e7001a0-0000-4000-8000-0000000000a1',@org,@order,'DELIVERY',1000,'dispatch.assignments/legacy-1',@created),
                  ('5e7001a0-0000-4000-8000-0000000000a1',@org,@order,'RETURN',500,'dispatch.assignments/legacy-2',@created),
                  ('5e7001a0-0000-4000-8000-0000000000a2',@org,@order,'DELIVERY',800,'dispatch.assignments/legacy-3',@created),
                  ('5e7001a0-0000-4000-8000-0000000000a2',@org,NULL,'ADJUSTMENT',-100,'platform.audit_logs/legacy-4',@created),
                  ('5e7001a0-0000-4000-8000-0000000000a3',@org,@order,'DELIVERY',900,'dispatch.assignments/legacy-1',@created),
                  ('5e7001a0-0000-4000-8000-0000000000a4',@org,NULL,'BONUS',250,'legacy/bonus-5',@created);
                """,
                P("org", OrganizationId), P("driver", DriverId), P("order", OrderId),
                P("created", new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero)));
            var before = await LedgerSnapshotAsync(connectionString);
            var coordinator = new ModuleMigrationCoordinator();
            Assert.Equal("PENDING", await LaneStatusAsync(coordinator, connectionString));

            await coordinator.ApplyAsync(connectionString, CancellationToken.None);

            Assert.All(
                await coordinator.AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            Assert.Equal(before, await LedgerSnapshotAsync(connectionString));
            await AssertLedgerInstalledAsync(connectionString);

            // Idempotent: the lane's SQL executed again by the migrator, then the whole migrator again.
            await ExecuteAsMigratorAsync(connectionString, EnforceSettlementLedgerIntegrity.LedgerSql);
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.Equal(before, await LedgerSnapshotAsync(connectionString));
            await AssertLedgerInstalledAsync(connectionString);

            // The adopted rows are now under the ledger's guards, even for the cluster administrator.
            var mutation = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
                "UPDATE finance.settlement_lines SET amount_cents=0 WHERE settlement_id='5e7001a0-0000-4000-8000-0000000000a2'"));
            Assert.Equal("42501", mutation.SqlState);
            var reopen = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
                "UPDATE finance.settlements SET status='DRAFT' WHERE id='5e7001a0-0000-4000-8000-0000000000a2'"));
            Assert.Equal("23514", reopen.SqlState);
            Assert.Equal(before, await LedgerSnapshotAsync(connectionString));

            // History drift in the Finance lane fails closed before any lane is touched.
            await ExecuteAsync(connectionString,
                $"INSERT INTO {HistoryTable}(\"MigrationId\",\"ProductVersion\") VALUES ('20991231000000_Unexpected','0.0.0')");
            Assert.Equal("DRIFT", await LaneStatusAsync(coordinator, connectionString));
            var drift = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.ApplyAsync(connectionString, CancellationToken.None));
            Assert.Equal($"Migration history drift detected for {Module}.", drift.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.AssertAsync(connectionString, CancellationToken.None));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task An_installation_whose_ledger_breaks_an_invariant_is_refused_and_left_untouched()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("set001refuse");
        try
        {
            await DeployBaselineAsync(connectionString);
            await SeedInstallationAsync(connectionString);
            await ExecuteAsync(connectionString, """
                INSERT INTO finance.settlements(
                  id,owner_org_id,payee_type,payee_id,status,total_cents,period_from,period_to) VALUES
                  ('5e7001a0-0000-4000-8000-0000000000b1',@org,'DRIVER',@driver,'DRAFT',1000,'2026-09-01','2026-09-07');
                INSERT INTO finance.settlement_lines(
                  settlement_id,owner_org_id,order_id,line_type,amount_cents,source_reference) VALUES
                  ('5e7001a0-0000-4000-8000-0000000000b1',@org,@order,'DELIVERY',1000,'dispatch.assignments/shared');
                """,
                P("org", OrganizationId), P("driver", DriverId), P("order", OrderId));
            var coordinator = new ModuleMigrationCoordinator();

            async Task AssertRefusedAsync(string expected)
            {
                var before = await LedgerSnapshotAsync(connectionString);
                var refusal = await Assert.ThrowsAnyAsync<Exception>(
                    () => coordinator.ApplyAsync(connectionString, CancellationToken.None));
                var error = FindPostgresException(refusal);
                Assert.Contains(expected, error.MessageText, StringComparison.Ordinal);

                // The lane rolled back as a whole: not recorded, no guard, no audit policy, no row changed.
                Assert.Equal("PENDING", await LaneStatusAsync(coordinator, connectionString));
                Assert.Equal(before, await LedgerSnapshotAsync(connectionString));
                Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
                    SELECT count(*) FROM pg_trigger
                    WHERE tgrelid IN ('finance.settlements'::regclass,'finance.settlement_lines'::regclass)
                      AND NOT tgisinternal
                    """));
                Assert.Equal("settlement_lines_tenant,settlements_tenant", await ScalarAsync<string>(connectionString, """
                    SELECT string_agg(policyname,',' ORDER BY policyname) FROM pg_policies
                    WHERE schemaname='finance' AND tablename IN ('settlements','settlement_lines')
                    """));
            }

            // A total that does not reconcile with its lines.
            await ExecuteAsync(connectionString,
                "UPDATE finance.settlements SET total_cents=999 WHERE id='5e7001a0-0000-4000-8000-0000000000b1'");
            await AssertRefusedAsync("settlements do not reconcile with their lines");
            await ExecuteAsync(connectionString,
                "UPDATE finance.settlements SET total_cents=1000 WHERE id='5e7001a0-0000-4000-8000-0000000000b1'");

            // A line filed under another tenant's settlement.
            await ExecuteAsync(connectionString, """
                INSERT INTO finance.settlement_lines(
                  settlement_id,owner_org_id,order_id,line_type,amount_cents,source_reference) VALUES
                  ('5e7001a0-0000-4000-8000-0000000000b1',@other,NULL,'ADJUSTMENT',0,'legacy/cross-tenant');
                """,
                P("other", OtherOrganizationId));
            await AssertRefusedAsync("belong to another tenant than their settlement");
            await ExecuteAsync(connectionString,
                "DELETE FROM finance.settlement_lines WHERE source_reference='legacy/cross-tenant'");

            // One economic source paid by two settlements that are not VOID.
            await ExecuteAsync(connectionString, """
                INSERT INTO finance.settlements(
                  id,owner_org_id,payee_type,payee_id,status,total_cents,period_from,period_to) VALUES
                  ('5e7001a0-0000-4000-8000-0000000000b2',@org,'DRIVER',@driver,'DRAFT',1000,'2026-09-08','2026-09-14');
                INSERT INTO finance.settlement_lines(
                  settlement_id,owner_org_id,order_id,line_type,amount_cents,source_reference) VALUES
                  ('5e7001a0-0000-4000-8000-0000000000b2',@org,@order,'RETURN',1000,'dispatch.assignments/shared');
                """,
                P("org", OrganizationId), P("driver", DriverId), P("order", OrderId));
            await AssertRefusedAsync("settled by more than one settlement that is not VOID");
            await ExecuteAsync(connectionString,
                "UPDATE finance.settlements SET status='VOID' WHERE id='5e7001a0-0000-4000-8000-0000000000b2'");

            // A canonical column that is not where the ledger rules expect it.
            await ExecuteAsync(connectionString,
                "ALTER TABLE finance.settlement_lines RENAME COLUMN source_reference TO source_ref");
            await AssertRefusedAsync("finance.settlement_lines columns do not match the canonical AI-06 contract");
            await ExecuteAsync(connectionString,
                "ALTER TABLE finance.settlement_lines RENAME COLUMN source_ref TO source_reference");

            // Once the ledger is consistent, the very same installation adopts it with every row intact.
            var consistent = await LedgerSnapshotAsync(connectionString);
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.All(
                await coordinator.AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            Assert.Equal(consistent, await LedgerSnapshotAsync(connectionString));
            await AssertLedgerInstalledAsync(connectionString);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    /// <summary>
    /// The installed ledger, exactly: the lane recorded once in a migrator-owned history table, the
    /// canonical columns untouched, the five guards bound to their functions with both reconciliation
    /// triggers deferred, invoker-rights guard functions, the two ledger indexes, and the security
    /// posture of the canonical tables unchanged.
    /// </summary>
    private static async Task AssertLedgerInstalledAsync(string connectionString)
    {
        Assert.Equal(EnforceSettlementLedgerIntegrity.MigrationId, await ScalarAsync<string>(connectionString,
            $"SELECT string_agg(\"MigrationId\",',' ORDER BY \"MigrationId\") FROM {HistoryTable}"));
        Assert.Equal("paqueteria_migrator", await ScalarAsync<string>(connectionString,
            $"SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid='{HistoryTable}'::regclass"));

        Assert.Equal(
            "id:uuid:NO,owner_org_id:uuid:NO,payee_type:text:NO,payee_id:uuid:NO,status:text:NO," +
            "total_cents:bigint:NO,period_from:date:NO,period_to:date:NO,created_at:timestamp with time zone:NO",
            await ColumnsAsync(connectionString, "settlements"));
        Assert.Equal(
            "id:uuid:NO,settlement_id:uuid:NO,owner_org_id:uuid:NO,order_id:uuid:YES,line_type:text:NO," +
            "amount_cents:bigint:NO,source_reference:text:NO,created_at:timestamp with time zone:NO",
            await ColumnsAsync(connectionString, "settlement_lines"));

        Assert.Equal(
            "settlement_lines_append_only:platform.reject_runtime_mutation:false," +
            "settlement_lines_guard_insert:finance.guard_settlement_line_insert:false," +
            "settlement_lines_reconciled:finance.require_settlement_reconciliation:true," +
            "settlements_guard_mutation:finance.guard_settlement_mutation:false," +
            "settlements_reconciled:finance.require_settlement_reconciliation:true",
            await ScalarAsync<string>(connectionString, """
                SELECT string_agg(
                  t.tgname || ':' || t.tgfoid::regproc::text || ':' || (t.tgdeferrable AND t.tginitdeferred)::text,
                  ',' ORDER BY t.tgname)
                FROM pg_trigger t
                WHERE t.tgrelid IN ('finance.settlements'::regclass,'finance.settlement_lines'::regclass)
                  AND NOT t.tgisinternal AND t.tgenabled='O'
                """));

        Assert.Equal(
            "guard_settlement_line_insert:paqueteria_migrator:false," +
            "guard_settlement_mutation:paqueteria_migrator:false," +
            "require_settlement_reconciliation:paqueteria_migrator:false",
            await ScalarAsync<string>(connectionString, """
                SELECT string_agg(p.proname || ':' || pg_get_userbyid(p.proowner) || ':' || p.prosecdef::text,
                  ',' ORDER BY p.proname)
                FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='finance'
                """));

        Assert.Equal(
            "settlement_lines_owner_source_idx,settlement_lines_pkey," +
            "settlement_lines_settlement_id_source_reference_key,settlement_lines_settlement_idx," +
            "settlements_owner_payee_period_idx,settlements_pkey",
            await ScalarAsync<string>(connectionString, """
                SELECT string_agg(c.relname,',' ORDER BY c.relname)
                FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid
                WHERE i.indrelid IN ('finance.settlements'::regclass,'finance.settlement_lines'::regclass)
                """));

        Assert.Equal(2L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM pg_class
            WHERE oid IN ('finance.settlements'::regclass,'finance.settlement_lines'::regclass)
              AND relrowsecurity AND relforcerowsecurity
              AND pg_get_userbyid(relowner)='paqueteria_migrator'
            """));
        Assert.Equal("settlement_lines_tenant,settlements_tenant", await ScalarAsync<string>(connectionString, """
            SELECT string_agg(policyname,',' ORDER BY policyname) FROM pg_policies
            WHERE schemaname='finance' AND tablename IN ('settlements','settlement_lines')
            """));
        Assert.False(await ScalarAsync<bool>(connectionString,
            "SELECT rolbypassrls FROM pg_roles WHERE rolname='paqueteria_migrator'"));
    }

    private static async Task DeployBaselineAsync(string connectionString)
    {
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        Assert.Equal(
            DatabaseBaselineApplyStatus.Applied,
            (await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString)).Status);
    }

    /// <summary>Two organizations and a real order, written as a pre-SET-001 installation holds them.</summary>
    private static Task SeedInstallationAsync(string connectionString) =>
        ExecuteAsync(connectionString, """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES
              (@org,'SET-001 Upgrade Organization','SET-001 Upgrade','BUSINESS'),
              (@other,'SET-001 Other Organization','SET-001 Other','BUSINESS');
            INSERT INTO locations.cities(id,state_code,name,timezone)
              VALUES (@city,'SI','SET-001 Upgrade City','America/Mazatlan');
            INSERT INTO locations.locations(
              id,owner_org_id,city_id,point,address_ciphertext,address_summary,pii_key_version) VALUES
              (@origin,@org,@city,public.ST_SetSRID(public.ST_MakePoint(-107.40,24.80),4326),
               decode('00','hex'),'Upgrade origin','test-v1'),
              (@destination,@org,@city,public.ST_SetSRID(public.ST_MakePoint(-107.39,24.81),4326),
               decode('01','hex'),'Upgrade destination','test-v1');
            INSERT INTO pricing.quotes(
              id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,
              pricing_tier,consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,
              minimum_total_cents_snapshot,currency,pricing_policy_version,request_snapshot_redacted,
              package_snapshot,breakdown,input_hash,status,expires_at)
            VALUES (
              @quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,
              1000,0,0,1000,500,'MXN','set001-upgrade-policy-v1',
              '{"city":"upgrade"}','[{"description":"upgrade package","weight_grams":500}]','{}',
              @input_hash,'ACTIVE',clock_timestamp()+interval '1 day');
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,
              service_type,pricing_tier,consolidated_route,payer_type,status,subtotal_cents,
              discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,
              pricing_policy_version,package_snapshot,cod_expected_cents,version)
            SELECT @order,'SET001-UPGRADE',q.id,q.owner_org_id,q.city_id,q.origin_location_id,
              q.destination_location_id,q.service_type,q.pricing_tier,q.consolidated_route,'SENDER',
              'DRAFT',q.subtotal_cents,q.discount_cents,q.tax_cents,q.total_cents,
              q.minimum_total_cents_snapshot,q.currency,q.pricing_policy_version,q.package_snapshot,0,1
            FROM pricing.quotes q WHERE q.id=@quote;
            """,
            P("org", OrganizationId),
            P("other", OtherOrganizationId),
            P("city", Guid.Parse("5e7001a0-0000-4000-8000-000000000005")),
            P("origin", Guid.Parse("5e7001a0-0000-4000-8000-000000000006")),
            P("destination", Guid.Parse("5e7001a0-0000-4000-8000-000000000007")),
            P("quote", Guid.Parse("5e7001a0-0000-4000-8000-000000000008")),
            P("order", OrderId),
            P("input_hash", new byte[32]));

    /// <summary>
    /// Every settlement and line, read by the cluster administrator so row security hides nothing.
    /// </summary>
    private static async Task<string> LedgerSnapshotAsync(string connectionString) =>
        await ScalarAsync<string>(connectionString, """
            SELECT COALESCE((SELECT string_agg(row_to_json(s)::text, E'\n' ORDER BY s.id) FROM finance.settlements s),'')
              || E'\n--\n' ||
              COALESCE((SELECT string_agg(row_to_json(l)::text, E'\n' ORDER BY l.id) FROM finance.settlement_lines l),'')
            """);

    private static Task<string> ColumnsAsync(string connectionString, string table) =>
        ScalarAsync<string>(connectionString, $"""
            SELECT string_agg(column_name || ':' || data_type || ':' || is_nullable, ',' ORDER BY ordinal_position)
            FROM information_schema.columns
            WHERE table_schema='finance' AND table_name='{table}'
            """);

    private static async Task<string> LaneStatusAsync(ModuleMigrationCoordinator coordinator, string connectionString) =>
        (await coordinator.PlanAsync(connectionString, CancellationToken.None))
            .Single(state => state.Module == Module)
            .Status;

    private static PostgresException FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected a PostgreSQL refusal, observed {exception}.");
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

    private static async Task ExecuteAsync(
        string connectionString,
        string sql,
        params NpgsqlParameter[] parameters)
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

    private static NpgsqlParameter P(string name, object value) => new(name, value);
}
