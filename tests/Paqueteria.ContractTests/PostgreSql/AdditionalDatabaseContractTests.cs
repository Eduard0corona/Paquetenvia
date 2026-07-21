using System.Globalization;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class AdditionalDatabaseContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Money_columns_are_bigint_and_large_values_and_constraints_execute_in_postgresql()
    {
        var invalidMoneyColumns = await QueryStringsAsync("""
            SELECT table_schema || '.' || table_name || '.' || column_name || ':' || data_type
            FROM information_schema.columns
            WHERE table_schema IN ('pricing','orders','dispatch','custody','finance')
              AND column_name LIKE '%\_cents' ESCAPE '\'
              AND data_type <> 'bigint'
            ORDER BY 1
            """);
        Assert.Empty(invalidMoneyColumns);

        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync();
        Assert.Equal(3_000_000_000L, await ScalarAsync<long>(
            "SELECT total_cents FROM orders.orders WHERE id=@id", P("id", scenario.OrderId)));

        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            scenario.UserId,
            [scenario.OrganizationId]);
        var inconsistent = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(tenant.Connection, tenant.Transaction, """
            UPDATE orders.orders SET subtotal_cents=100,total_cents=99 WHERE id=@id
            """, P("id", scenario.OrderId)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, inconsistent.SqlState);
    }

    [PostgreSqlContractFact]
    public async Task External_offer_versioning_sensitive_function_acl_and_search_paths_match_the_catalog()
    {
        var offerColumns = await QueryStringsAsync("""
            SELECT column_name || ':' || data_type || ':' || is_nullable
            FROM information_schema.columns
            WHERE table_schema='dispatch' AND table_name='external_offers'
              AND column_name IN ('accepted_by_driver_id','accepted_at','version')
            ORDER BY column_name
            """);
        Assert.Equal(
        [
            "accepted_at:timestamp with time zone:YES",
            "accepted_by_driver_id:uuid:YES",
            "version:integer:NO",
        ], offerColumns);

        await using (var scenario = new SyntheticOrderScenario(fixture))
        {
            await scenario.InitializeAsync();
            await using var tenant = await TenantTransaction.BeginAsync(
                fixture.AppDataSource,
                "paqueteria_app",
                scenario.UserId,
                [scenario.OrganizationId]);
            var invalidAcceptance = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                tenant.Connection,
                tenant.Transaction,
                """
                INSERT INTO dispatch.external_offers(
                  id,order_id,owner_org_id,commission_cents,status,expires_at,version)
                VALUES (@id,@order,@org,100,'ACCEPTED',clock_timestamp()+interval '1 hour',1)
                """,
                P("id", Guid.NewGuid()),
                P("order", scenario.OrderId),
                P("org", scenario.OrganizationId)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, invalidAcceptance.SqlState);
        }

        var unsafeSensitiveFunctions = await QueryStringsAsync("""
            SELECT n.nspname || '.' || p.proname
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='security'
              AND p.proname IN (
                'resolve_identity_context','get_public_tracking_projection','map_public_order_status',
                'claim_outbox','settle_outbox','requeue_stale_outbox','purge_outbox',
                'claim_location_outbox','settle_location_outbox','requeue_stale_location_outbox','purge_location_outbox')
              AND EXISTS (
                SELECT 1
                FROM aclexplode(COALESCE(p.proacl,acldefault('f',p.proowner))) acl
                WHERE acl.grantee=0 AND acl.privilege_type='EXECUTE')
            """);
        Assert.Empty(unsafeSensitiveFunctions);

        var missingSearchPaths = await QueryStringsAsync("""
            SELECT p.proname
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='security'
              AND p.proname IN (
                'resolve_identity_context','get_public_tracking_projection','map_public_order_status',
                'claim_outbox','settle_outbox','requeue_stale_outbox','purge_outbox',
                'claim_location_outbox','settle_location_outbox','requeue_stale_location_outbox','purge_location_outbox')
              AND NOT EXISTS (SELECT 1 FROM unnest(COALESCE(p.proconfig,'{}')) value WHERE value LIKE 'search_path=%')
            """);
        Assert.Empty(missingSearchPaths);
    }

    private async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        var result = await command.ExecuteScalarAsync();
        return result is T typed ? typed : (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<string>> QueryStringsAsync(string sql)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlParameter P(string name, object value) => new(name, value);
}
