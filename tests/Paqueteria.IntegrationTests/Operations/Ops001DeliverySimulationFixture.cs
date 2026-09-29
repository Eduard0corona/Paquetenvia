using Custody.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.IntegrationTests.Custody;
using Paqueteria.IntegrationTests.Realtime;
using Paqueteria.IntegrationTests.Security;
using Realtime.Application.Dispatching;

namespace Paqueteria.IntegrationTests.Operations;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Ops001DeliverySimulationCollection :
    ICollectionFixture<Ops001DeliverySimulationFixture>
{
    public const string Name = "OPS-001 delivery simulation";
}

public sealed class Ops001DeliverySimulationFixture : IAsyncLifetime
{
    internal PostgreSqlSecurityWebApplicationFactory Database { get; } = new();
    internal CorsCapableS3Fixture Storage { get; } = new();

    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();
        try
        {
            await Storage.InitializeAsync();
        }
        catch
        {
            await Database.DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        Exception? storageFailure = null;
        try
        {
            await Storage.DisposeAsync();
        }
        catch (Exception exception)
        {
            storageFailure = exception;
        }

        await Database.DisposeAsync();
        if (storageFailure is not null)
        {
            throw storageFailure;
        }
    }

    internal RealtimeKestrelWebApplicationFactory CreateApiHost(
        Ops001ScenarioData data,
        string? workerConnectionString = null,
        IRealtimeOutboxFailureInjector? failureInjector = null,
        ILoggerProvider? logProvider = null,
        RealtimeAuthorizationRecorder? authorizationRecorder = null,
        int port = 0)
    {
        var configuration = CreateApplicationConfiguration(data);
        return new RealtimeKestrelWebApplicationFactory(
            Database.ApplicationConnectionString,
            authorizationRecorder ?? new RealtimeAuthorizationRecorder(),
            port,
            workerConnectionString,
            failureInjector,
            logProvider,
            configurationOverrides: configuration,
            enableDispatch: true);
    }

