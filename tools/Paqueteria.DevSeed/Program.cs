using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application.Tracking;
using Orders.Infrastructure;
using Paqueteria.Application.Security;

const string RequiredEnvironment = "Development";
const string RequiredOptIn = "PAQUETERIA_LOCAL_DEV_SEED_ENABLED";
const string SyntheticSeedOptIn = "PAQUETERIA_SYNTHETIC_SEED_ENABLED";
const string AdminConnectionEnvironment = "PAQUETERIA_LOCAL_ADMIN_CONNECTION";
const string AppPasswordEnvironment = "PAQUETERIA_LOCAL_APP_PASSWORD";
const string WorkerPasswordEnvironment = "PAQUETERIA_LOCAL_WORKER_PASSWORD";
const string AppConnectionEnvironment = "PAQUETERIA_LOCAL_APP_CONNECTION";

if ((args.Length != 1 || args[0] is not ("bootstrap" or "seed" or "status")) &&
    (args.Length != 2 || args[0] != "tracking" || !Guid.TryParse(args[1], out _)))
{
    return Fail("Usage: Paqueteria.DevSeed <bootstrap|seed|status|tracking order-id>");
}

var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
var isLocalDevelopment =
    string.Equals(environmentName, RequiredEnvironment, StringComparison.Ordinal) &&
    string.Equals(Environment.GetEnvironmentVariable(RequiredOptIn), "true", StringComparison.Ordinal);
var isDevSyntheticSeed =
    args[0] == "seed" &&
    SyntheticEnvironmentPolicy.IsDevSynthetic(environmentName) &&
    string.Equals(Environment.GetEnvironmentVariable(SyntheticSeedOptIn), "true", StringComparison.Ordinal);

if (!isLocalDevelopment && !isDevSyntheticSeed)
{
    return Fail("DevSeed command is not authorized for this environment and explicit opt-in.");
}

if (args[0] == "tracking")
{
    await IssueTrackingAsync(Guid.Parse(args[1]), Required(AppConnectionEnvironment));
    return 0;
}

var adminConnection = Required(AdminConnectionEnvironment);
await using var connection = new NpgsqlConnection(adminConnection);
await connection.OpenAsync();

if (args[0] == "bootstrap")
{
    await BootstrapRuntimeRolesAsync(connection, Required(AppPasswordEnvironment), Required(WorkerPasswordEnvironment));
}

if (args[0] is "bootstrap" or "seed")
{
    await SeedPrerequisitesAsync(connection);
}

if (isDevSyntheticSeed)
{
    await ReportSyntheticSeedStatusAsync(connection);
}
else
{
    await ReportStatusAsync(connection);
}
return 0;

static async Task IssueTrackingAsync(Guid orderId, string appConnection)
{
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Paqueteria"] = appConnection,
            ["Orders:Provider"] = "PostgreSql",
            ["PublicTracking:Provider"] = "PostgreSql",
            ["PublicTracking:AllowedOrigins:0"] = "http://127.0.0.1:3000",
        })
        .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddOrdersInfrastructure(configuration);
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    var grant = await scope.ServiceProvider
        .GetRequiredService<IPublicTrackingTokenService>()
        .IssueAsync(
            new IssuePublicTrackingTokenCommand(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa20"),
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                orderId,
                "local-seed-delivered-tracking-v1"),
            CancellationToken.None);
    Console.WriteLine(grant.Token);
}

static async Task BootstrapRuntimeRolesAsync(
    NpgsqlConnection connection,
    string appPassword,
    string workerPassword)
{
    await EnsureLoginAsync(connection, "paqueteria_local_app", appPassword, "paqueteria_app");
    await EnsureLoginAsync(connection, "paqueteria_local_worker", workerPassword, "paqueteria_worker");
}

static async Task EnsureLoginAsync(
    NpgsqlConnection connection,
    string login,
    string password,
    string runtimeRole)
{
    if (password.Length != 64 || password.Any(value => !Uri.IsHexDigit(value)))
    {
        throw new InvalidOperationException("Local runtime passwords must be 64 hexadecimal characters.");
    }
    var quotedPassword = $"'{password}'";
    var sql = $$"""
        DO $do$
        BEGIN
          IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='{{login}}') THEN
            CREATE ROLE {{login}} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD {{quotedPassword}};
          ELSE
            ALTER ROLE {{login}} WITH LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD {{quotedPassword}};
          END IF;
        END
        $do$;
        GRANT {{runtimeRole}} TO {{login}};
        """;
    await ExecuteAsync(connection, sql);
}

