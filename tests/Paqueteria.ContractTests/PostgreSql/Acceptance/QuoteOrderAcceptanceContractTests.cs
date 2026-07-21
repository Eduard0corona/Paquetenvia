using System.Globalization;
using Npgsql;
using Paqueteria.ContractTests.Cryptography;
using Paqueteria.ContractTests.PostgreSql.Fixtures;

namespace Paqueteria.ContractTests.PostgreSql.Acceptance;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class QuoteOrderAcceptanceContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Quote_snapshot_order_acceptance_event_and_outbox_commit_atomically()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(createOrder: false);
        var acceptanceId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        var acceptedAt = DateTimeOffset.Parse("2026-07-20T12:34:56.1234560Z", CultureInfo.InvariantCulture);
        var evidence = new OrderAcceptanceEvidence(
            scenario.OrderId,
            scenario.QuoteId,
            scenario.OrganizationId,
            scenario.UserId,
            "terms-2026-07",
            "privacy-2026-07",
            acceptedAt,
            "PWA");
        var evidenceHash = OrderAcceptanceCanonicalizer.Hash(evidence);

        await using (var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            scenario.UserId,
            [scenario.OrganizationId]))
        {
            Assert.Equal(1, await ExecuteAsync(tenant.Connection, tenant.Transaction, """
                UPDATE pricing.quotes SET status='USED',consumed_at=@recorded_at
                WHERE id=@quote AND status='ACTIVE' AND expires_at>@recorded_at
                """, P("quote", scenario.QuoteId), P("recorded_at", DateTimeOffset.UtcNow)));

            await ExecuteAsync(tenant.Connection, tenant.Transaction, OrderFromQuoteSql,
                P("order", scenario.OrderId), P("public_id", scenario.PublicOrderId), P("quote", scenario.QuoteId));
            await ExecuteAsync(tenant.Connection, tenant.Transaction, """
                INSERT INTO orders.order_acceptances(
                  id,order_id,quote_id,owner_org_id,actor_id,terms_version,privacy_version,accepted_at_client,
                  recorded_at_server,acceptance_channel,evidence_schema_version,evidence_hash)
                VALUES (@id,@order,@quote,@org,@actor,@terms,@privacy,@accepted,@recorded,'PWA','order-acceptance-v1',@hash)
                """, P("id", acceptanceId), P("order", scenario.OrderId), P("quote", scenario.QuoteId),
                P("org", scenario.OrganizationId), P("actor", scenario.UserId), P("terms", evidence.TermsVersion),
                P("privacy", evidence.PrivacyVersion), P("accepted", acceptedAt), P("recorded", DateTimeOffset.UtcNow), P("hash", evidenceHash));
            await ExecuteAsync(tenant.Connection, tenant.Transaction, """
                INSERT INTO orders.order_events(id,order_id,owner_org_id,aggregate_version,event_type,public_event_code,payload,actor_id,occurred_at)
                VALUES (@id,@order,@org,1,'ORDER_CREATED','ORDER_CREATED','{}',@actor,@occurred)
                """, P("id", eventId), P("order", scenario.OrderId), P("org", scenario.OrganizationId),
                P("actor", scenario.UserId), P("occurred", DateTimeOffset.UtcNow));
            await ExecuteAsync(tenant.Connection, tenant.Transaction, """
                INSERT INTO platform.outbox_events(
                  id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,priority,status,
                  attempts,available_at,created_at)
                VALUES (@id,@org,'{}','orders.created','Order',@order,1,'{}',50,'PENDING',0,@occurred,@occurred)
                """, P("id", outboxId), P("org", scenario.OrganizationId), P("order", scenario.OrderId), P("occurred", DateTimeOffset.UtcNow));
            await tenant.CommitAsync();
        }

        await using (var command = fixture.AdminDataSource.CreateCommand("""
            SELECT q.status,o.owner_org_id=q.owner_org_id,o.origin_location_id=q.origin_location_id,
                   o.destination_location_id=q.destination_location_id,o.city_id=q.city_id,
                   o.service_area_id IS NOT DISTINCT FROM q.service_area_id,o.service_type=q.service_type,
                   o.pricing_tier=q.pricing_tier,o.consolidated_route=q.consolidated_route,
                   o.subtotal_cents=q.subtotal_cents,o.discount_cents=q.discount_cents,o.tax_cents=q.tax_cents,
                   o.total_cents=q.total_cents,o.minimum_total_cents_snapshot=q.minimum_total_cents_snapshot,
                   o.currency=q.currency,o.pricing_policy_version=q.pricing_policy_version,
                   o.package_snapshot=q.package_snapshot,o.financial_override IS NOT DISTINCT FROM q.financial_override,
                   a.evidence_hash
            FROM pricing.quotes q JOIN orders.orders o ON o.quote_id=q.id
            JOIN orders.order_acceptances a ON a.order_id=o.id WHERE q.id=@quote
            """))
        {
            command.Parameters.Add(P("quote", scenario.QuoteId));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("USED", reader.GetString(0));
            for (var index = 1; index <= 17; index++)
            {
                Assert.True(reader.GetBoolean(index), $"Quote snapshot comparison {index} failed.");
            }
            Assert.Equal(evidenceHash, reader.GetFieldValue<byte[]>(18));
        }

        await using (var secondConsumption = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            scenario.UserId,
            [scenario.OrganizationId]))
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                secondConsumption.Connection,
                secondConsumption.Transaction,
                OrderFromQuoteSql,
                P("order", Guid.NewGuid()), P("public_id", $"ARC002-{Guid.NewGuid():N}"), P("quote", scenario.QuoteId)));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        }

        Assert.Equal(3_000_000_000L, scenario.TotalCents);
    }

    [PostgreSqlContractFact]
    public async Task Quote_to_order_rollback_leaves_no_partial_rows_and_quote_active()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(createOrder: false);
        await using (var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            scenario.UserId,
            [scenario.OrganizationId]))
        {
            await ExecuteAsync(tenant.Connection, tenant.Transaction,
                "UPDATE pricing.quotes SET status='USED',consumed_at=clock_timestamp() WHERE id=@quote",
                P("quote", scenario.QuoteId));
            await ExecuteAsync(tenant.Connection, tenant.Transaction, OrderFromQuoteSql,
                P("order", scenario.OrderId), P("public_id", scenario.PublicOrderId), P("quote", scenario.QuoteId));
            await tenant.RollbackAsync();
        }

        Assert.Equal("ACTIVE", await AdminScalarAsync<string>("SELECT status FROM pricing.quotes WHERE id=@id", P("id", scenario.QuoteId)));
        Assert.Equal(0, await AdminScalarAsync<int>("SELECT count(*)::integer FROM orders.orders WHERE id=@id", P("id", scenario.OrderId)));
        Assert.Equal(0, await AdminScalarAsync<int>("SELECT count(*)::integer FROM orders.order_acceptances WHERE order_id=@id", P("id", scenario.OrderId)));
        Assert.Equal(0, await AdminScalarAsync<int>("SELECT count(*)::integer FROM orders.order_events WHERE order_id=@id", P("id", scenario.OrderId)));
        Assert.Equal(0, await AdminScalarAsync<int>("SELECT count(*)::integer FROM platform.outbox_events WHERE aggregate_id=@id", P("id", scenario.OrderId)));
    }

    [PostgreSqlContractFact]
    public async Task Acceptance_and_all_evidence_tables_are_rls_protected_and_append_only_for_runtime()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync();
        var acceptance = Guid.NewGuid();
        var orderEvent = Guid.NewGuid();
        var upload = Guid.NewGuid();
        var proof = Guid.NewGuid();
        var audit = Guid.NewGuid();
        await scenario.ExecuteAdminAsync("""
            INSERT INTO orders.order_acceptances(id,order_id,quote_id,owner_org_id,actor_id,terms_version,privacy_version,accepted_at_client,recorded_at_server,acceptance_channel,evidence_schema_version,evidence_hash)
              VALUES (@acceptance,@order,@quote,@org,@user_id,'terms-test','privacy-test',clock_timestamp(),clock_timestamp(),'API','order-acceptance-v1',@hash);
            INSERT INTO orders.order_events(id,order_id,owner_org_id,aggregate_version,event_type,payload,occurred_at)
              VALUES (@event,@order,@org,1,'SYNTHETIC','{}',clock_timestamp());
            INSERT INTO custody.proof_upload_sessions(id,order_id,owner_org_id,requested_by,object_key_quarantine,expected_content_type,maximum_bytes,status,expires_at)
              VALUES (@upload,@order,@org,@user_id,@quarantine,'image/jpeg',1024,'READY',clock_timestamp()+interval '1 hour');
            INSERT INTO custody.proofs(id,order_id,owner_org_id,upload_session_id,proof_type,object_key,sha256,content_type,size_bytes,captured_at,created_by)
              VALUES (@proof,@order,@org,@upload,'DELIVERY_PHOTO',@object_key,@hash,'image/jpeg',12,clock_timestamp(),@user_id);
            INSERT INTO platform.audit_logs(id,org_id,actor_id,action,entity_type,entity_id)
              VALUES (@audit,@org,@user_id,'SYNTHETIC','Order',@order);
            """, P("acceptance", acceptance), P("order", scenario.OrderId), P("quote", scenario.QuoteId),
            P("org", scenario.OrganizationId), P("user_id", scenario.UserId), P("hash", new byte[32]), P("event", orderEvent),
            P("upload", upload), P("quarantine", $"quarantine/{upload:N}"), P("proof", proof), P("object_key", $"proof/{proof:N}"), P("audit", audit));

        foreach (var dataSourceAndRole in new[]
        {
            (DataSource: fixture.AppDataSource, Role: "paqueteria_app"),
            (DataSource: fixture.WorkerDataSource, Role: "paqueteria_worker"),
        })
        {
            foreach (var statement in new[]
            {
                $"UPDATE orders.order_acceptances SET terms_version='tampered' WHERE id='{acceptance:D}'",
                $"DELETE FROM orders.order_acceptances WHERE id='{acceptance:D}'",
                $"UPDATE orders.order_events SET event_type='tampered' WHERE id='{orderEvent:D}'",
                $"DELETE FROM orders.order_events WHERE id='{orderEvent:D}'",
                $"UPDATE custody.proofs SET object_key='tampered' WHERE id='{proof:D}'",
                $"DELETE FROM custody.proofs WHERE id='{proof:D}'",
                $"UPDATE platform.audit_logs SET action='tampered' WHERE id='{audit:D}'",
                $"DELETE FROM platform.audit_logs WHERE id='{audit:D}'",
            })
            {
                await using var tenant = await TenantTransaction.BeginAsync(
                    dataSourceAndRole.DataSource,
                    dataSourceAndRole.Role,
                    scenario.UserId,
                    [scenario.OrganizationId]);
                var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(tenant.Connection, tenant.Transaction, statement));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
            }
        }

        await using var foreign = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            Guid.NewGuid(),
            [Guid.NewGuid()]);
        Assert.Equal(0, await ScalarAsync<int>(foreign.Connection, foreign.Transaction,
            "SELECT count(*)::integer FROM orders.order_acceptances WHERE id=@id", P("id", acceptance)));
    }

    private const string OrderFromQuoteSql = """
        INSERT INTO orders.orders(
          id,public_id,quote_id,owner_org_id,client_account_id,city_id,service_area_id,origin_location_id,destination_location_id,
          service_type,pricing_tier,consolidated_route,payer_type,status,subtotal_cents,discount_cents,tax_cents,total_cents,
          minimum_total_cents_snapshot,currency,pricing_policy_version,package_snapshot,financial_override,cod_expected_cents,version)
        SELECT @order,@public_id,q.id,q.owner_org_id,q.client_account_id,q.city_id,q.service_area_id,q.origin_location_id,q.destination_location_id,
          q.service_type,q.pricing_tier,q.consolidated_route,'SENDER','DRAFT',q.subtotal_cents,q.discount_cents,q.tax_cents,q.total_cents,
          q.minimum_total_cents_snapshot,q.currency,q.pricing_policy_version,q.package_snapshot,q.financial_override,0,1
        FROM pricing.quotes q WHERE q.id=@quote
        """;

    private async Task<T> AdminScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        var result = await command.ExecuteScalarAsync();
        return ConvertValue<T>(result);
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return ConvertValue<T>(await command.ExecuteScalarAsync());
    }

    private static T ConvertValue<T>(object? result) => result is T typed
        ? typed
        : (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);

    private static NpgsqlParameter P(string name, object value) => new(name, value);
}
