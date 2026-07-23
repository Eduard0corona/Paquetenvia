using Npgsql;

namespace Paqueteria.ContractTests.PostgreSql.Fixtures;

internal sealed class SyntheticOrderScenario(PostgreSqlContractFixture fixture) : IAsyncDisposable
{
    public Guid OrganizationId { get; } = Guid.NewGuid();
    public Guid UserId { get; } = Guid.NewGuid();
    public Guid CityId { get; } = Guid.NewGuid();
    public Guid OriginLocationId { get; } = Guid.NewGuid();
    public Guid DestinationLocationId { get; } = Guid.NewGuid();
    public Guid QuoteId { get; } = Guid.NewGuid();
    public Guid OrderId { get; } = Guid.NewGuid();
    public string PublicOrderId { get; } = $"ARC002-{Guid.NewGuid():N}";
    public long SubtotalCents { get; } = 3_000_000_100L;
    public long DiscountCents { get; } = 100L;
    public long TaxCents { get; } = 0L;
    public long TotalCents { get; } = 3_000_000_000L;
    public long MinimumTotalCents { get; } = 2_000_000_000L;

    public async Task InitializeAsync(string orderStatus = "DRAFT", string quoteStatus = "ACTIVE", bool createOrder = true)
    {
        var sql = """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
              VALUES (@org,'ARC-002 Synthetic Organization','ARC-002 Synthetic','BUSINESS');
            INSERT INTO identity.users(id,identity_subject)
              VALUES (@user_id,@subject);
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
              @subtotal,@discount,@tax,@total,@minimum,'MXN','arc002-policy-v1',
              '{"city":"synthetic"}','[{"description":"synthetic package","weight_grams":500,"declared_value_cents":3000000000}]','{}',@input_hash,@quote_status,clock_timestamp()+interval '1 day');
            """;
        if (createOrder)
        {
            sql += """
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,
              consolidated_route,payer_type,status,subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,
              currency,pricing_policy_version,package_snapshot,cod_expected_cents,version)
            SELECT @order,@public_id,q.id,q.owner_org_id,q.city_id,q.origin_location_id,q.destination_location_id,q.service_type,q.pricing_tier,
              q.consolidated_route,'SENDER',@order_status,q.subtotal_cents,q.discount_cents,q.tax_cents,q.total_cents,q.minimum_total_cents_snapshot,
              q.currency,q.pricing_policy_version,q.package_snapshot,0,1
            FROM pricing.quotes q WHERE q.id=@quote;
            """;
        }

        await ExecuteAdminAsync(sql,
            P("org", OrganizationId), P("user_id", UserId), P("subject", $"oidc|arc002|{UserId:N}"), P("city", CityId),
            P("city_name", $"Synthetic City {CityId:N}"),
            P("origin", OriginLocationId), P("destination", DestinationLocationId), P("quote", QuoteId), P("order", OrderId),
            P("public_id", PublicOrderId), P("subtotal", SubtotalCents), P("discount", DiscountCents), P("tax", TaxCents),
            P("total", TotalCents), P("minimum", MinimumTotalCents), P("input_hash", new byte[32]),
            P("quote_status", quoteStatus), P("order_status", orderStatus));
    }

    public async ValueTask DisposeAsync()
    {
        await using (var migrator = await TenantTransaction.BeginAsync(
            fixture.AdminDataSource,
            "paqueteria_migrator",
            UserId,
            [OrganizationId]))
        {
            await ExecuteAsync(migrator.Connection, migrator.Transaction, """
                DELETE FROM custody.proofs WHERE owner_org_id=@org;
                DELETE FROM orders.order_acceptances WHERE owner_org_id=@org;
                DELETE FROM orders.order_events WHERE owner_org_id=@org;
                DELETE FROM platform.audit_logs WHERE org_id=@org;
                """, P("org", OrganizationId));
            await migrator.CommitAsync();
        }

        await ExecuteAdminAsync("""
            DELETE FROM platform.outbox_events WHERE owner_org_id=@org;
            DELETE FROM platform.location_outbox_events WHERE owner_org_id=@org;
            DELETE FROM platform.idempotency_keys WHERE owner_org_id=@org;
            DELETE FROM orders.public_tracking_tokens WHERE owner_org_id=@org;
            DELETE FROM custody.proof_upload_sessions WHERE owner_org_id=@org;
            DELETE FROM orders.package_items WHERE owner_org_id=@org;
            DELETE FROM orders.orders WHERE owner_org_id=@org;
            DELETE FROM pricing.quotes WHERE owner_org_id=@org;
            DELETE FROM locations.locations WHERE owner_org_id=@org;
            DELETE FROM locations.cities WHERE id=@city;
            DELETE FROM organizations.organization_memberships WHERE organization_id=@org OR user_id=@user_id;
            DELETE FROM identity.users WHERE id=@user_id;
            DELETE FROM organizations.organizations WHERE id=@org;
            """, P("org", OrganizationId), P("city", CityId), P("user_id", UserId));
    }

    public async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    public static NpgsqlParameter P(string name, object value) => new(name, value);
}