    internal IHost CreateProofWorker()
    {
        var settings = Storage.CreateProofStorageConfiguration()
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        settings["ConnectionStrings:Paqueteria"] = Database.WorkerConnectionString;
        settings["ProofStorage:MaximumConcurrency"] = "1";

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing",
            ApplicationName = "Paqueteria.OPS001.ProofWorker",
        });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddCustodyInfrastructure(
            builder.Configuration,
            builder.Environment);
        return builder.Build();
    }

    internal async Task BootstrapAsync(
        Ops001ScenarioData data,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(Database.AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO organizations.organizations(
              id,legal_name,display_name,organization_type)
            VALUES
              (@org,'OPS-001 Synthetic Owner','OPS-001 Synthetic Owner','BUSINESS'),
              (@decoy_org,'OPS-001 Synthetic Decoy','OPS-001 Synthetic Decoy','BUSINESS');

            INSERT INTO identity.users(id,identity_subject,status)
            VALUES (@dispatcher,'mock-subject-active-dispatcher','ACTIVE')
            ON CONFLICT (id) DO UPDATE SET status='ACTIVE';

            INSERT INTO organizations.organization_memberships(
              id,user_id,organization_id,role,status,is_default)
            VALUES (
              gen_random_uuid(),@dispatcher,@org,'DISPATCHER','ACTIVE',false);

            INSERT INTO locations.cities(
              id,country_code,state_code,name,timezone,status)
            VALUES (
              @city,'MX','SIN',@city_name,'America/Mazatlan','ACTIVE');

            INSERT INTO locations.service_areas(
              id,owner_org_id,city_id,name,polygon,status)
            VALUES (
              @area,@org,@city,'OPS-001 Synthetic Service Area',
              ST_GeomFromText(
                'MULTIPOLYGON(((-107.60 24.60,-107.20 24.60,-107.20 25.00,-107.60 25.00,-107.60 24.60)))',
                4326),
              'ACTIVE');

            INSERT INTO locations.operating_zones(
              id,owner_org_id,service_area_id,name,zone_type,polygon,status)
            VALUES (
              @zone,@org,@area,'OPS-001 Synthetic Core','CORE',
              ST_GeomFromText(
                'MULTIPOLYGON(((-107.50 24.70,-107.30 24.70,-107.30 24.90,-107.50 24.90,-107.50 24.70)))',
                4326),
              'ACTIVE');

            INSERT INTO pricing.tariff_rules(
              id,owner_org_id,city_id,service_area_id,operating_zone_id,
              pricing_tier,service_type,amount_cents,tax_mode,active_from,status,policy_version)
            VALUES (
              @tariff,@org,@city,@area,@zone,'OCCASIONAL','SAME_DAY',
              12000,'VAT_INCLUDED',clock_timestamp()-interval '1 day','ACTIVE','OPS-001-synthetic-v1');
            """,
            cancellationToken,
            P("org", data.OrganizationId),
            P("decoy_org", data.DecoyOrganizationId),
            P("dispatcher", data.DispatcherUserId),
            P("city", data.CityId),
            new NpgsqlParameter<string>("city_name", NpgsqlDbType.Text)
            {
                TypedValue = $"OPS-001 Synthetic City {data.RunNumber:D2}",
            },
            P("area", data.ServiceAreaId),
            P("zone", data.OperatingZoneId),
            P("tariff", data.TariffRuleId));

        for (var index = 0; index < Ops001ScenarioData.DriverCount; index++)
        {
            var userId = data.DriverUserIds[index];
            var driverId = data.DriverIds[index];
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO identity.users(id,identity_subject,status)
                VALUES (@user_id,@subject,'ACTIVE');

                INSERT INTO organizations.organization_memberships(
                  id,user_id,organization_id,role,status,is_default)
                VALUES (
                  gen_random_uuid(),@user_id,@org,'DRIVER','ACTIVE',false);

                INSERT INTO drivers.driver_profiles(
                  id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
                VALUES (
                  @driver_id,@user_id,@org,@city,'OWN','MOTORCYCLE','ACTIVE');

                INSERT INTO drivers.driver_service_areas(
                  driver_id,service_area_id,org_id,status)
                VALUES (@driver_id,@area,@org,'ACTIVE');

                INSERT INTO drivers.driver_documents(
                  id,driver_id,org_id,document_type,object_key,sha256,status)
                VALUES (
                  gen_random_uuid(),@driver_id,@org,'IDENTITY',
                  @object_key,@document_hash,'VALID');
                """,
                cancellationToken,
                P("user_id", userId),
                new NpgsqlParameter<string>("subject", NpgsqlDbType.Text)
                {
                    TypedValue = $"ops001-driver-{data.RunNumber:D2}-{index + 1:D2}",
                },
                P("org", data.OrganizationId),
                P("driver_id", driverId),
                P("city", data.CityId),
                P("area", data.ServiceAreaId),
                new NpgsqlParameter<string>("object_key", NpgsqlDbType.Text)
                {
                    TypedValue = $"synthetic/ops001/{data.RunNumber:D2}/driver-document-{index + 1:D2}",
                },
                new NpgsqlParameter<byte[]>("document_hash", NpgsqlDbType.Bytea)
                {
                    TypedValue = Enumerable.Repeat((byte)(index + 1), 32).ToArray(),
                });
        }

        await InsertDecoyOrderAsync(connection, transaction, data, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private IReadOnlyDictionary<string, string?> CreateApplicationConfiguration(
        Ops001ScenarioData data)
    {
        var settings = Storage.CreateProofStorageConfiguration()
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        settings["Locations:Provider"] = "PostgreSql";
        settings["Locations:GeocodingProvider"] = "Mock";
        settings["Locations:PiiProtector"] = "Mock";
        settings["Locations:CommandTimeoutSeconds"] = "30";
        settings["Pricing:Provider"] = "PostgreSql";
        settings["Pricing:CommandTimeoutSeconds"] = "30";
        settings["Pricing:QuoteLifetimeMinutes"] = "30";
        settings["Orders:Provider"] = "PostgreSql";
        settings["Drivers:Provider"] = "PostgreSql";
        settings["Dispatch:Provider"] = "PostgreSql";
        settings["Dispatch:AssignmentPolicyVersion"] = "OPS-001-synthetic-v1";
        settings["Realtime:OutboxDispatcher:WorkerId"] =
            $"ops001-r{data.RunNumber:D2}";
        settings["Realtime:OutboxDispatcher:Business:BatchSize"] = "10";
        settings["Realtime:OutboxDispatcher:Business:MaximumConcurrency"] = "4";
        settings["Realtime:OutboxDispatcher:Business:MaximumAttempts"] = "4";
        settings["Realtime:OutboxDispatcher:Business:LeaseSeconds"] = "15";
        settings["Realtime:OutboxDispatcher:Business:PollIntervalMilliseconds"] = "50";
        settings["Realtime:OutboxDispatcher:StaleRequeueIntervalSeconds"] = "1";
        settings["PublicTracking:LookupPermitLimit"] = "1000";
        settings["PublicTracking:LookupWindowSeconds"] = "60";

        foreach (var vehicle in new[] { "MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER" })
        {
            settings[$"Drivers:Eligibility:RequiredDocumentTypesByVehicleType:{vehicle}:0"] =
                "IDENTITY";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumPackageCount"] =
                "100";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumTotalWeightGrams"] =
                "100000";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumSinglePackageWeightGrams"] =
                "10000";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumLengthMillimeters"] =
                "2000";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumWidthMillimeters"] =
                "2000";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumHeightMillimeters"] =
                "2000";
        }

        settings["Drivers:Eligibility:PolicyVersion"] = "OPS-001-synthetic-v1";
        settings["Drivers:Eligibility:NonExpiringDocumentTypes:0"] = "IDENTITY";
        return settings;
    }

    private static async Task InsertDecoyOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Ops001ScenarioData data,
        CancellationToken cancellationToken)
    {
        var origin = data.ResourceGuid("decoy-origin");
        var destination = data.ResourceGuid("decoy-destination");
        var quote = data.ResourceGuid("decoy-quote");
        var order = data.ResourceGuid("decoy-order");
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO locations.locations(
              id,owner_org_id,city_id,point,address_ciphertext,address_summary,
              pii_key_version)
            VALUES
              (@origin,@decoy_org,@city,
               ST_SetSRID(ST_MakePoint(-107.45,24.75),4326),
               decode('00','hex'),'OPS-001 DECOY ORIGIN','synthetic-v1'),
              (@destination,@decoy_org,@city,
               ST_SetSRID(ST_MakePoint(-107.44,24.76),4326),
               decode('01','hex'),'OPS-001 DECOY DESTINATION','synthetic-v1');

            INSERT INTO pricing.quotes(
              id,owner_org_id,city_id,origin_location_id,destination_location_id,
              service_type,pricing_tier,consolidated_route,subtotal_cents,
              discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,
              currency,pricing_policy_version,request_snapshot_redacted,
              package_snapshot,breakdown,input_hash,status,expires_at)
            VALUES (
              @quote,@decoy_org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',
              false,12000,0,0,12000,12000,'MXN','OPS-001-synthetic-v1','{}',
              '[{"description":"[REDACTED]","weight_grams":1000,"declared_value_cents":0}]',
              '[]',decode(repeat('00',32),'hex'),'USED',
              clock_timestamp()+interval '1 hour');

            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,city_id,origin_location_id,
              destination_location_id,service_type,pricing_tier,consolidated_route,
              payer_type,status,subtotal_cents,discount_cents,tax_cents,total_cents,
              minimum_total_cents_snapshot,currency,pricing_policy_version,
              package_snapshot,cod_expected_cents,version)
            VALUES (
              @order,@public_id,@quote,@decoy_org,@city,@origin,@destination,
              'SAME_DAY','OCCASIONAL',false,'SENDER','DELIVERED',
              12000,0,0,12000,12000,'MXN','OPS-001-synthetic-v1',
              '[{"description":"[REDACTED]","weight_grams":1000,"declared_value_cents":0}]',
              0,9);
            """,
            cancellationToken,
            P("origin", origin),
            P("destination", destination),
            P("decoy_org", data.DecoyOrganizationId),
            P("city", data.CityId),
            P("quote", quote),
            P("order", order),
            new NpgsqlParameter<string>("public_id", NpgsqlDbType.Text)
            {
                TypedValue = $"OPS001-DECOY-{data.RunNumber:D2}",
            });
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = 30,
        };
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter P(string name, Guid value) =>
        new(name, NpgsqlDbType.Uuid) { Value = value };
}