static async Task SeedPrerequisitesAsync(NpgsqlConnection connection)
{
    await using var transaction = await connection.BeginTransactionAsync();
    const string sql = """
        INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
        VALUES
          ('11111111-1111-1111-1111-111111111111','Synthetic Local Organization','Synthetic Local Organization','BUSINESS'),
          ('33333333-3333-3333-3333-333333333333','Synthetic Decoy Organization','Synthetic Decoy Organization','BUSINESS')
        ON CONFLICT (id) DO UPDATE SET
          legal_name=EXCLUDED.legal_name,display_name=EXCLUDED.display_name;

        INSERT INTO identity.users(id,identity_subject,status)
        VALUES
          ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa20','local-subject-dispatcher-mfa','ACTIVE'),
          ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11','mock-subject-active-driver','ACTIVE'),
          ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa13','mock-subject-external-driver','ACTIVE')
        ON CONFLICT (id) DO UPDATE SET
          identity_subject=EXCLUDED.identity_subject,status='ACTIVE';

        INSERT INTO organizations.organization_memberships(
          id,user_id,organization_id,role,status,is_default)
        VALUES
          ('dddddddd-dddd-dddd-dddd-dddddddddd20','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa20','11111111-1111-1111-1111-111111111111','DISPATCHER','ACTIVE',true),
          ('dddddddd-dddd-dddd-dddd-dddddddddd11','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11','11111111-1111-1111-1111-111111111111','DRIVER','ACTIVE',true),
          ('dddddddd-dddd-dddd-dddd-dddddddddd13','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa13','11111111-1111-1111-1111-111111111111','DRIVER','ACTIVE',false)
        ON CONFLICT (id) DO UPDATE SET status='ACTIVE',is_default=EXCLUDED.is_default;

        INSERT INTO locations.cities(id,country_code,state_code,name,timezone,status)
        VALUES ('44444444-4444-4444-4444-444444444441','MX','SIN','Synthetic Local City','America/Mazatlan','ACTIVE')
        ON CONFLICT (id) DO UPDATE SET status='ACTIVE';

        INSERT INTO locations.service_areas(id,owner_org_id,city_id,name,polygon,status)
        VALUES (
          '44444444-4444-4444-4444-444444444442','11111111-1111-1111-1111-111111111111',
          '44444444-4444-4444-4444-444444444441','Synthetic Local Service Area',
          ST_GeomFromText('MULTIPOLYGON(((-107.60 24.60,-107.20 24.60,-107.20 25.00,-107.60 25.00,-107.60 24.60)))',4326),'ACTIVE')
        ON CONFLICT (id) DO UPDATE SET status='ACTIVE';

        INSERT INTO locations.operating_zones(id,owner_org_id,service_area_id,name,zone_type,polygon,status)
        VALUES (
          '44444444-4444-4444-4444-444444444443','11111111-1111-1111-1111-111111111111',
          '44444444-4444-4444-4444-444444444442','Synthetic Local Core','CORE',
          ST_GeomFromText('MULTIPOLYGON(((-107.50 24.70,-107.30 24.70,-107.30 24.90,-107.50 24.90,-107.50 24.70)))',4326),'ACTIVE')
        ON CONFLICT (id) DO UPDATE SET status='ACTIVE';

        INSERT INTO pricing.tariff_rules(
          id,owner_org_id,city_id,service_area_id,operating_zone_id,
          pricing_tier,service_type,amount_cents,tax_mode,active_from,status,policy_version)
        VALUES (
          '44444444-4444-4444-4444-444444444444','11111111-1111-1111-1111-111111111111',
          '44444444-4444-4444-4444-444444444441','44444444-4444-4444-4444-444444444442',
          '44444444-4444-4444-4444-444444444443','OCCASIONAL','SAME_DAY',12000,'EXEMPT',
          '2026-01-01T00:00:00Z','ACTIVE','LOCAL-SYNTHETIC-v1')
        ON CONFLICT (id) DO UPDATE SET status='ACTIVE',amount_cents=EXCLUDED.amount_cents,
          policy_version=EXCLUDED.policy_version;

        INSERT INTO drivers.driver_profiles(id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
        VALUES (
          '55555555-5555-5555-5555-555555555551','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11',
          '11111111-1111-1111-1111-111111111111','44444444-4444-4444-4444-444444444441',
          'OWN','MOTORCYCLE','ACTIVE')
        ON CONFLICT (id) DO UPDATE SET status='ACTIVE';

        INSERT INTO drivers.driver_profiles(id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
        VALUES (
          '55555555-5555-5555-5555-555555555553','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa13',
          '11111111-1111-1111-1111-111111111111','44444444-4444-4444-4444-444444444441',
          'EXTERNAL','MOTORCYCLE','ACTIVE')
        ON CONFLICT (id) DO UPDATE SET driver_type='EXTERNAL',status='ACTIVE';

        INSERT INTO drivers.driver_service_areas(driver_id,service_area_id,org_id,status)
        VALUES (
          '55555555-5555-5555-5555-555555555551','44444444-4444-4444-4444-444444444442',
          '11111111-1111-1111-1111-111111111111','ACTIVE')
        ON CONFLICT (driver_id,service_area_id) DO UPDATE SET status='ACTIVE';

        INSERT INTO drivers.driver_service_areas(driver_id,service_area_id,org_id,status)
        VALUES (
          '55555555-5555-5555-5555-555555555553','44444444-4444-4444-4444-444444444442',
          '11111111-1111-1111-1111-111111111111','ACTIVE')
        ON CONFLICT (driver_id,service_area_id) DO UPDATE SET status='ACTIVE';

        INSERT INTO drivers.driver_documents(id,driver_id,org_id,document_type,object_key,sha256,status)
        VALUES (
          '55555555-5555-5555-5555-555555555552','55555555-5555-5555-5555-555555555551',
          '11111111-1111-1111-1111-111111111111','IDENTITY',
          'synthetic/local/driver-document',decode(repeat('11',32),'hex'),'VALID')
        ON CONFLICT (id) DO UPDATE SET status='VALID';

        INSERT INTO drivers.driver_documents(id,driver_id,org_id,document_type,object_key,sha256,expires_at,status)
        VALUES (
          '55555555-5555-5555-5555-555555555554','55555555-5555-5555-5555-555555555553',
          '11111111-1111-1111-1111-111111111111','IDENTITY',
          'synthetic/local/external-driver-document',decode(repeat('13',32),'hex'),'2099-01-01T00:00:00Z','VALID')
        ON CONFLICT (id) DO UPDATE SET expires_at=EXCLUDED.expires_at,status='VALID';
        """;

    await using var command = new NpgsqlCommand(sql, connection, transaction);
    await command.ExecuteNonQueryAsync();
    await transaction.CommitAsync();
}

