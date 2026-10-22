using Npgsql;
using NpgsqlTypes;
using Paqueteria.IntegrationTests.Custody;
using Paqueteria.IntegrationTests.Driver;
using Paqueteria.IntegrationTests.Observability;
using Paqueteria.IntegrationTests.Realtime;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.PlatformEndToEnd;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlatformEndToEndCollection : ICollectionFixture<PlatformEndToEndFixture>
{
    public const string Name = "OPS-005 platform end-to-end";
}

/// <summary>
/// OPS-005: what the suite shares, assembled once with the IntegrationTests harness. PostgreSQL 18/PostGIS with the
/// baseline, the module migrations and the API, Worker and realtime logins (all <c>NOBYPASSRLS</c>); the S3-compatible
/// storage container the driver PWA job already uses, reached by the browser through a loopback TLS tunnel because the
/// driver app only accepts HTTPS signed uploads; and the production Next.js build of the web served by
/// <c>next start</c> for the API origin every test starts its own API on. Each test then starts its own API and Worker
/// (<see cref="PlatformHosts"/>), so a test-only configuration never leaks into another test.
/// </summary>
/// <remarks>
/// Synthetic data only (SEC-003): the PostgreSQL fixture's synthetic organizations, users and driver profiles, plus the
/// rows <see cref="SeedAsync"/> adds. No name, address, phone number, e-mail address or token is real.
/// </remarks>
public sealed class PlatformEndToEndFixture : IAsyncLifetime
{
    internal const string TermsVersion = "ops005-synthetic-terms-v1";
    internal const string PrivacyVersion = "ops005-synthetic-privacy-v1";

    internal static readonly Guid OrganizationId = PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId;
    internal static readonly Guid DriverId = PostgreSqlSecurityWebApplicationFactory.ActiveDriverId;

    /// <summary>The synthetic city the PostgreSQL fixture seeds; the active driver's home city.</summary>
    internal static readonly Guid CityId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    internal static readonly Guid DispatcherUserId = Guid.Parse("0e500500-0000-4000-8000-000000000001");
    internal static readonly Guid ServiceAreaId = Guid.Parse("0e500500-0000-4000-8000-000000000002");
    internal static readonly Guid OperatingZoneId = Guid.Parse("0e500500-0000-4000-8000-000000000003");
    internal static readonly Guid TariffRuleId = Guid.Parse("0e500500-0000-4000-8000-000000000004");

    /// <summary>The synthetic tariff, in centavos (invariant 4).</summary>
    internal const long TariffAmountCents = 12_000;

    private const string TelemetryEnabledVariable = "TelemetryExport__Enabled";
    private const string TelemetryConnectionStringVariable = "TelemetryExport__ConnectionString";

