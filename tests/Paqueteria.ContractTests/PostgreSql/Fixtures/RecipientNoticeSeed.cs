using Npgsql;

namespace Paqueteria.ContractTests.PostgreSql.Fixtures;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-003): synthetic rows for the recipient notice lane tests, written by the
/// deployment principal (no runtime path writes them before NTF-004 and NTF-005). Nothing here is personal data: the
/// locations hold one-byte placeholders and no phone or e-mail.
/// </summary>
internal static class RecipientNoticeSeed
{
    public const string RequestTopic = "orders.recipient-notice-requested";

    /// <summary>An organization of type BUSINESS; its insert provisions the Notifications templates.</summary>
    public static async Task<Guid> InsertOrganizationAsync(NpgsqlConnection admin, string name)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(admin,
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES(@id,@name,@name,'BUSINESS')",
            new NpgsqlParameter("id", id), new NpgsqlParameter("name", name));
        return id;
    }

    /// <summary>One CONFIRMED order of <paramref name="owner"/> and its order event, as the order of a recipient notice.</summary>
    public static async Task<(Guid OrderId, Guid OrderEventId, string PublicId)> InsertOrderAsync(NpgsqlConnection admin, Guid owner)
    {
        var city = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var quote = Guid.NewGuid();
        var order = Guid.NewGuid();
        var orderEvent = Guid.NewGuid();
        var publicId = $"ORD_ntf003{Guid.NewGuid():N}"[..26];
        await ExecuteAsync(admin,
            """
            INSERT INTO locations.cities(id,state_code,name,timezone)
              VALUES (@city,'SI',@city_name,'America/Mazatlan');
            INSERT INTO locations.locations(id,owner_org_id,city_id,point,address_ciphertext,address_summary,pii_key_version) VALUES
              (@origin,@org,@city,public.ST_SetSRID(public.ST_MakePoint(-107.40,24.80),4326),decode('00','hex'),'Synthetic origin','test-v1'),
              (@destination,@org,@city,public.ST_SetSRID(public.ST_MakePoint(-107.39,24.81),4326),decode('01','hex'),'Synthetic destination','test-v1');
            INSERT INTO pricing.quotes(
              id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,consolidated_route,
              subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
              request_snapshot_redacted,package_snapshot,breakdown,input_hash,status,expires_at)
            VALUES (
              @quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,
              10000,0,0,10000,5000,'MXN','ntf003-policy-v1',
              '{"city":"synthetic"}','[{"description":"synthetic package","weight_grams":500,"declared_value_cents":10000}]','{}',@input_hash,'USED',clock_timestamp()+interval '1 day');
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,
              consolidated_route,payer_type,status,subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,
              currency,pricing_policy_version,package_snapshot,cod_expected_cents,version)
            SELECT @order,@public_id,q.id,q.owner_org_id,q.city_id,q.origin_location_id,q.destination_location_id,q.service_type,q.pricing_tier,
              q.consolidated_route,'SENDER','CONFIRMED',q.subtotal_cents,q.discount_cents,q.tax_cents,q.total_cents,q.minimum_total_cents_snapshot,
              q.currency,q.pricing_policy_version,q.package_snapshot,0,2
            FROM pricing.quotes q WHERE q.id=@quote;
            INSERT INTO orders.order_events(id,order_id,owner_org_id,aggregate_version,event_type,public_event_code,payload,occurred_at)
              VALUES (@order_event,@order,@org,2,'ORDER_STATUS_CHANGED','ORDER_CREATED','{}',clock_timestamp());
            """,
            new NpgsqlParameter("city", city), new NpgsqlParameter("city_name", $"NTF-003 City {city:N}"),
            new NpgsqlParameter("origin", origin), new NpgsqlParameter("destination", destination),
            new NpgsqlParameter("org", owner), new NpgsqlParameter("quote", quote), new NpgsqlParameter("order", order),
            new NpgsqlParameter("order_event", orderEvent), new NpgsqlParameter("public_id", publicId),
            new NpgsqlParameter("input_hash", new byte[32]));
        return (order, orderEvent, publicId);
    }

    /// <summary>
    /// One <c>orders.recipient-notice-requested</c> row (the AI-24 <c>recipient-notice-requested-v1</c> payload, ids only)
    /// as the NTF-004 producer will write it, PENDING and available now.
    /// </summary>
    public static async Task<Guid> InsertRequestAsync(
        NpgsqlConnection admin,
        Guid owner,
        Guid order,
        Guid orderEvent,
        string notice,
        DateTimeOffset occurredAt,
        string? traceParent = null)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(admin,
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
              payload,priority,status,attempts,available_at,created_at,trace_parent)
            VALUES(@id,@owner,jsonb_build_object('organization_ids',jsonb_build_array(@owner::text)),
              'orders.recipient-notice-requested','Order',@order,2,
              jsonb_build_object('schema_version','recipient-notice-requested-v1','order_event_id',@order_event::text,
                'order_id',@order::text,'notice',@notice::text,'occurred_at',@occurred_at::text),
              50,'PENDING',0,timestamptz '2000-01-01 00:00:00Z',timestamptz '2000-01-01 00:00:00Z',@trace_parent)
            """,
            new NpgsqlParameter("id", id), new NpgsqlParameter("owner", owner), new NpgsqlParameter("order", order),
            new NpgsqlParameter("order_event", orderEvent), new NpgsqlParameter("notice", notice),
            new NpgsqlParameter("occurred_at", occurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture)),
            new NpgsqlParameter("trace_parent", (object?)traceParent ?? DBNull.Value));
        return id;
    }

    public static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }
}
