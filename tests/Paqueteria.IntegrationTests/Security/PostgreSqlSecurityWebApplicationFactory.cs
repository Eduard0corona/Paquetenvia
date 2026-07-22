using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Paqueteria.Infrastructure.Database.Baseline;
using Testcontainers.PostgreSql;

namespace Paqueteria.IntegrationTests.Security;

public sealed class PostgreSqlSecurityWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Image = "postgis/postgis:18-3.6@sha256:b410052c6f0d7d37b83cac1369df144e1c843971155dea3317961001704d0a9d";
    public const string ValidTrackingToken = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";
    public const string ExpiredTrackingToken = "expired-token-sec002-000000000000";
    public const string RevokedTrackingToken = "revoked-token-sec002-000000000000";
    public static readonly Guid ViewerOrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OperationsOrganizationId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private PostgreSqlContainer? _container;
    private string _adminConnectionString = string.Empty;
    private string _applicationConnectionString = string.Empty;

    public string PostgreSqlVersion { get; private set; } = string.Empty;
    public string PostGisVersion { get; private set; } = string.Empty;
    public string ApplicationConnectionString => _applicationConnectionString;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase("paqueteria_sec002")
            .WithUsername("postgres")
            .WithPassword(_adminPassword)
            .WithCleanUp(true)
            .Build();
        await _container.StartAsync();

        var adminConnectionString = _container.GetConnectionString();
        _adminConnectionString = adminConnectionString;
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, adminConnectionString);

        await using var admin = NpgsqlDataSource.Create(adminConnectionString);
        await using (var command = admin.CreateCommand($$"""
            CREATE ROLE paqueteria_sec002_api LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{{_appPassword}}';
            GRANT paqueteria_app TO paqueteria_sec002_api;
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        await SeedSyntheticDataAsync(admin);
        await using (var version = admin.CreateCommand(
            "SELECT current_setting('server_version'), public.PostGIS_Version()"))
        await using (var reader = await version.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            PostgreSqlVersion = reader.GetString(0);
            PostGisVersion = reader.GetString(1);
        }

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Username = "paqueteria_sec002_api",
            Password = _appPassword,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 4,
            Timeout = 5,
            CommandTimeout = 5,
            ApplicationName = "Paqueteria.SEC002.IntegrationTests",
        };
        _applicationConnectionString = builder.ConnectionString;
    }

    public new async Task DisposeAsync()
    {
        Dispose();
        NpgsqlConnection.ClearAllPools();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "PostgreSql",
                ["IdentityBootstrap:CommandTimeoutSeconds"] = "5",
                ["PublicTracking:Provider"] = "PostgreSql",
                ["PublicTracking:CommandTimeoutSeconds"] = "5",
                ["Tenancy:Provider"] = "PostgreSql",
                ["Tenancy:CommandTimeoutSeconds"] = "5",
                ["ConnectionStrings:Paqueteria"] = _applicationConnectionString,
            }));
    }

    public async Task<int> CountTenantActivationAuditsAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::integer FROM platform.audit_logs WHERE action='TENANT_CONTEXT_ACTIVATED'",
            connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task SeedSyntheticDataAsync(NpgsqlDataSource admin)
    {
        await using var command = admin.CreateCommand("""
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES
              ('11111111-1111-1111-1111-111111111111','Synthetic Viewer','Synthetic Viewer','BUSINESS'),
              ('22222222-2222-2222-2222-222222222222','Synthetic Operations','Synthetic Operations','PLATFORM');
            INSERT INTO identity.users(id,identity_subject,status) VALUES
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1','mock-subject-active-viewer','ACTIVE'),
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2','mock-subject-platform-admin-mfa','ACTIVE'),
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3','mock-subject-platform-admin-no-mfa','ACTIVE'),
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4','mock-subject-multi-org','ACTIVE'),
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa5','mock-subject-suspended','SUSPENDED'),
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa6','mock-subject-disabled','DISABLED'),
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa7','mock-subject-suspended-membership','ACTIVE'),
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa8','mock-subject-revoked-membership','ACTIVE'),
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa9','mock-subject-no-memberships','ACTIVE');
            INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default) VALUES
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1','11111111-1111-1111-1111-111111111111','VIEWER','ACTIVE',true),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2','11111111-1111-1111-1111-111111111111','PLATFORM_ADMIN','ACTIVE',true),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3','11111111-1111-1111-1111-111111111111','PLATFORM_ADMIN','ACTIVE',true),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4','11111111-1111-1111-1111-111111111111','VIEWER','ACTIVE',true),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4','22222222-2222-2222-2222-222222222222','DISPATCHER','ACTIVE',false),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa7','11111111-1111-1111-1111-111111111111','VIEWER','SUSPENDED',false),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa8','11111111-1111-1111-1111-111111111111','VIEWER','REVOKED',false);

            INSERT INTO locations.cities(id,state_code,name,timezone)
              VALUES ('33333333-3333-3333-3333-333333333333','SI','Synthetic City','America/Mazatlan');
            INSERT INTO locations.locations(id,owner_org_id,city_id,point,address_ciphertext,address_summary,pii_key_version) VALUES
              ('44444444-4444-4444-4444-444444444441','11111111-1111-1111-1111-111111111111','33333333-3333-3333-3333-333333333333',public.ST_SetSRID(public.ST_MakePoint(-107.40,24.80),4326),decode('00','hex'),'Synthetic origin','test-v1'),
              ('44444444-4444-4444-4444-444444444442','11111111-1111-1111-1111-111111111111','33333333-3333-3333-3333-333333333333',public.ST_SetSRID(public.ST_MakePoint(-107.39,24.81),4326),decode('01','hex'),'Synthetic destination','test-v1');
            INSERT INTO pricing.quotes(id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,request_snapshot_redacted,package_snapshot,breakdown,input_hash,status,expires_at)
              VALUES ('55555555-5555-5555-5555-555555555555','11111111-1111-1111-1111-111111111111','33333333-3333-3333-3333-333333333333','44444444-4444-4444-4444-444444444441','44444444-4444-4444-4444-444444444442','SAME_DAY','OCCASIONAL',false,10000,0,0,10000,10000,'MXN','sec002-v1','{}','[]','{}',decode(repeat('00',32),'hex'),'ACTIVE',clock_timestamp()+interval '1 day');
            INSERT INTO orders.orders(id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,consolidated_route,payer_type,status,subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,package_snapshot,cod_expected_cents,version)
              VALUES ('66666666-6666-6666-6666-666666666666','SEC002-PUBLIC-001','55555555-5555-5555-5555-555555555555','11111111-1111-1111-1111-111111111111','33333333-3333-3333-3333-333333333333','44444444-4444-4444-4444-444444444441','44444444-4444-4444-4444-444444444442','SAME_DAY','OCCASIONAL',false,'SENDER','DELIVERING',10000,0,0,10000,10000,'MXN','sec002-v1','[]',0,1);
            INSERT INTO orders.public_tracking_tokens(id,order_id,owner_org_id,token_hash,expires_at,revoked_at) VALUES
              (gen_random_uuid(),'66666666-6666-6666-6666-666666666666','11111111-1111-1111-1111-111111111111',extensions.digest(pg_catalog.convert_to('AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA','UTF8'),'sha256'),clock_timestamp()+interval '1 day',NULL),
              (gen_random_uuid(),'66666666-6666-6666-6666-666666666666','11111111-1111-1111-1111-111111111111',extensions.digest(pg_catalog.convert_to('expired-token-sec002-000000000000','UTF8'),'sha256'),clock_timestamp()-interval '1 second',NULL),
              (gen_random_uuid(),'66666666-6666-6666-6666-666666666666','11111111-1111-1111-1111-111111111111',extensions.digest(pg_catalog.convert_to('revoked-token-sec002-000000000000','UTF8'),'sha256'),clock_timestamp()+interval '1 day',clock_timestamp());
            INSERT INTO orders.order_events(id,order_id,owner_org_id,aggregate_version,event_type,public_event_code,payload,occurred_at) VALUES
              (gen_random_uuid(),'66666666-6666-6666-6666-666666666666','11111111-1111-1111-1111-111111111111',1,'INTERNAL',NULL,'{"secret":"private"}',clock_timestamp()-interval '3 minutes'),
              (gen_random_uuid(),'66666666-6666-6666-6666-666666666666','11111111-1111-1111-1111-111111111111',2,'PICKED_UP','PICKED_UP','{"secret":"private"}',clock_timestamp()-interval '2 minutes'),
              (gen_random_uuid(),'66666666-6666-6666-6666-666666666666','11111111-1111-1111-1111-111111111111',3,'OUT_FOR_DELIVERY','OUT_FOR_DELIVERY','{"secret":"private"}',clock_timestamp()-interval '1 minute');
            """);
        command.CommandTimeout = 30;
        await command.ExecuteNonQueryAsync();
    }
}
