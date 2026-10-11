using System.Security.Cryptography;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Orders;

/// <summary>
/// ORD-002-API-GUARD-REGISTRY: the real API over a real PostgreSQL baseline (with the INC-001 lane migrated by the
/// base factory) and the mock active-dispatcher identity provisioned as a DISPATCHER of the tenant. Orders, incidents,
/// drivers and packages are seeded directly; the tests drive every transition and assignment through HTTP.
/// </summary>
public sealed class OrderTransitionGuardsHttpFixture : IAsyncLifetime
{
    /// <summary>The organization every seeded row and every request belongs to.</summary>
    internal static readonly Guid TenantId = MockIdentityProfiles.ViewerOrganizationId;

    private static readonly Guid CityId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    internal OrderTransitionGuardsApiFactory Api { get; } = new();

    internal Guid DispatcherUserId { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await Api.InitializeAsync();
        try
        {
            await ExecuteAdminAsync(
                """
                INSERT INTO identity.users(id,identity_subject,status)
                VALUES (@dispatcher,'mock-subject-active-dispatcher','ACTIVE');
                INSERT INTO organizations.organization_memberships(
                  id,user_id,organization_id,role,status,is_default)
                VALUES (gen_random_uuid(),@dispatcher,@org,'DISPATCHER','ACTIVE',true);
                """,
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

    /// <summary>One order of the tenant in <paramref name="status"/>, with its own locations and consumed quote.</summary>
    internal async Task<Guid> SeedOrderAsync(string status, int version, bool claimWindowSet)
    {
        var orderId = Guid.NewGuid();
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
              10000,'MXN','ord002-guards-v1','{}','[]','{}',@input_hash,'USED',clock_timestamp()+interval '1 day');
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,
              service_type,pricing_tier,consolidated_route,payer_type,status,subtotal_cents,discount_cents,
              tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
              package_snapshot,cod_expected_cents,version,claim_window_ends_at)
            VALUES (
              @order,@public_id,@quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,
              'SENDER',@status,10000,0,0,10000,10000,'MXN','ord002-guards-v1','[]',0,@version,
              CASE WHEN @window THEN clock_timestamp()+interval '72 hours' END);
            """,
            new NpgsqlParameter("origin", Guid.NewGuid()),
            new NpgsqlParameter("destination", Guid.NewGuid()),
            new NpgsqlParameter("org", TenantId),
            new NpgsqlParameter("city", CityId),
            new NpgsqlParameter("quote", Guid.NewGuid()),
            new NpgsqlParameter("input_hash", RandomNumberGenerator.GetBytes(32)),
            new NpgsqlParameter("order", orderId),
            new NpgsqlParameter("public_id", $"GRD-{orderId:N}"),
            new NpgsqlParameter("status", status),
            new NpgsqlParameter("version", version),
            new NpgsqlParameter("window", claimWindowSet));
        return orderId;
    }

    /// <summary>
    /// An OPEN incident on the order with the proof it cites, in one transaction as the deferred evidence rule
    /// requires.
    /// </summary>
    internal async Task<Guid> SeedOpenIncidentAsync(Guid orderId)
    {
        var incidentId = Guid.NewGuid();
        var upload = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO custody.proof_upload_sessions(
              id,order_id,owner_org_id,requested_by,object_key_quarantine,
              expected_content_type,maximum_bytes,status,expires_at)
            VALUES (
              @upload,@order,@org,@actor,@quarantine,'image/jpeg',1024,'READY',
              clock_timestamp()+interval '1 day');
            INSERT INTO custody.proofs(
              id,order_id,owner_org_id,upload_session_id,proof_type,object_key,sha256,
              content_type,size_bytes,captured_at,created_by)
            VALUES (
              @proof,@order,@org,@upload,'DELIVERY_PHOTO',@object_key,
              decode(repeat('04',32),'hex'),'image/jpeg',100,clock_timestamp(),@actor);
            INSERT INTO incidents.incidents(
              id,order_id,owner_org_id,incident_type,severity,status,custody_acquired,
              description_ciphertext,pii_key_version,reason_code,next_action,occurred_at,sla_due_at,created_by)
            VALUES (
              @incident,@order,@org,'FAILED_DELIVERY_ATTEMPT','MEDIUM','OPEN',true,
              @ciphertext,'ord002-guards-v1','RECIPIENT_ABSENT','RESCHEDULED',
              clock_timestamp()-interval '5 minutes',clock_timestamp()+interval '1 day',@actor);
            INSERT INTO incidents.incident_evidence(
              id,incident_id,order_id,owner_org_id,proof_id,created_by)
            VALUES (gen_random_uuid(),@incident,@order,@org,@proof,@actor);
            """,
            new NpgsqlParameter("upload", upload),
            new NpgsqlParameter("order", orderId),
            new NpgsqlParameter("org", TenantId),
            new NpgsqlParameter("actor", DispatcherUserId),
            new NpgsqlParameter("quarantine", $"quarantine/{upload:N}"),
            new NpgsqlParameter("proof", Guid.NewGuid()),
            new NpgsqlParameter("object_key", $"proofs/{upload:N}"),
            new NpgsqlParameter("incident", incidentId),
            new NpgsqlParameter("ciphertext", RandomNumberGenerator.GetBytes(48)));
        return incidentId;
    }

