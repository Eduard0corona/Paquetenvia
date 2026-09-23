using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// The INC-001 upgrade path of an installation that already holds incidents. The canonical
/// baseline is deployed, incidents are written into it exactly as a pre-INC-001 installation
/// would hold them, and only then does the canonical migrator run. The interesting part is that
/// <c>incidents.incidents</c> keeps FORCE ROW LEVEL SECURITY and <c>paqueteria_migrator</c> stays
/// NOBYPASSRLS throughout: an upgrade that could not see the existing rows would backfill nothing
/// and then be refused by SET NOT NULL, which does see them.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class IncidentsUpgradePostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly Guid OrganizationId = Guid.Parse("5f1c1e00-0000-4000-8000-000000000001");
    private static readonly Guid UserId = Guid.Parse("5f1c1e00-0000-4000-8000-000000000002");
    private static readonly Guid OrderId = Guid.Parse("5f1c1e00-0000-4000-8000-000000000003");

    /// <summary>The incident that failed in transit: custody was acquired before the attempt.</summary>
    private static readonly Guid CustodyIncidentId = Guid.Parse("5f1c1e00-0000-4000-8000-000000000004");

    /// <summary>The incident that failed at pickup: custody was never acquired.</summary>
    private static readonly Guid PickupIncidentId = Guid.Parse("5f1c1e00-0000-4000-8000-000000000005");

    [PostgreSqlContractFact]
    public async Task A_populated_pre_INC001_installation_upgrades_without_losing_a_single_incident()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("incupgrade");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            Assert.Equal(
                DatabaseBaselineApplyStatus.Applied,
                (await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString)).Status);

            await SeedPreUpgradeIncidentsAsync(connectionString);

            // The starting point really is pre-INC-001: none of its columns exist yet.
            Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
                SELECT count(*) FROM information_schema.columns
                WHERE table_schema='incidents' AND table_name='incidents'
                  AND column_name IN ('reason_code','next_action','occurred_at','sla_due_at')
                """));
            Assert.Equal(2L, await ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM incidents.incidents"));

            // The canonical migrator, as deployment runs it, against the populated installation.
            await new ModuleMigrationCoordinator().ApplyAsync(
                connectionString, CancellationToken.None, azureOwnershipBridge: true);
            Assert.All(
                await new ModuleMigrationCoordinator().AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));

            await AssertHistoryPreservedAsync(connectionString);
            await AssertBackfillIsCompleteAsync(connectionString);
            await AssertConstraintsAsync(connectionString);
            await AssertSecurityPostureAsync(connectionString);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    /// <summary>
    /// Writes the rows a pre-INC-001 installation holds: the canonical AI-06 incident columns and
    /// nothing else. The connection is the cluster administrator, which is how an installation
    /// arrives at this state — it is the upgrade that has to cope with RLS, not the seed.
    /// </summary>
    private static async Task SeedPreUpgradeIncidentsAsync(string connectionString)
    {
        await ExecuteAsync(connectionString, """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
              VALUES (@org,'INC-001 Upgrade Organization','INC-001 Upgrade','BUSINESS');
            INSERT INTO identity.users(id,identity_subject) VALUES (@user,@subject);
            INSERT INTO organizations.organization_memberships(
              id,user_id,organization_id,role,status,is_default)
              VALUES (gen_random_uuid(),@user,@org,'DISPATCHER','ACTIVE',true);
            INSERT INTO locations.cities(id,state_code,name,timezone)
              VALUES (@city,'SI','INC-001 Upgrade City','America/Mazatlan');
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
              1000,0,0,1000,500,'MXN','inc001-upgrade-policy-v1',
              '{"city":"upgrade"}','[{"description":"upgrade package","weight_grams":500}]','{}',
              @input_hash,'ACTIVE',clock_timestamp()+interval '1 day');
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,
              service_type,pricing_tier,consolidated_route,payer_type,status,subtotal_cents,
              discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,
              pricing_policy_version,package_snapshot,cod_expected_cents,version)
            SELECT @order,@public_id,q.id,q.owner_org_id,q.city_id,q.origin_location_id,
              q.destination_location_id,q.service_type,q.pricing_tier,q.consolidated_route,'SENDER',
              'DELIVERING',q.subtotal_cents,q.discount_cents,q.tax_cents,q.total_cents,
              q.minimum_total_cents_snapshot,q.currency,q.pricing_policy_version,q.package_snapshot,0,1
            FROM pricing.quotes q WHERE q.id=@quote;

            -- Two incidents of the shape AI-06 published before INC-001: one that had already
            -- acquired custody and one that failed at pickup, so both backfill branches run.
            INSERT INTO incidents.incidents(
              id,order_id,owner_org_id,incident_type,severity,status,custody_acquired,
              created_by,created_at) VALUES
              (@custody_incident,@order,@org,'FAILED_DELIVERY_ATTEMPT','HIGH','OPEN',true,
               @user,@created_at),
              (@pickup_incident,@order,@org,'FAILED_PICKUP_ATTEMPT','LOW','INVESTIGATING',false,
               @user,@created_at);
            """,
            P("org", OrganizationId),
            P("user", UserId),
            P("subject", $"oidc|inc001-upgrade|{UserId:N}"),
            P("city", Guid.Parse("5f1c1e00-0000-4000-8000-000000000006")),
            P("origin", Guid.Parse("5f1c1e00-0000-4000-8000-000000000007")),
            P("destination", Guid.Parse("5f1c1e00-0000-4000-8000-000000000008")),
            P("quote", Guid.Parse("5f1c1e00-0000-4000-8000-000000000009")),
            P("order", OrderId),
            P("public_id", "ARC002-INC001-UPGRADE"),
            P("input_hash", new byte[32]),
            P("custody_incident", CustodyIncidentId),
            P("pickup_incident", PickupIncidentId),
            P("created_at", new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>The identity and the history of every pre-existing incident survive untouched.</summary>
    private static async Task AssertHistoryPreservedAsync(string connectionString)
    {
        Assert.Equal(2L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM incidents.incidents"));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT id,incident_type,severity,status,custody_acquired,created_by,created_at
            FROM incidents.incidents ORDER BY incident_type
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(CustodyIncidentId, reader.GetGuid(0));
        Assert.Equal("FAILED_DELIVERY_ATTEMPT", reader.GetString(1));
        Assert.Equal("HIGH", reader.GetString(2));
        Assert.Equal("OPEN", reader.GetString(3));
        Assert.True(reader.GetBoolean(4));
        Assert.Equal(UserId, reader.GetGuid(5));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            reader.GetFieldValue<DateTimeOffset>(6));

        Assert.True(await reader.ReadAsync());
        Assert.Equal(PickupIncidentId, reader.GetGuid(0));
        Assert.Equal("INVESTIGATING", reader.GetString(3));
        Assert.False(reader.GetBoolean(4));

        Assert.False(await reader.ReadAsync());
    }

    /// <summary>Every INC-001 field is populated, and populated from the row it belongs to.</summary>
    private static async Task AssertBackfillIsCompleteAsync(string connectionString)
    {
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM incidents.incidents
            WHERE reason_code IS NULL OR next_action IS NULL
               OR occurred_at IS NULL OR sla_due_at IS NULL
            """));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT id,reason_code,next_action,occurred_at,sla_due_at,created_at
            FROM incidents.incidents ORDER BY incident_type
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(CustodyIncidentId, reader.GetGuid(0));
        Assert.Equal("FAILED_DELIVERY_ATTEMPT", reader.GetString(1));
        // Custody was acquired, so the derived next action is the one ORD-002 can honour.
        Assert.Equal("RETURNING", reader.GetString(2));
        Assert.Equal(reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(3));
        // HIGH resolves in eight hours, measured from the attempt.
        Assert.Equal(
            reader.GetFieldValue<DateTimeOffset>(3).AddHours(8),
            reader.GetFieldValue<DateTimeOffset>(4));

        Assert.True(await reader.ReadAsync());
        Assert.Equal(PickupIncidentId, reader.GetGuid(0));
        Assert.Equal("FAILED_PICKUP_ATTEMPT", reader.GetString(1));
        // Custody was never acquired, so returning is not derivable.
        Assert.Equal("RESCHEDULED", reader.GetString(2));
        // LOW resolves in seventy-two hours.
        Assert.Equal(
            reader.GetFieldValue<DateTimeOffset>(3).AddHours(72),
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    private static async Task AssertConstraintsAsync(string connectionString)
    {
        Assert.Equal(4L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema='incidents' AND table_name='incidents'
              AND column_name IN ('reason_code','next_action','occurred_at','sla_due_at')
              AND is_nullable='NO'
            """));

        Assert.Equal(3L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM pg_constraint
            WHERE conrelid='incidents.incidents'::regclass AND contype='c'
              AND conname IN (
                'incidents_next_action_check',
                'incidents_sla_due_at_check',
                'incidents_returning_requires_custody_check')
            """));

        Assert.Equal(2L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM pg_constraint
            WHERE conrelid='incidents.incident_evidence'::regclass AND contype='f'
              AND conname IN (
                'incident_evidence_incident_order_owner_fkey',
                'incident_evidence_proof_order_owner_fkey')
            """));

        Assert.Equal(1L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM pg_trigger
            WHERE tgrelid='incidents.incident_evidence'::regclass
              AND tgname='incident_evidence_coherent' AND NOT tgisinternal
            """));
    }

    /// <summary>
    /// The upgrade left the security posture exactly as it found it: forced RLS on both incident
    /// tables, the canonical tenant policies and nothing else, a migrator that still cannot bypass
    /// row security, and ownership unchanged.
    /// </summary>
    private static async Task AssertSecurityPostureAsync(string connectionString)
    {
        Assert.Equal(2L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM pg_class c
            JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='incidents' AND c.relname IN ('incidents','incident_evidence')
              AND c.relrowsecurity AND c.relforcerowsecurity
            """));

        var policies = await ScalarAsync<string>(connectionString, """
            SELECT string_agg(policyname,',' ORDER BY policyname)
            FROM pg_policies WHERE schemaname='incidents'
            """);
        Assert.Equal("incident_evidence_tenant,incidents_tenant", policies);

        Assert.False(await ScalarAsync<bool>(connectionString,
            "SELECT rolbypassrls FROM pg_roles WHERE rolname='paqueteria_migrator'"));

        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM pg_class c
            JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='incidents' AND c.relkind IN ('r','p')
              AND pg_get_userbyid(c.relowner)<>'paqueteria_migrator'
            """));
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