    private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);
    private LoopbackPortLease? _apiPort;
    private LoopbackPortLease? _webPort;
    private LoopbackTlsTunnel? _storageTunnel;
    private DriverStopsNextServer? _web;
    private TraceHeaderCaptureServer? _ingestion;

    public PlatformEndToEndFixture()
    {
        // Started before any host of the suite exists, so every outgoing request of this process is seen.
        _ = OutboundHttpRequests.Instance;
    }

    internal PostgreSqlSecurityWebApplicationFactory Database { get; } = new();

    internal CorsCapableS3Fixture Storage { get; } = new();

    /// <summary>
    /// The API listens here on loopback TLS in every test; the web build has this origin inlined. Leased for the whole
    /// suite (<see cref="LoopbackPortLease"/>), so nothing else takes the port between one test's API and the next.
    /// </summary>
    internal LoopbackPortLease ApiPortLease =>
        _apiPort ?? throw new InvalidOperationException("The fixture is not initialized.");

    internal int ApiPort => ApiPortLease.Port;

    internal int WebPort =>
        _webPort?.Port ?? throw new InvalidOperationException("The fixture is not initialized.");

    internal string ApiOrigin => $"https://127.0.0.1:{ApiPort}";

    internal string WebOrigin => $"http://127.0.0.1:{WebPort}";

    internal Uri WebAddress => _web?.BaseAddress ?? throw new InvalidOperationException("Next.js is not running.");

    /// <summary>The HTTPS origin the API presigns browser uploads for; it relays byte for byte to the storage container.</summary>
    internal string StorageOrigin =>
        _storageTunnel?.Origin.GetLeftPart(UriPartial.Authority)
        ?? throw new InvalidOperationException("The storage tunnel is not running.");

    /// <summary>
    /// A loopback server named as the Application Insights ingestion endpoint of the synthetic connection string the
    /// hosts receive with the export off, as the pilot gives both hosts its connection string with the export off.
    /// Anything that tried to export would land here instead of leaving the machine.
    /// </summary>
    internal TraceHeaderCaptureServer Ingestion =>
        _ingestion ?? throw new InvalidOperationException("The ingestion capture is not running.");

    internal string TelemetryConnectionString =>
        $"InstrumentationKey=00000000-0000-4000-8000-0000000e5005;IngestionEndpoint={Ingestion.BaseAddress.AbsoluteUri}";

    public async Task InitializeAsync()
    {
        _ingestion = await TraceHeaderCaptureServer.StartAsync();
        // As the pilot sets them (apps.bicep: TelemetryExport__Enabled 'false' and the component's connection string),
        // as environment variables, which both host compositions read before deciding what to register.
        foreach (var (name, value) in new[]
                 {
                     (TelemetryEnabledVariable, "false"),
                     (TelemetryConnectionStringVariable, TelemetryConnectionString),
                 })
        {
            _previousEnvironment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        await Task.WhenAll(Database.InitializeAsync(), Storage.InitializeAsync());
        // Leased once the containers run: Docker picks their host ports itself, without asking the kernel.
        _apiPort = LoopbackPortLease.Acquire();
        _webPort = LoopbackPortLease.Acquire();
        _storageTunnel = LoopbackTlsTunnel.Start(
            new Uri(Storage.Endpoint),
            RealtimeKestrelWebApplicationFactory.LoopbackCertificate);
        await Storage.ConfigureBrowserCorsAsync(new Uri(WebOrigin));
        await SeedAsync();
        _web = await DriverStopsNextServer.StartProductionAsync(
            ApiOrigin,
            _webPort,
            connectSources: StorageOrigin,
            runtimeEnvironment: new Dictionary<string, string>
            {
                ["PAQUETERIA_TERMS_VERSION"] = TermsVersion,
                ["PAQUETERIA_PRIVACY_VERSION"] = PrivacyVersion,
            });
    }

    public async Task DisposeAsync()
    {
        var failures = new List<Exception>();
        async Task Run(Func<ValueTask> dispose)
        {
            try
            {
                await dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (_web is not null) await Run(_web.DisposeAsync);
        if (_storageTunnel is not null) await Run(_storageTunnel.DisposeAsync);
        await Run(async () => await Storage.DisposeAsync());
        await Run(async () => await Database.DisposeAsync());
        if (_ingestion is not null) await Run(_ingestion.DisposeAsync);
        _webPort?.Dispose();
        _apiPort?.Dispose();
        foreach (var (name, value) in _previousEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(failures);
        }
    }

    /// <summary>The login of each runtime pool, each a member of exactly one runtime role.</summary>
    internal IReadOnlyDictionary<string, string> RuntimeLogins => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [new NpgsqlConnectionStringBuilder(Database.ApplicationConnectionString).Username!] = "paqueteria_app",
        [new NpgsqlConnectionStringBuilder(Database.RealtimeConnectionString).Username!] = "paqueteria_realtime",
        [new NpgsqlConnectionStringBuilder(Database.WorkerConnectionString).Username!] = "paqueteria_worker",
    };

    internal async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default! : (T)result;
    }

    internal async Task<IReadOnlyList<object?[]>> RowsAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
            {
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// The synthetic operation the journey needs on top of the PostgreSQL fixture's seed: the dispatcher of the
    /// synthetic organization (the mock identity <c>active-dispatcher</c>), a service area, an operating zone and a
    /// SAME_DAY tariff around the synthetic coordinates, and the active driver's service area and valid identity
    /// document, so the driver is eligible for the order. Test setup only, through the administration connection;
    /// the hosts never run with it.
    /// </summary>
    private async Task SeedAsync()
    {
        await using var connection = new NpgsqlConnection(Database.AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO identity.users(id,identity_subject,status)
            VALUES (@dispatcher,'mock-subject-active-dispatcher','ACTIVE');

            INSERT INTO organizations.organization_memberships(
              id,user_id,organization_id,role,status,is_default)
            VALUES (gen_random_uuid(),@dispatcher,@org,'DISPATCHER','ACTIVE',true);

            INSERT INTO locations.service_areas(id,owner_org_id,city_id,name,polygon,status)
            VALUES (
              @area,@org,@city,'OPS-005 Synthetic Service Area',
              ST_GeomFromText(
                'MULTIPOLYGON(((-107.60 24.60,-107.20 24.60,-107.20 25.00,-107.60 25.00,-107.60 24.60)))',
                4326),
              'ACTIVE');

            INSERT INTO locations.operating_zones(
              id,owner_org_id,service_area_id,name,zone_type,polygon,status)
            VALUES (
              @zone,@org,@area,'OPS-005 Synthetic Core','CORE',
              ST_GeomFromText(
                'MULTIPOLYGON(((-107.50 24.70,-107.30 24.70,-107.30 24.90,-107.50 24.90,-107.50 24.70)))',
                4326),
              'ACTIVE');

            INSERT INTO pricing.tariff_rules(
              id,owner_org_id,city_id,service_area_id,operating_zone_id,
              pricing_tier,service_type,amount_cents,tax_mode,active_from,status,policy_version)
            VALUES (
              @tariff,@org,@city,@area,@zone,'OCCASIONAL','SAME_DAY',
              @amount,'VAT_INCLUDED',clock_timestamp()-interval '1 day','ACTIVE','OPS-005-synthetic-v1');

            INSERT INTO drivers.driver_service_areas(driver_id,service_area_id,org_id,status)
            VALUES (@driver,@area,@org,'ACTIVE');

            INSERT INTO drivers.driver_documents(
              id,driver_id,org_id,document_type,object_key,sha256,status)
            VALUES (
              gen_random_uuid(),@driver,@org,'IDENTITY',
              'synthetic/ops005/driver-document-01',@document_hash,'VALID');
            """,
            connection,
            transaction)
        {
            CommandTimeout = 30,
        };
        command.Parameters.Add(Uuid("dispatcher", DispatcherUserId));
        command.Parameters.Add(Uuid("org", OrganizationId));
        command.Parameters.Add(Uuid("city", CityId));
        command.Parameters.Add(Uuid("area", ServiceAreaId));
        command.Parameters.Add(Uuid("zone", OperatingZoneId));
        command.Parameters.Add(Uuid("tariff", TariffRuleId));
        command.Parameters.Add(Uuid("driver", DriverId));
        command.Parameters.Add(new NpgsqlParameter<long>("amount", NpgsqlDbType.Bigint) { TypedValue = TariffAmountCents });
        command.Parameters.Add(new NpgsqlParameter<byte[]>("document_hash", NpgsqlDbType.Bytea)
        {
            TypedValue = Enumerable.Repeat((byte)0x05, 32).ToArray(),
        });
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static NpgsqlParameter Uuid(string name, Guid value) =>
        new NpgsqlParameter<Guid>(name, NpgsqlDbType.Uuid) { TypedValue = value };
}