    /// <summary>One package within the MOTORCYCLE capacity of <see cref="OrderTransitionGuardsApiFactory"/>.</summary>
    internal Task SeedPackageAsync(Guid orderId) =>
        ExecuteAdminAsync(
            """
            INSERT INTO orders.package_items(
              id,order_id,owner_org_id,description,weight_grams,declared_value_cents,dimensions_mm)
            VALUES (
              gen_random_uuid(),@order,@org,'synthetic package',500,0,
              '{"length_mm":100,"width_mm":80,"height_mm":60}');
            """,
            new NpgsqlParameter("order", orderId),
            new NpgsqlParameter("org", TenantId));

    /// <summary>An active OWN motorcycle driver of the tenant with the one document the policy requires.</summary>
    internal async Task<Guid> SeedEligibleDriverAsync()
    {
        var driverId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO identity.users(id,identity_subject,status)
            VALUES (@user,@subject,'ACTIVE');
            INSERT INTO organizations.organization_memberships(
              id,user_id,organization_id,role,status,is_default)
            VALUES (gen_random_uuid(),@user,@org,'DRIVER','ACTIVE',true);
            INSERT INTO drivers.driver_profiles(
              id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
            VALUES (@driver,@user,@org,@city,'OWN','MOTORCYCLE','ACTIVE');
            INSERT INTO drivers.driver_documents(
              id,driver_id,org_id,document_type,object_key,sha256,status)
            VALUES (
              gen_random_uuid(),@driver,@org,'IDENTITY',@object_key,decode(repeat('ab',32),'hex'),'VALID');
            """,
            new NpgsqlParameter("user", userId),
            new NpgsqlParameter("subject", $"ord002-guards-driver-{userId:N}"),
            new NpgsqlParameter("org", TenantId),
            new NpgsqlParameter("driver", driverId),
            new NpgsqlParameter("city", CityId),
            new NpgsqlParameter("object_key", $"synthetic/ord002/{driverId:N}"));
        return driverId;
    }

    /// <summary>Everything a transition writes for the order: status, version and its status events, outbox and audit rows.</summary>
    internal async Task<OrderTransitionFootprint> ReadOrderAsync(Guid orderId)
    {
        await using var connection = new NpgsqlConnection(Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT o.status,o.version,
                   (SELECT count(*) FROM orders.order_events e
                     WHERE e.order_id=o.id AND e.event_type='ORDER_STATUS_CHANGED'),
                   (SELECT count(*) FROM platform.outbox_events x WHERE x.aggregate_id=o.id),
                   (SELECT count(*) FROM platform.audit_logs a WHERE a.entity_id=o.id)
            FROM orders.orders o
            WHERE o.id=@order
            """,
            connection);
        command.Parameters.AddWithValue("order", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new OrderTransitionFootprint(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    /// <summary>The idempotency reservations stored under <paramref name="idempotencyKey"/>.</summary>
    internal async Task<long> CountTransitionReservationsAsync(string idempotencyKey)
    {
        await using var connection = new NpgsqlConnection(Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM platform.idempotency_keys WHERE idempotency_key=@key",
            connection);
        command.Parameters.AddWithValue("key", idempotencyKey);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    internal async Task<long> CountActiveAssignmentsAsync(Guid orderId, Guid driverId)
    {
        await using var connection = new NpgsqlConnection(Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM dispatch.assignments
            WHERE order_id=@order AND driver_id=@driver AND status='ACCEPTED'
            """,
            connection);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("driver", driverId);
        return (long)(await command.ExecuteScalarAsync())!;
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

/// <summary>The PostgreSQL security host with the Orders, Drivers and Dispatch providers switched on.</summary>
internal sealed class OrderTransitionGuardsApiFactory : PostgreSqlSecurityWebApplicationFactory
{
    /// <summary>
    /// The DRV-001 policy the Drivers provider requires for every vehicle type: an identity document that does not
    /// expire and room for one small package.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> DriverEligibilitySettings { get; } = CreateEligibility();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration(configuration =>
        {
            var settings = new Dictionary<string, string?>(DriverEligibilitySettings)
            {
                ["Orders:Provider"] = "PostgreSql",
                ["Orders:CommandTimeoutSeconds"] = "5",
                ["Drivers:Provider"] = "PostgreSql",
                ["Drivers:CommandTimeoutSeconds"] = "5",
                ["Dispatch:Provider"] = "PostgreSql",
                ["Dispatch:CommandTimeoutSeconds"] = "5",
            };
            configuration.AddInMemoryCollection(settings);
        });
    }

    private static Dictionary<string, string?> CreateEligibility()
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Drivers:Eligibility:NonExpiringDocumentTypes:0"] = "IDENTITY",
        };
        foreach (var vehicle in new[] { "MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER" })
        {
            settings[$"Drivers:Eligibility:RequiredDocumentTypesByVehicleType:{vehicle}:0"] = "IDENTITY";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumPackageCount"] = "4";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumTotalWeightGrams"] = "4000";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumSinglePackageWeightGrams"] = "2000";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumLengthMillimeters"] = "500";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumWidthMillimeters"] = "400";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumHeightMillimeters"] = "300";
        }

        return settings;
    }
}

/// <summary>What a transition writes for one order.</summary>
internal sealed record OrderTransitionFootprint(
    string Status,
    int Version,
    long StatusEvents,
    long OutboxEvents,
    long AuditLogs);