static async Task ReportStatusAsync(NpgsqlConnection connection)
{
    const string sql = """
        SELECT
          EXISTS(SELECT 1 FROM pg_roles WHERE rolname='paqueteria_local_app' AND rolcanlogin AND NOT rolinherit AND NOT rolbypassrls),
          EXISTS(SELECT 1 FROM pg_roles WHERE rolname='paqueteria_local_worker' AND rolcanlogin AND NOT rolinherit AND NOT rolbypassrls),
          (SELECT count(*) FROM identity.users WHERE identity_subject IN ('local-subject-dispatcher-mfa','mock-subject-active-driver','mock-subject-external-driver')),
          (SELECT count(*) FROM organizations.organizations WHERE id IN ('11111111-1111-1111-1111-111111111111','33333333-3333-3333-3333-333333333333')),
          pg_has_role('paqueteria_local_app','paqueteria_app','member')
            AND NOT pg_has_role('paqueteria_local_app','paqueteria_worker','member'),
          pg_has_role('paqueteria_local_worker','paqueteria_worker','member')
            AND NOT pg_has_role('paqueteria_local_worker','paqueteria_app','member'),
          NOT has_function_privilege('paqueteria_app','security.claim_notifications_outbox(text,integer,interval)','EXECUTE')
            AND has_function_privilege('paqueteria_worker','security.claim_notifications_outbox(text,integer,interval)','EXECUTE');
        """;
    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    if (!reader.GetBoolean(0) || !reader.GetBoolean(1) || reader.GetInt64(2) != 3 || reader.GetInt64(3) != 2 ||
        !reader.GetBoolean(4) || !reader.GetBoolean(5) || !reader.GetBoolean(6))
    {
        throw new InvalidOperationException("Local runtime roles or deterministic seed are incomplete.");
    }

    Console.WriteLine("DevSeed ready: runtime roles=2, synthetic identities=3, organizations=2.");
}

static async Task ReportSyntheticSeedStatusAsync(NpgsqlConnection connection)
{
    const string sql = """
        SELECT
          (SELECT count(*) FROM identity.users WHERE identity_subject IN ('local-subject-dispatcher-mfa','mock-subject-active-driver','mock-subject-external-driver')),
          (SELECT count(*) FROM organizations.organizations WHERE id IN ('11111111-1111-1111-1111-111111111111','33333333-3333-3333-3333-333333333333'));
        """;
    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    if (reader.GetInt64(0) != 3 || reader.GetInt64(1) != 2)
    {
        throw new InvalidOperationException("Deterministic synthetic prerequisites are incomplete.");
    }

    Console.WriteLine("DevSynthetic prerequisites ready: synthetic identities=3, organizations=2.");
}

static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
{
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Required environment variable is missing: {name}.");

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
