using System.Globalization;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class BootstrapContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly string[] ExpectedSchemas =
    [
        "extensions", "identity", "organizations", "clients", "locations", "pricing", "orders", "dispatch",
        "drivers", "routes", "custody", "incidents", "finance", "allies", "notifications", "reporting", "platform", "security",
    ];

    private static readonly string[] LifecycleFunctions =
    [
        "claim_outbox", "settle_outbox", "requeue_stale_outbox", "purge_outbox",
        "claim_location_outbox", "settle_location_outbox", "requeue_stale_location_outbox", "purge_location_outbox",
    ];

    [PostgreSqlContractFact]
    public async Task Ai06_then_ai18_bootstraps_the_complete_catalog()
    {
        Console.WriteLine($"PostgreSQL version: {fixture.PostgreSqlVersion}");
        Console.WriteLine($"PostGIS version: {fixture.PostGisVersion}");
        Console.WriteLine($"DBA-001 apply status: {fixture.ApplyStatus}");
        Console.WriteLine($"AI-06 schema: {fixture.SchemaDuration.TotalMilliseconds:F0} ms");
        Console.WriteLine($"AI-18 roles: {fixture.RolesDuration.TotalMilliseconds:F0} ms");
        Console.WriteLine($"DBA-001 assertions: {fixture.AssertionsDuration.TotalMilliseconds:F0} ms");
        Console.WriteLine($"Complete baseline bootstrap: {fixture.BootstrapDuration.TotalMilliseconds:F0} ms");
        Assert.StartsWith("18.", fixture.PostgreSqlVersion, StringComparison.Ordinal);
        Assert.StartsWith("3.6", fixture.PostGisVersion, StringComparison.Ordinal);
        Assert.Equal(Paqueteria.Infrastructure.Database.Baseline.DatabaseBaselineApplyStatus.Applied, fixture.ApplyStatus);
        Assert.True(fixture.BootstrapDuration < TimeSpan.FromMinutes(2), $"Bootstrap took {fixture.BootstrapDuration}.");

        var schemas = await QueryStringsAsync(
            "SELECT nspname FROM pg_namespace WHERE nspname = ANY(@schemas) ORDER BY nspname",
            new NpgsqlParameter<string[]>("schemas", ExpectedSchemas));
        Assert.Equal(ExpectedSchemas.Order(StringComparer.Ordinal), schemas);

        Assert.Equal(39, await ScalarAsync<int>("""
            SELECT count(*)::integer
            FROM pg_class c
            JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname = ANY(@schemas) AND c.relkind IN ('r','p')
            """, new NpgsqlParameter<string[]>("schemas", ExpectedSchemas.Where(name => name != "extensions").ToArray())));

        Assert.Equal(35, await ScalarAsync<int>("""
            SELECT count(*)::integer
            FROM pg_class c
            JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname = ANY(@schemas) AND c.relkind IN ('r','p')
              AND c.relrowsecurity AND c.relforcerowsecurity
            """, new NpgsqlParameter<string[]>("schemas", ExpectedSchemas)));
        Assert.Equal(35, await ScalarAsync<int>("SELECT count(*)::integer FROM pg_policy"));

        var lifecycle = await QueryStringsAsync(
            "SELECT proname FROM pg_proc JOIN pg_namespace n ON n.oid=pronamespace WHERE n.nspname='security' AND proname=ANY(@names) ORDER BY proname",
            new NpgsqlParameter<string[]>("names", LifecycleFunctions));
        Assert.Equal(LifecycleFunctions.Order(StringComparer.Ordinal), lifecycle);

        var appendOnlyTriggers = await QueryStringsAsync("""
            SELECT tgname FROM pg_trigger
            WHERE NOT tgisinternal AND tgname = ANY(@names)
            ORDER BY tgname
            """, new NpgsqlParameter<string[]>("names",
            [
                "order_events_append_only", "order_acceptances_append_only", "proofs_append_only", "audit_logs_append_only",
                "outbox_content_immutable", "location_outbox_content_immutable",
            ]));
        Assert.Equal(6, appendOnlyTriggers.Count);
    }

    [PostgreSqlContractFact]
    public async Task Extensions_are_placed_and_callable_as_contractually_required()
    {
        var extensions = await QueryStringsAsync("""
            SELECT e.extname || ':' || n.nspname
            FROM pg_extension e JOIN pg_namespace n ON n.oid=e.extnamespace
            WHERE e.extname IN ('postgis','pgcrypto') ORDER BY e.extname
            """);
        Assert.Equal(["pgcrypto:extensions", "postgis:public"], extensions);

        const string token = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";
        var hash = await ScalarAsync<byte[]>(
            "SELECT extensions.digest(pg_catalog.convert_to(@token,'UTF8'),'sha256')",
            new NpgsqlParameter<string>("token", token));
        Assert.Equal("eb9f16800c9029ffca85695763d23c3ace71011cf40e9354acd810205e250f87", Convert.ToHexString(hash).ToLowerInvariant());

        Assert.False(await ScalarAsync<bool>("""
            SELECT EXISTS (
              SELECT 1
              FROM pg_namespace n
              CROSS JOIN LATERAL aclexplode(COALESCE(n.nspacl, acldefault('n',n.nspowner))) acl
              WHERE n.nspname='public' AND acl.grantee=0 AND acl.privilege_type='CREATE'
            )
            """));
        Assert.False(await ScalarAsync<bool>("SELECT has_schema_privilege('paqueteria_app','public','CREATE')"));
        Assert.False(await ScalarAsync<bool>("SELECT has_schema_privilege('paqueteria_worker','public','CREATE')"));
        Assert.True(await ScalarAsync<bool>("SELECT has_schema_privilege('paqueteria_app','public','USAGE')"));
        Assert.True(await ScalarAsync<bool>("SELECT has_schema_privilege('paqueteria_worker','public','USAGE')"));
    }

    [PostgreSqlContractFact]
    public async Task Runtime_login_cannot_create_objects_in_public()
    {
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            []);
        await using var command = new NpgsqlCommand("CREATE TABLE public.arc002_forbidden(id integer)", tenant.Connection, tenant.Transaction);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [PostgreSqlContractFact]
    public async Task Roles_ownership_memberships_and_specialized_privileges_match_ai18()
    {
        var roles = await QueryStringsAsync("""
            SELECT rolname || ':' || rolcanlogin::text || ':' || rolbypassrls::text
            FROM pg_roles
            WHERE rolname=ANY(@roles)
            ORDER BY rolname
            """, new NpgsqlParameter<string[]>("roles",
            [
                "paqueteria_migrator", "paqueteria_app", "paqueteria_worker", "paqueteria_bootstrap",
                "paqueteria_outbox_executor", "paqueteria_maintenance",
            ]));
        Assert.Equal(
        [
            "paqueteria_app:false:false",
            "paqueteria_bootstrap:false:true",
            "paqueteria_maintenance:false:true",
            "paqueteria_migrator:false:false",
            "paqueteria_outbox_executor:false:true",
            "paqueteria_worker:false:false",
        ], roles);

        Assert.Equal(18, await ScalarAsync<int>("""
            SELECT count(*)::integer FROM pg_namespace n JOIN pg_roles r ON r.oid=n.nspowner
            WHERE n.nspname=ANY(@schemas) AND r.rolname='paqueteria_migrator'
            """, new NpgsqlParameter<string[]>("schemas", ExpectedSchemas)));
        Assert.Equal(0, await ScalarAsync<int>("""
            SELECT count(*)::integer
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_roles r ON r.oid=c.relowner
            WHERE n.nspname=ANY(@schemas) AND c.relkind IN ('r','p','S') AND r.rolname<>'paqueteria_migrator'
            """, new NpgsqlParameter<string[]>("schemas", ExpectedSchemas)));
        Assert.Equal(0, await ScalarAsync<int>("""
            SELECT count(*)::integer
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace JOIN pg_roles r ON r.oid=p.proowner
            WHERE n.nspname=ANY(@schemas) AND r.rolname IN ('paqueteria_app','paqueteria_worker')
            """, new NpgsqlParameter<string[]>("schemas", ExpectedSchemas)));

        var specializedOwners = await QueryStringsAsync("""
            SELECT p.proname || ':' || r.rolname
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace JOIN pg_roles r ON r.oid=p.proowner
            WHERE n.nspname='security' AND p.proname=ANY(@names)
            ORDER BY p.proname
            """, new NpgsqlParameter<string[]>("names",
            [.. LifecycleFunctions, "resolve_identity_context", "get_public_tracking_projection"]));
        Assert.Contains("resolve_identity_context:paqueteria_bootstrap", specializedOwners);
        Assert.Contains("get_public_tracking_projection:paqueteria_bootstrap", specializedOwners);
        Assert.All(specializedOwners.Where(value => value.StartsWith("purge_", StringComparison.Ordinal)),
            value => Assert.EndsWith(":paqueteria_maintenance", value, StringComparison.Ordinal));
        Assert.All(specializedOwners.Where(value => value.Contains("outbox", StringComparison.Ordinal) && !value.StartsWith("purge_", StringComparison.Ordinal)),
            value => Assert.EndsWith(":paqueteria_outbox_executor", value, StringComparison.Ordinal));

        var loginMemberships = await QueryStringsAsync("""
            SELECT member.rolname || '->' || granted.rolname
            FROM pg_auth_members m JOIN pg_roles member ON member.oid=m.member JOIN pg_roles granted ON granted.oid=m.roleid
            WHERE member.rolname IN ('paqueteria_app_login_test','paqueteria_worker_login_test')
            ORDER BY member.rolname
            """);
        Assert.Equal(
        [
            "paqueteria_app_login_test->paqueteria_app",
            "paqueteria_worker_login_test->paqueteria_worker",
        ], loginMemberships);

        var loginFlags = await QueryStringsAsync("""
            SELECT rolname || ':' || rolcanlogin::text || ':' || rolsuper::text || ':' || rolcreatedb::text || ':' ||
                   rolcreaterole::text || ':' || rolreplication::text || ':' || rolbypassrls::text
            FROM pg_roles
            WHERE rolname IN ('paqueteria_app_login_test','paqueteria_worker_login_test')
            ORDER BY rolname
            """);
        Assert.Equal(
        [
            "paqueteria_app_login_test:true:false:false:false:false:false",
            "paqueteria_worker_login_test:true:false:false:false:false:false",
        ], loginFlags);

        foreach (var lane in new[] { "platform.outbox_events", "platform.location_outbox_events" })
        {
            Assert.True(await HasTablePrivilegeAsync("paqueteria_maintenance", lane, "SELECT"));
            Assert.True(await HasTablePrivilegeAsync("paqueteria_maintenance", lane, "DELETE"));
            Assert.False(await HasTablePrivilegeAsync("paqueteria_maintenance", lane, "UPDATE"));
            foreach (var role in new[] { "paqueteria_app", "paqueteria_worker" })
            {
                Assert.True(await HasTablePrivilegeAsync(role, lane, "INSERT"));
                Assert.False(await HasTablePrivilegeAsync(role, lane, "SELECT"));
                Assert.False(await HasTablePrivilegeAsync(role, lane, "UPDATE"));
                Assert.False(await HasTablePrivilegeAsync(role, lane, "DELETE"));
            }
        }

        Assert.Equal(4, await ScalarAsync<int>("""
            SELECT count(*)::integer FROM information_schema.role_table_grants
            WHERE grantee='paqueteria_maintenance'
            """));
        Assert.Equal(4, await ScalarAsync<int>("""
            SELECT count(*)::integer FROM information_schema.role_table_grants
            WHERE grantee='paqueteria_outbox_executor'
            """));
        Assert.False(await HasTablePrivilegeAsync("paqueteria_bootstrap", "identity.users", "SELECT"));
        Assert.True(await ScalarAsync<bool>(
            "SELECT has_column_privilege('paqueteria_bootstrap','identity.users','identity_subject','SELECT')"));
        Assert.False(await ScalarAsync<bool>(
            "SELECT has_column_privilege('paqueteria_bootstrap','identity.users','created_at','SELECT')"));

        foreach (var function in new[]
        {
            "security.claim_outbox(text,integer,interval)",
            "security.settle_outbox(uuid,uuid,text,text,timestamptz)",
            "security.requeue_stale_outbox(interval,integer,integer)",
            "security.claim_location_outbox(text,integer,interval)",
            "security.settle_location_outbox(uuid,uuid,text,text,timestamptz)",
            "security.requeue_stale_location_outbox(interval,integer,integer)",
        })
        {
            Assert.True(await HasFunctionPrivilegeAsync("paqueteria_worker", function));
            Assert.False(await HasFunctionPrivilegeAsync("paqueteria_app", function));
            Assert.False(await HasFunctionPrivilegeAsync("paqueteria_maintenance", function));
        }

        foreach (var function in new[]
        {
            "security.purge_outbox(timestamptz,timestamptz,integer,boolean)",
            "security.purge_location_outbox(timestamptz,timestamptz,integer,boolean)",
        })
        {
            Assert.True(await HasFunctionPrivilegeAsync("paqueteria_worker", function));
            Assert.False(await HasFunctionPrivilegeAsync("paqueteria_app", function));
            Assert.False(await HasFunctionPrivilegeAsync("paqueteria_outbox_executor", function));
        }
    }

    private async Task<bool> HasTablePrivilegeAsync(string role, string table, string privilege) =>
        await ScalarAsync<bool>(
            "SELECT has_table_privilege(@role,@table,@privilege)",
            new NpgsqlParameter<string>("role", role),
            new NpgsqlParameter<string>("table", table),
            new NpgsqlParameter<string>("privilege", privilege));

    private async Task<bool> HasFunctionPrivilegeAsync(string role, string function) =>
        await ScalarAsync<bool>(
            "SELECT has_function_privilege(@role,@function,'EXECUTE')",
            new NpgsqlParameter<string>("role", role),
            new NpgsqlParameter<string>("function", function));

    private async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        if (value is T typedValue)
        {
            return typedValue;
        }

        return (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<string>> QueryStringsAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var results = new List<string>();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }
}
