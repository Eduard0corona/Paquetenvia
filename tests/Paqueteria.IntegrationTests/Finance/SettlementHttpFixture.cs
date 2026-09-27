using System.Security.Cryptography;
using Finance.Infrastructure.Persistence;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Paqueteria.Infrastructure.Tenancy;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Finance;

/// <summary>
/// The real API over a real PostgreSQL baseline with the SET-001 ledger lane migrated and the Finance
/// provider enabled: authentication, identity bootstrap, tenant selection and the settlement service all
/// run their production PostgreSQL implementations on the runtime application role. Only the operational
/// facts a settlement derives from are seeded directly; every settlement is produced through HTTP.
/// </summary>
public sealed class SettlementHttpFixture : IAsyncLifetime
{
    /// <summary>The active organization every mock identity profile of the seed belongs to.</summary>
    internal static readonly Guid TenantId = MockIdentityProfiles.ViewerOrganizationId;

    /// <summary>An organization none of the settlement actors belongs to.</summary>
    internal static readonly Guid ForeignTenantId = MockIdentityProfiles.OperationsOrganizationId;

    /// <summary>
    /// The mock subject of <see cref="MockIdentityProfiles.ActiveDispatcher"/>, provisioned here as an active
    /// FINANCE member: identity is resolved from PostgreSQL, so its role is whatever this seed grants.
    /// </summary>
    internal const string FinanceProfile = MockIdentityProfiles.ActiveDispatcher;

    /// <summary>An MFA-satisfied DISPATCHER: dispatch capability never extends to settlements.</summary>
    internal const string DispatcherProfile = MockIdentityProfiles.LocalDispatcherMfa;

    private static readonly Guid CityId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    internal SettlementApiFactory Api { get; } = new();

    internal Guid FinanceUserId { get; } = Guid.NewGuid();

    internal Guid DispatcherUserId { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await Api.InitializeAsync();
        try
        {
            await MigrateFinanceAsync();
            await ExecuteAdminAsync(
                """
                INSERT INTO identity.users(id,identity_subject,status) VALUES
                  (@finance,'mock-subject-active-dispatcher','ACTIVE'),
                  (@dispatcher,'local-subject-dispatcher-mfa','ACTIVE');
                INSERT INTO organizations.organization_memberships(
                  id,user_id,organization_id,role,status,is_default) VALUES
                  (gen_random_uuid(),@finance,@org,'FINANCE','ACTIVE',true),
                  (gen_random_uuid(),@dispatcher,@org,'DISPATCHER','ACTIVE',true);
                """,
                new NpgsqlParameter("finance", FinanceUserId),
                new NpgsqlParameter("dispatcher", DispatcherUserId),
                new NpgsqlParameter("org", TenantId));
        }
        catch
        {
            await Api.DisposeAsync();
            throw;
        }
    }

    public Task DisposeAsync() => Api.DisposeAsync();

