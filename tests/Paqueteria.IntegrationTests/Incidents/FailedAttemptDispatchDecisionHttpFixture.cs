using System.Security.Cryptography;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Paqueteria.IntegrationTests.Orders;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Incidents;

/// <summary>
/// INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11: the real API over a real PostgreSQL baseline with the Incidents lane
/// migrated, the Orders, Drivers and Dispatch providers and the synthetic incident PII protector. The mock
/// active-driver identity is the OWN motorcycle driver of the synthetic seed; this fixture gives it the document the
/// eligibility policy requires and provisions the mock active-dispatcher as a DISPATCHER. Orders reach DELIVERING with
/// their canonical history and an ACTIVE assignment to that driver; everything after it goes through HTTP.
/// </summary>
public sealed class FailedAttemptDispatchDecisionHttpFixture : IAsyncLifetime
{
    internal static readonly Guid TenantId = MockIdentityProfiles.ViewerOrganizationId;

    private static readonly Guid CityId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly string[] HistoryToDelivering =
        ["CONFIRMED", "READY_FOR_PICKUP", "ASSIGNED", "AT_PICKUP", "PICKED_UP", "IN_TRANSIT", "DELIVERING"];

    internal DispatchDecisionApiFactory Api { get; } = new();

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
                INSERT INTO drivers.driver_documents(
                  id,driver_id,org_id,document_type,object_key,sha256,status)
                VALUES (
                  gen_random_uuid(),@driver,@org,'IDENTITY',@object_key,decode(repeat('ab',32),'hex'),'VALID');
                """,
                new NpgsqlParameter("dispatcher", DispatcherUserId),
                new NpgsqlParameter("org", TenantId),
                new NpgsqlParameter("driver", PostgreSqlSecurityWebApplicationFactory.ActiveDriverId),
                new NpgsqlParameter("object_key", $"synthetic/inc002/{PostgreSqlSecurityWebApplicationFactory.ActiveDriverId:N}"));
        }
        catch
        {
            await Api.DisposeAsync();
            throw;
        }
    }

    public Task DisposeAsync() => Api.DisposeAsync();

    /// <summary>
    /// One DELIVERING order of the tenant with its canonical status history (versions 1..7), one package, an ACTIVE
    /// assignment to the mock active driver and the DELIVERY_PHOTO the driver took at the door, after the order
    /// entered DELIVERING.
    /// </summary>
    internal async Task<SeededDelivery> SeedDeliveringOrderAsync()
    {
        var seeded = new SeededDelivery(Guid.NewGuid(), Guid.NewGuid(), HistoryToDelivering.Length);
        var upload = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
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
              10000,'MXN','inc002-http-v1','{}','[]','{}',@input_hash,'USED',clock_timestamp()+interval '1 day');
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,
              service_type,pricing_tier,consolidated_route,payer_type,status,subtotal_cents,discount_cents,
              tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
              package_snapshot,cod_expected_cents,version)
            VALUES (
              @order,@public_id,@quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,
              'SENDER','DELIVERING',10000,0,0,10000,10000,'MXN','inc002-http-v1','[]',0,@version);
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,aggregate_version,event_type,payload,actor_id,occurred_at)
            SELECT gen_random_uuid(),@order,@org,path.version::integer,'ORDER_STATUS_CHANGED',
                   jsonb_build_object(
                     'previous_status',COALESCE((@statuses::text[])[path.version::integer - 1],'DRAFT'),
                     'new_status',path.status),
                   @dispatcher,clock_timestamp()-make_interval(secs => (@version - path.version::integer + 2))
            FROM unnest(@statuses::text[]) WITH ORDINALITY AS path(status,version);
            INSERT INTO orders.package_items(
              id,order_id,owner_org_id,description,weight_grams,declared_value_cents,dimensions_mm)
            VALUES (
              gen_random_uuid(),@order,@org,'synthetic package',500,0,
              '{"length_mm":100,"width_mm":80,"height_mm":60}');
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,driver_id,assignment_type,status,cost_cents,accepted_at,created_at)
            VALUES (
              gen_random_uuid(),@order,@org,@driver,'OWN','ACTIVE',1500,
              clock_timestamp()-interval '1 hour',clock_timestamp()-interval '1 hour');
            INSERT INTO custody.proof_upload_sessions(
              id,order_id,owner_org_id,requested_by,object_key_quarantine,
              expected_content_type,maximum_bytes,status,expires_at)
            VALUES (
              @upload,@order,@org,@driver_user,@quarantine,'image/jpeg',1024,'CONSUMED',
              clock_timestamp()+interval '1 day');
            INSERT INTO custody.proofs(
              id,order_id,owner_org_id,upload_session_id,proof_type,object_key,sha256,
              content_type,size_bytes,captured_at,created_by,created_at)
            VALUES (
              @proof,@order,@org,@upload,'DELIVERY_PHOTO',@object_key,
              decode(repeat('07',32),'hex'),'image/jpeg',100,clock_timestamp(),@driver_user,clock_timestamp());
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("origin", Guid.NewGuid());
            command.Parameters.AddWithValue("destination", Guid.NewGuid());
            command.Parameters.AddWithValue("org", TenantId);
            command.Parameters.AddWithValue("city", CityId);
            command.Parameters.AddWithValue("quote", Guid.NewGuid());
            command.Parameters.AddWithValue("input_hash", RandomNumberGenerator.GetBytes(32));
            command.Parameters.AddWithValue("order", seeded.OrderId);
            command.Parameters.AddWithValue("public_id", $"INC002-{seeded.OrderId:N}");
            command.Parameters.AddWithValue("version", seeded.Version);
            command.Parameters.AddWithValue("statuses", HistoryToDelivering);
            command.Parameters.AddWithValue("dispatcher", DispatcherUserId);
            command.Parameters.AddWithValue("driver", PostgreSqlSecurityWebApplicationFactory.ActiveDriverId);
            command.Parameters.AddWithValue("driver_user", ActiveDriverUserId);
            command.Parameters.AddWithValue("upload", upload);
            command.Parameters.AddWithValue("quarantine", $"quarantine/{upload:N}");
            command.Parameters.AddWithValue("proof", seeded.ProofId);
            command.Parameters.AddWithValue("object_key", $"proofs/{upload:N}");
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return seeded;
    }

    /// <summary>What the incident store holds for the order's incidents, read as the cluster administrator.</summary>
    internal async Task<StoredReport> ReadReportAsync(Guid incidentId)
    {
        await using var connection = new NpgsqlConnection(Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT i.next_action IS NULL,i.severity,i.reason_code,i.created_by,
                   i.description_ciphertext,i.pii_key_version
            FROM incidents.incidents i
            WHERE i.id=@incident
            """,
            connection);
        command.Parameters.AddWithValue("incident", incidentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new StoredReport(
            reader.GetBoolean(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetGuid(3),
            reader.GetFieldValue<byte[]>(4),
            reader.GetString(5));
    }

    internal async Task<long> CountIncidentsAsync(Guid orderId)
    {
        await using var connection = new NpgsqlConnection(Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM incidents.incidents WHERE order_id=@order",
            connection);
        command.Parameters.AddWithValue("order", orderId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The user of the mock active-driver identity in the synthetic seed.</summary>
    internal static readonly Guid ActiveDriverUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11");

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>The PostgreSQL security host with the Orders, Drivers and Dispatch providers and the mock PII protector.</summary>
internal sealed class DispatchDecisionApiFactory : PostgreSqlSecurityWebApplicationFactory
{
    internal const string PiiKeyVersion = "inc002-http-v1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration(configuration =>
        {
            var settings = new Dictionary<string, string?>(OrderTransitionGuardsApiFactory.DriverEligibilitySettings)
            {
                ["Orders:Provider"] = "PostgreSql",
                ["Orders:CommandTimeoutSeconds"] = "5",
                ["Drivers:Provider"] = "PostgreSql",
                ["Drivers:CommandTimeoutSeconds"] = "5",
                ["Dispatch:Provider"] = "PostgreSql",
                ["Dispatch:CommandTimeoutSeconds"] = "5",
                ["Incidents:PiiProtector"] = "Mock",
                ["Incidents:PiiKeyVersion"] = PiiKeyVersion,
            };
            configuration.AddInMemoryCollection(settings);
        });
    }
}

internal sealed record SeededDelivery(Guid OrderId, Guid ProofId, int Version);

internal sealed record StoredReport(
    bool NextActionIsNull,
    string Severity,
    string ReasonCode,
    Guid CreatedBy,
    byte[] DescriptionCiphertext,
    string PiiKeyVersion);
