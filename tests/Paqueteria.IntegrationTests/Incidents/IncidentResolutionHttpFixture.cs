using System.Security.Cryptography;
using Identity.Infrastructure.Mock;
using Npgsql;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Incidents;

/// <summary>
/// The real API over a real PostgreSQL baseline with the INC-001 lane migrated: authentication,
/// identity bootstrap and tenant selection run their PostgreSQL providers, and the incident service
/// is the production one on the runtime application role. Incidents are seeded directly, so every
/// resolution the tests observe went through the HTTP surface.
/// </summary>
public sealed class IncidentResolutionHttpFixture : IAsyncLifetime
{
    /// <summary>The active organization every mock identity profile of the seed belongs to.</summary>
    internal static readonly Guid TenantId = MockIdentityProfiles.ViewerOrganizationId;

    /// <summary>An organization the dispatcher is not a member of.</summary>
    internal static readonly Guid ForeignTenantId = MockIdentityProfiles.OperationsOrganizationId;

    internal const string SeededDescription = "El destinatario no se encontraba en el domicilio.";

    private static readonly Guid CityId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    internal PostgreSqlSecurityWebApplicationFactory Api { get; } = new();

    internal Guid DispatcherUserId { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await Api.InitializeAsync();
        try
        {
            // The mock active-dispatcher identity, provisioned as a DISPATCHER of the tenant.
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

    /// <summary>
    /// Seeds one DELIVERING order of <paramref name="organizationId"/> with one proof and one
    /// incident citing it, all in a single transaction as the deferred evidence rule requires.
    /// </summary>
    internal async Task<SeededIncident> SeedIncidentAsync(Guid organizationId, string status = "OPEN")
    {
        var seeded = new SeededIncident(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var origin = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var quote = Guid.NewGuid();
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
              10000,'MXN','inc001-http-v1','{}','[]','{}',@input_hash,'ACTIVE',clock_timestamp()+interval '1 day');
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,
              service_type,pricing_tier,consolidated_route,payer_type,status,subtotal_cents,discount_cents,
              tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
              package_snapshot,cod_expected_cents,version)
            VALUES (
              @order,@public_id,@quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,
              'SENDER','DELIVERING',10000,0,0,10000,10000,'MXN','inc001-http-v1','[]',0,1);
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
              @incident,@order,@org,'FAILED_DELIVERY_ATTEMPT','MEDIUM',@status,true,
              @ciphertext,'inc001-http-v1','RECIPIENT_ABSENT','RESCHEDULED',
              clock_timestamp()-interval '5 minutes',clock_timestamp()+interval '1 day',@actor);
            INSERT INTO incidents.incident_evidence(
              id,incident_id,order_id,owner_org_id,proof_id,created_by)
            VALUES (gen_random_uuid(),@incident,@order,@org,@proof,@actor);
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("origin", origin);
            command.Parameters.AddWithValue("destination", destination);
            command.Parameters.AddWithValue("org", organizationId);
            command.Parameters.AddWithValue("city", CityId);
            command.Parameters.AddWithValue("quote", quote);
            command.Parameters.AddWithValue("input_hash", RandomNumberGenerator.GetBytes(32));
            command.Parameters.AddWithValue("order", seeded.OrderId);
            command.Parameters.AddWithValue("public_id", $"INC001-{seeded.OrderId:N}");
            command.Parameters.AddWithValue("upload", upload);
            command.Parameters.AddWithValue("actor", DispatcherUserId);
            command.Parameters.AddWithValue("quarantine", $"quarantine/{upload:N}");
            command.Parameters.AddWithValue("proof", seeded.ProofId);
            command.Parameters.AddWithValue("object_key", $"proofs/{upload:N}");
            command.Parameters.AddWithValue("incident", seeded.IncidentId);
            command.Parameters.AddWithValue("status", status);
            command.Parameters.AddWithValue("ciphertext", RandomNumberGenerator.GetBytes(48));
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return seeded;
    }

    internal async Task<IncidentSnapshot> ReadAsync(SeededIncident seeded)
    {
        await using var connection = new NpgsqlConnection(Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT i.status,i.resolved_at,o.status,o.version,
                   (SELECT count(*) FROM orders.order_events e WHERE e.order_id=o.id),
                   (SELECT string_agg(x.id::text||':'||x.proof_id::text,',' ORDER BY x.id)
                      FROM incidents.incident_evidence x WHERE x.incident_id=i.id),
                   (SELECT count(*) FROM platform.audit_logs a
                     WHERE a.entity_id=i.id
                       AND a.action IN ('incidents.incident.resolved','incidents.incident.rejected')),
                   (SELECT count(*) FROM platform.idempotency_keys k
                     WHERE k.resource_id=i.id AND k.scope='INC-001:RESOLVE_INCIDENT')
            FROM incidents.incidents i JOIN orders.orders o ON o.id=i.order_id
            WHERE i.id=@incident
            """,
            connection);
        command.Parameters.AddWithValue("incident", seeded.IncidentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new IncidentSnapshot(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetInt64(7));
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

internal sealed record SeededIncident(Guid IncidentId, Guid OrderId, Guid ProofId);

internal sealed record IncidentSnapshot(
    string Status,
    DateTimeOffset? ResolvedAt,
    string OrderStatus,
    int OrderVersion,
    long OrderEvents,
    string Evidence,
    long ResolutionAudits,
    long ResolutionReservations);