    /// <summary>A fresh DRIVER of <paramref name="organizationId"/>, so every test settles its own payee.</summary>
    internal async Task<Guid> SeedDriverAsync(Guid organizationId)
    {
        var user = Guid.NewGuid();
        var driver = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO identity.users(id,identity_subject,status) VALUES (@user,'set001-http-' || @user::text,'ACTIVE');
            INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default)
            VALUES (gen_random_uuid(),@user,@org,'DRIVER','ACTIVE',true);
            INSERT INTO drivers.driver_profiles(id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
            VALUES (@driver,@user,@org,@city,'OWN','MOTORCYCLE','ACTIVE');
            """,
            new NpgsqlParameter("user", user),
            new NpgsqlParameter("driver", driver),
            new NpgsqlParameter("org", organizationId),
            new NpgsqlParameter("city", CityId));
        return driver;
    }

    /// <summary>
    /// One order of <paramref name="driverId"/> in <paramref name="orderStatus"/> with an ACTIVE OWN assignment
    /// and the ORD-002 status change that recorded <paramref name="outcome"/> at <paramref name="occurredAt"/>.
    /// </summary>
    internal async Task<SeededWork> SeedWorkAsync(
        Guid driverId,
        string orderStatus,
        string outcome,
        DateTimeOffset occurredAt,
        long costCents,
        long codExpectedCents = 0)
    {
        var work = new SeededWork(Guid.NewGuid(), Guid.NewGuid(), costCents);
        await ExecuteAdminAsync(
            """
            INSERT INTO locations.locations(
              id,owner_org_id,city_id,point,address_ciphertext,address_summary,pii_key_version)
            VALUES
              (@origin,@org,@city,public.ST_SetSRID(public.ST_MakePoint(-107.40,24.80),4326),
               decode('00','hex'),'Synthetic origin','test-v1'),
              (@destination,@org,@city,public.ST_SetSRID(public.ST_MakePoint(-107.39,24.81),4326),
               decode('01','hex'),'Synthetic destination','test-v1');
            INSERT INTO pricing.quotes(
              id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,
              consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,
              minimum_total_cents_snapshot,currency,pricing_policy_version,request_snapshot_redacted,
              package_snapshot,breakdown,input_hash,status,expires_at)
            VALUES (
              @quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,10000,0,0,10000,
              10000,'MXN','set001-http-v1','{}','[]','{}',@input_hash,'USED',clock_timestamp()+interval '1 day');
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,
              service_type,pricing_tier,consolidated_route,payer_type,status,subtotal_cents,discount_cents,
              tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
              package_snapshot,cod_expected_cents,version)
            VALUES (
              @order,@public_id,@quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,
              'SENDER',@status,10000,0,0,10000,10000,'MXN','set001-http-v1','[]',@cod,2);
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,driver_id,route_id,assignment_type,status,cost_cents,accepted_at,created_at)
            VALUES (@assignment,@order,@org,@driver,NULL,'OWN','ACTIVE',@cost,@accepted,@accepted);
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,operator_org_id,aggregate_version,event_type,public_event_code,
              payload,actor_id,occurred_at)
            VALUES (gen_random_uuid(),@order,@org,NULL,2,'ORDER_STATUS_CHANGED',@outcome,
              jsonb_build_object('new_status',@outcome),NULL,@occurred);
            """,
            new NpgsqlParameter("origin", Guid.NewGuid()),
            new NpgsqlParameter("destination", Guid.NewGuid()),
            new NpgsqlParameter("org", TenantId),
            new NpgsqlParameter("city", CityId),
            new NpgsqlParameter("quote", Guid.NewGuid()),
            new NpgsqlParameter("input_hash", RandomNumberGenerator.GetBytes(32)),
            new NpgsqlParameter("order", work.OrderId),
            new NpgsqlParameter("public_id", $"SET001-{work.OrderId:N}"),
            new NpgsqlParameter("status", orderStatus),
            new NpgsqlParameter("cod", codExpectedCents),
            new NpgsqlParameter("assignment", work.AssignmentId),
            new NpgsqlParameter("driver", driverId),
            new NpgsqlParameter("cost", costCents),
            new NpgsqlParameter("accepted", occurredAt.AddHours(-2)),
            new NpgsqlParameter("outcome", outcome),
            new NpgsqlParameter("occurred", occurredAt));
        return work;
    }

    /// <summary>An incident on the order, in the canonical baseline shape this fixture migrates.</summary>
    internal Task SeedIncidentAsync(Guid orderId, string status) => ExecuteAdminAsync(
        """
        INSERT INTO incidents.incidents(
          id,order_id,owner_org_id,incident_type,severity,status,custody_acquired,created_by)
        VALUES (gen_random_uuid(),@order,@org,'FAILED_DELIVERY_ATTEMPT','MEDIUM',@status,false,@actor);
        """,
        new NpgsqlParameter("order", orderId),
        new NpgsqlParameter("org", TenantId),
        new NpgsqlParameter("status", status),
        new NpgsqlParameter("actor", DispatcherUserId));

    /// <summary>A settlement of another tenant, written directly so no settlement actor ever saw it.</summary>
    internal async Task<Guid> SeedForeignSettlementAsync()
    {
        var settlement = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO finance.settlements(id,owner_org_id,payee_type,payee_id,status,total_cents,period_from,period_to)
            VALUES (@id,@org,'DRIVER',gen_random_uuid(),'DRAFT',0,DATE '2026-09-14',DATE '2026-09-20');
            """,
            new NpgsqlParameter("id", settlement),
            new NpgsqlParameter("org", ForeignTenantId));
        return settlement;
    }

    /// <summary>
    /// AI05-LIST-SETTLEMENTS: <paramref name="count"/> empty DRAFT settlements of one organization, all in
    /// one statement so they share created_at and the keyset has to break the tie by id.
    /// </summary>
    internal async Task<IReadOnlyList<Guid>> SeedDraftSettlementsAsync(
        Guid organizationId,
        int count,
        DateOnly periodFrom,
        DateOnly periodTo)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        await ExecuteAdminAsync(
            """
            INSERT INTO finance.settlements(id,owner_org_id,payee_type,payee_id,status,total_cents,period_from,period_to)
            SELECT id,@org,'DRIVER',gen_random_uuid(),'DRAFT',0,@from,@to FROM unnest(@ids) AS seeded(id);
            """,
            new NpgsqlParameter("ids", ids),
            new NpgsqlParameter("org", organizationId),
            new NpgsqlParameter("from", periodFrom),
            new NpgsqlParameter("to", periodTo));
        return ids;
    }

    internal async Task<SettlementSnapshot> ReadAsync(Guid settlementId)
    {
        await using var connection = new NpgsqlConnection(Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT s.status,s.total_cents,
                   (SELECT count(*) FROM finance.settlement_lines l WHERE l.settlement_id=s.id),
                   (SELECT COALESCE(sum(l.amount_cents),0) FROM finance.settlement_lines l WHERE l.settlement_id=s.id),
                   (SELECT count(*) FROM platform.audit_logs a WHERE a.entity_id=s.id AND a.entity_type='Settlement')
            FROM finance.settlements s
            WHERE s.id=@id
            """,
            connection);
        command.Parameters.AddWithValue("id", settlementId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetDecimal(3), reader.GetInt64(4));
    }

    internal async Task<long> CountSettlementsForAsync(Guid driverId)
    {
        await using var connection = new NpgsqlConnection(Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM finance.settlements WHERE payee_id=@driver", connection);
        command.Parameters.AddWithValue("driver", driverId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task MigrateFinanceAsync()
    {
        await using var connection = new NpgsqlConnection(Api.AdminConnectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<FinanceDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(FinanceDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_finance", "platform");
            })
            .Options;
        await using var context = new FinanceDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync();
    }

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>The PostgreSQL security host with the Finance provider switched on.</summary>
internal sealed class SettlementApiFactory : PostgreSqlSecurityWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Finance:Provider"] = "PostgreSql",
                ["Finance:CommandTimeoutSeconds"] = "5",
            }));
    }
}

internal sealed record SeededWork(Guid OrderId, Guid AssignmentId, long CostCents)
{
    public string Source => $"dispatch.assignments/{AssignmentId:D}";
}

internal sealed record SettlementSnapshot(string Status, long TotalCents, long Lines, decimal LineSum, long Audits);
