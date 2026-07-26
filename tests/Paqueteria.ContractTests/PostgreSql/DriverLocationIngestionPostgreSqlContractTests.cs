using Drivers.Application.Locations;
using Drivers.Domain.Location;
using Drivers.Infrastructure;
using Drivers.Infrastructure.Locations;
using Drivers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class DriverLocationIngestionPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 7, 24, 20, 0, 0, TimeSpan.Zero);

    [PostgreSqlContractFact]
    public async Task Batch_persists_every_valid_point_deduplicates_and_emits_minimal_throttled_outbox()
    {
        var scenario = await SeedAsync();
        try
        {
            await using var scope = CreateScope(CapturedAt.AddMinutes(5));
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            var result = await scope.Service.PublishAsync(
                Command(
                    scenario,
                    Point(firstId, CapturedAt),
                    Point(secondId, CapturedAt.AddSeconds(5), latitude: 24.80911),
                    Point(firstId, CapturedAt.AddSeconds(6)),
                    Point(Guid.NewGuid(), CapturedAt.AddSeconds(7), latitude: 91)),
                default);

            Assert.Equal(
                [DriverLocationItemStatus.Accepted, DriverLocationItemStatus.Accepted,
                    DriverLocationItemStatus.Duplicate, DriverLocationItemStatus.Rejected],
                result.Items.Select(value => value.Status));
            Assert.Equal(result.Items[0].PositionId, result.Items[2].PositionId);
            Assert.Equal((1, 1, 2), (result.PublishedCount, result.SuppressedCount, result.AcceptedCount));
            Assert.Equal(1, result.DuplicateCount);
            Assert.Equal(1, result.RejectedCount);

            var replay = await scope.Service.PublishAsync(
                Command(scenario, Point(firstId, CapturedAt), Point(secondId, CapturedAt.AddSeconds(5))),
                default);
            Assert.All(replay.Items, value => Assert.Equal(DriverLocationItemStatus.Duplicate, value.Status));
            Assert.Equal(result.Items[0].PositionId, replay.Items[0].PositionId);
            Assert.Equal(result.Items[1].PositionId, replay.Items[1].PositionId);

            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                SELECT p.client_event_id,ST_SRID(p.point),ST_X(p.point),ST_Y(p.point),
                       p.accuracy_m,p.heading_degrees,p.speed_mps,p.captured_at,p.received_at,
                       p.publish_realtime,
                       o.topic,o.payload,o.status,o.attempts,o.available_at,o.created_at,
                       o.locked_at,o.locked_by,o.lease_token,o.lease_expires_at,o.last_error,o.processed_at
                FROM drivers.driver_positions p
                LEFT JOIN platform.location_outbox_events o ON o.driver_position_id=p.id
                WHERE p.driver_id=@driver
                ORDER BY p.captured_at;
                """);
            command.Parameters.AddWithValue("driver", scenario.DriverId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(firstId, reader.GetGuid(0));
            Assert.Equal(4326, reader.GetInt32(1));
            Assert.Equal(-107.394, reader.GetDouble(2), 6);
            Assert.Equal(24.8091, reader.GetDouble(3), 6);
            Assert.Equal(8.50m, reader.GetDecimal(4));
            Assert.Equal(180m, reader.GetDecimal(5));
            Assert.Equal(7.20m, reader.GetDecimal(6));
            Assert.Equal(CapturedAt, reader.GetFieldValue<DateTimeOffset>(7));
            Assert.Equal(CapturedAt.AddMinutes(5), reader.GetFieldValue<DateTimeOffset>(8));
            Assert.True(reader.GetBoolean(9));
            Assert.Equal("drivers.location-updated", reader.GetString(10));
            var payload = reader.GetFieldValue<System.Text.Json.JsonDocument>(11).RootElement;
            Assert.Equal(
                ["accuracy_m", "captured_at", "driver_id", "driver_position_id", "lat", "lng", "schema_version"],
                payload.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
            Assert.Equal("driver-location-updated-v1", payload.GetProperty("schema_version").GetString());
            Assert.Equal(scenario.DriverId, payload.GetProperty("driver_id").GetGuid());
            Assert.Equal(result.Items[0].PositionId, payload.GetProperty("driver_position_id").GetGuid());
            Assert.Equal("PENDING", reader.GetString(12));
            Assert.Equal(0, reader.GetInt32(13));
            Assert.Equal(CapturedAt.AddMinutes(5), reader.GetFieldValue<DateTimeOffset>(14));
            Assert.Equal(CapturedAt.AddMinutes(5), reader.GetFieldValue<DateTimeOffset>(15));
            for (var ordinal = 16; ordinal <= 21; ordinal++)
            {
                Assert.True(reader.IsDBNull(ordinal));
            }

            Assert.True(await reader.ReadAsync());
            Assert.Equal(secondId, reader.GetGuid(0));
            Assert.False(reader.GetBoolean(9));
            Assert.True(reader.IsDBNull(10));
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Location_persistence_and_outbox_truncate_captured_and_received_timestamps()
    {
        var rawCaptured = DateTimeOffset.Parse(
            "2026-07-25T18:00:00.1234567Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var rawReceived = rawCaptured.AddMinutes(5);
        var canonicalCaptured = UtcMicrosecondPrecision.Normalize(rawCaptured);
        var canonicalReceived = UtcMicrosecondPrecision.Normalize(rawReceived);
        var scenario = await SeedAsync();
        try
        {
            await using var scope = CreateScope(rawReceived);
            var eventId = Guid.NewGuid();
            var created = await scope.Service.PublishAsync(
                Command(scenario, Point(eventId, rawCaptured)),
                default);
            var replay = await scope.Service.PublishAsync(
                Command(scenario, Point(eventId, rawCaptured)),
                default);

            Assert.Equal(DriverLocationItemStatus.Accepted, created.Items[0].Status);
            Assert.Equal(DriverLocationItemStatus.Duplicate, replay.Items[0].Status);
            Assert.Equal(created.Items[0].PositionId, replay.Items[0].PositionId);
            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                SELECT p.captured_at,p.received_at,
                       o.payload->>'captured_at',o.created_at,o.available_at
                FROM drivers.driver_positions p
                JOIN platform.location_outbox_events o ON o.driver_position_id=p.id
                WHERE p.driver_id=@driver AND p.client_event_id=@event
                """);
            command.Parameters.AddWithValue("driver", scenario.DriverId);
            command.Parameters.AddWithValue("event", eventId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(canonicalCaptured, reader.GetFieldValue<DateTimeOffset>(0));
            Assert.Equal(canonicalReceived, reader.GetFieldValue<DateTimeOffset>(1));
            Assert.Equal(
                canonicalCaptured,
                DateTimeOffset.Parse(
                    reader.GetString(2),
                    System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(canonicalReceived, reader.GetFieldValue<DateTimeOffset>(3));
            Assert.Equal(canonicalReceived, reader.GetFieldValue<DateTimeOffset>(4));
            Assert.Equal(0, canonicalCaptured.UtcTicks % 10);
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Canonical_empty_event_id_is_rejected_without_position_or_outbox()
    {
        var scenario = await SeedAsync();
        try
        {
            await using var scope = CreateScope(CapturedAt.AddMinutes(5));
            var result = await scope.Service.PublishAsync(
                Command(scenario, Point(Guid.Empty, CapturedAt)),
                default);

            var item = Assert.Single(result.Items);
            Assert.Equal(Guid.Empty, item.ClientEventId);
            Assert.Equal(DriverLocationItemStatus.Rejected, item.Status);
            Assert.Equal(DriverLocationRejectionCodes.InvalidClientEventId, item.ErrorCode);
            Assert.Null(item.PositionId);
            Assert.False(item.Duplicate);
            Assert.Equal(0, result.PublishedCount);
            Assert.Equal(0L, await ScalarAdminAsync(
                "SELECT count(*) FROM drivers.driver_positions WHERE driver_id=@driver",
                Uuid("driver", scenario.DriverId)));
            Assert.Equal(0L, await ScalarAdminAsync(
                "SELECT count(*) FROM platform.location_outbox_events WHERE owner_org_id=@org",
                Uuid("org", scenario.OrganizationId)));
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Publication_policy_is_deterministic_for_distance_silence_and_out_of_order_batches()
    {
        var scenario = await SeedAsync();
        try
        {
            await using var scope = CreateScope(CapturedAt.AddMinutes(10));
            var values = new[]
            {
                Point(Guid.NewGuid(), CapturedAt.AddSeconds(70), latitude: 24.80911),
                Point(Guid.NewGuid(), CapturedAt.AddSeconds(10), latitude: 24.8100),
                Point(Guid.NewGuid(), CapturedAt),
                Point(Guid.NewGuid(), CapturedAt.AddSeconds(9), latitude: 24.9),
            };
            var result = await scope.Service.PublishAsync(Command(scenario, values), default);
            Assert.Equal(4, result.AcceptedCount);
            Assert.Equal(3, result.PublishedCount);
            Assert.Equal(1, result.SuppressedCount);

            await using var query = fixture.AdminDataSource.CreateCommand(
                """
                SELECT client_event_id,publish_realtime
                FROM drivers.driver_positions
                WHERE driver_id=@driver
                ORDER BY captured_at;
                """);
            query.Parameters.AddWithValue("driver", scenario.DriverId);
            await using var reader = await query.ExecuteReaderAsync();
            var publication = new Dictionary<Guid, bool>();
            while (await reader.ReadAsync())
            {
                publication.Add(reader.GetGuid(0), reader.GetBoolean(1));
            }
            Assert.True(publication[values[2].ClientEventId]);
            Assert.False(publication[values[3].ClientEventId]);
            Assert.True(publication[values[1].ClientEventId]);
            Assert.True(publication[values[0].ClientEventId]);
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_contexts_serialize_deduplication_and_create_one_position_and_outbox()
    {
        var scenario = await SeedAsync();
        try
        {
            await using var first = CreateScope(CapturedAt.AddMinutes(5), maxPoolSize: 2);
            await using var second = CreateScope(CapturedAt.AddMinutes(5), maxPoolSize: 2);
            var eventId = Guid.NewGuid();
            var command = Command(scenario, Point(eventId, CapturedAt));
            var results = await Task.WhenAll(
                first.Service.PublishAsync(command, default),
                second.Service.PublishAsync(command, default));
            Assert.Equal(1, results.Sum(value => value.AcceptedCount));
            Assert.Equal(1, results.Sum(value => value.DuplicateCount));
            Assert.Equal(results[0].Items[0].PositionId, results[1].Items[0].PositionId);
            Assert.Equal(1L, await ScalarAdminAsync(
                "SELECT count(*) FROM drivers.driver_positions WHERE driver_id=@driver AND client_event_id=@event",
                Uuid("driver", scenario.DriverId),
                Uuid("event", eventId)));
            Assert.Equal(1L, await ScalarAdminAsync(
                """
                SELECT count(*) FROM platform.location_outbox_events o
                JOIN drivers.driver_positions p ON p.id=o.driver_position_id
                WHERE p.driver_id=@driver AND p.client_event_id=@event
                """,
                Uuid("driver", scenario.DriverId),
                Uuid("event", eventId)));
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Same_event_id_is_scoped_by_driver_and_tenant_context_does_not_leak_through_pooling()
    {
        var firstScenario = await SeedAsync();
        Scenario? secondScenario = null;
        try
        {
            secondScenario = await SeedAsync();
            var eventId = Guid.NewGuid();
            await using (var first = CreateScope(CapturedAt.AddMinutes(5)))
            {
                var result = await first.Service.PublishAsync(
                    Command(firstScenario, Point(eventId, CapturedAt)),
                    default);
                Assert.Equal(1, result.AcceptedCount);
            }
            await using (var second = CreateScope(CapturedAt.AddMinutes(5)))
            {
                var result = await second.Service.PublishAsync(
                    Command(secondScenario, Point(eventId, CapturedAt)),
                    default);
                Assert.Equal(1, result.AcceptedCount);
            }

            Assert.Equal(2L, await ScalarAdminAsync(
                "SELECT count(*) FROM drivers.driver_positions WHERE client_event_id=@event",
                Uuid("event", eventId)));
            Assert.Equal(1L, await CountVisiblePositionsAsync(
                firstScenario.UserId,
                firstScenario.OrganizationId,
                eventId));
            Assert.Equal(1L, await CountVisiblePositionsAsync(
                secondScenario.UserId,
                secondScenario.OrganizationId,
                eventId));
            Assert.Equal(0L, await CountVisiblePositionsAsync(
                firstScenario.UserId,
                Guid.NewGuid(),
                eventId));

            var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAdminAsync(
                """
                INSERT INTO drivers.driver_positions
                  (id,driver_id,org_id,city_id,client_event_id,point,accuracy_m,
                   heading_degrees,speed_mps,captured_at,received_at,publish_realtime)
                VALUES
                  (@id,@driver,@org,@city,@event,
                   ST_SetSRID(ST_MakePoint(-107.394,24.8091),4326),
                   8.5,NULL,NULL,@captured,@received,false);
                """,
                Uuid("id", Guid.NewGuid()),
                Uuid("driver", firstScenario.DriverId),
                Uuid("org", firstScenario.OrganizationId),
                Uuid("city", firstScenario.CityId),
                Uuid("event", eventId),
                Timestamp("captured", CapturedAt),
                Timestamp("received", CapturedAt.AddMinutes(5))));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        }
        finally
        {
            if (secondScenario is not null)
            {
                await CleanupAsync(secondScenario);
            }
            await CleanupAsync(firstScenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Capability_precedes_dedup_and_outbox_privileges_remain_insert_only()
    {
        var scenario = await SeedAsync();
        try
        {
            await using var scope = CreateScope(CapturedAt.AddMinutes(5));
            var eventId = Guid.NewGuid();
            await scope.Service.PublishAsync(Command(scenario, Point(eventId, CapturedAt)), default);
            await ExecuteAdminAsync(
                "UPDATE organizations.organization_memberships SET role='VIEWER' WHERE id=@id",
                Uuid("id", scenario.MembershipId));
            await Assert.ThrowsAsync<DriverLocationForbiddenException>(
                () => scope.Service.PublishAsync(Command(scenario, Point(eventId, CapturedAt)), default));

            foreach (var sql in new[]
            {
                "SELECT count(*) FROM platform.location_outbox_events",
                "UPDATE platform.location_outbox_events SET attempts=attempts+1",
                "DELETE FROM platform.location_outbox_events",
            })
            {
                await using var transaction = await TenantTransaction.BeginAsync(
                    fixture.AppDataSource,
                    "paqueteria_app",
                    scenario.UserId,
                    [scenario.OrganizationId]);
                await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction.Transaction);
                var exception = await Assert.ThrowsAsync<PostgresException>(
                    () => command.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
                await transaction.RollbackAsync();
            }

            await using var security = fixture.AdminDataSource.CreateCommand(
                """
                SELECT c.relrowsecurity,c.relforcerowsecurity,
                       NOT r.rolbypassrls,
                       has_table_privilege('paqueteria_app','platform.location_outbox_events','INSERT'),
                       has_table_privilege('paqueteria_app','platform.location_outbox_events','SELECT'),
                       has_table_privilege('paqueteria_app','platform.location_outbox_events','UPDATE'),
                       has_table_privilege('paqueteria_app','platform.location_outbox_events','DELETE')
                FROM pg_class c
                JOIN pg_namespace n ON n.oid=c.relnamespace
                JOIN pg_roles r ON r.rolname='paqueteria_app'
                WHERE n.nspname='drivers' AND c.relname='driver_positions';
                """);
            await using var reader = await security.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
            Assert.False(reader.GetBoolean(4));
            Assert.False(reader.GetBoolean(5));
            Assert.False(reader.GetBoolean(6));
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Failure_checkpoints_and_cancellation_roll_back_the_complete_batch()
    {
        var scenario = await SeedAsync();
        try
        {
            var stages = Enum.GetValues<DriverLocationTransactionStage>();
            for (var index = 0; index < stages.Length; index++)
            {
                var eventId = Guid.NewGuid();
                var captured = CapturedAt.AddMinutes(index * 2);
                await using (var failing = CreateScope(
                                 captured.AddMinutes(1),
                                 failureInjector: new ThrowingInjector(stages[index])))
                {
                    await Assert.ThrowsAsync<InjectedFailureException>(
                        () => failing.Service.PublishAsync(
                            Command(scenario, Point(eventId, captured)),
                            default));
                }
                Assert.Equal(0L, await ScalarAdminAsync(
                    "SELECT count(*) FROM drivers.driver_positions WHERE client_event_id=@event",
                    Uuid("event", eventId)));

                await using var retry = CreateScope(captured.AddMinutes(1));
                var accepted = await retry.Service.PublishAsync(
                    Command(scenario, Point(eventId, captured)),
                    default);
                Assert.Equal(1, accepted.AcceptedCount);
            }

            var cancelledId = Guid.NewGuid();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await using var cancelled = CreateScope(CapturedAt.AddHours(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cancelled.Service.PublishAsync(
                    Command(scenario, Point(cancelledId, CapturedAt.AddHours(1))),
                    cancellation.Token));
            Assert.Equal(0L, await ScalarAdminAsync(
                "SELECT count(*) FROM drivers.driver_positions WHERE client_event_id=@event",
                Uuid("event", cancelledId)));

            Assert.Equal(0L, await ScalarAdminAsync(
                """
                SELECT count(*)
                FROM drivers.driver_positions p
                LEFT JOIN platform.location_outbox_events o ON o.driver_position_id=p.id
                WHERE p.driver_id=@driver AND p.publish_realtime AND o.id IS NULL
                """,
                Uuid("driver", scenario.DriverId)));
            Assert.Equal(0L, await ScalarAdminAsync(
                """
                SELECT count(*)
                FROM platform.location_outbox_events o
                LEFT JOIN drivers.driver_positions p ON p.id=o.driver_position_id
                WHERE o.owner_org_id=@org AND p.id IS NULL
                """,
                Uuid("org", scenario.OrganizationId)));
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Transient_failure_reexecutes_the_complete_transaction_without_duplicates()
    {
        var scenario = await SeedAsync();
        try
        {
            var injector = new TransientOnceInjector();
            await using var scope = CreateScope(
                CapturedAt.AddMinutes(5),
                failureInjector: injector);
            var eventId = Guid.NewGuid();

            var result = await scope.Service.PublishAsync(
                Command(scenario, Point(eventId, CapturedAt)),
                default);

            Assert.Equal(1, result.AcceptedCount);
            Assert.Equal(0, result.DuplicateCount);
            Assert.True(injector.InvocationCount >= 2);
            Assert.Equal(1L, await ScalarAdminAsync(
                "SELECT count(*) FROM drivers.driver_positions WHERE driver_id=@driver AND client_event_id=@event",
                Uuid("driver", scenario.DriverId),
                Uuid("event", eventId)));
            Assert.Equal(1L, await ScalarAdminAsync(
                """
                SELECT count(*)
                FROM platform.location_outbox_events o
                JOIN drivers.driver_positions p ON p.id=o.driver_position_id
                WHERE p.driver_id=@driver AND p.client_event_id=@event
                """,
                Uuid("driver", scenario.DriverId),
                Uuid("event", eventId)));
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    private LocationServiceScope CreateScope(
        DateTimeOffset now,
        int maxPoolSize = 1,
        IDriverLocationFailureInjector? failureInjector = null)
    {
        var dataSource = fixture.CreateAppDataSource(
            maxPoolSize: maxPoolSize,
            applicationName: "Paqueteria.DRV003.Contract");
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<DriversDbContext>()
            .UseNpgsql(dataSource, postgres =>
            {
                postgres.UseNetTopologySuite();
                postgres.EnableRetryOnFailure();
            })
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new DriversDbContext(options, state);
        var service = new PostgreSqlDriverLocationIngestionService(
            new TenantTransactionContext<DriversDbContext>(context, state),
            new DriverLocationAuthorizer(),
            failureInjector ?? new NoOpDriverLocationFailureInjector(),
            new FixedClock(now),
            Options.Create(new DriversOptions
            {
                Provider = DriversProviderKind.PostgreSql,
                LocationTelemetry = new DriverLocationTelemetryOptions
                {
                    MinimumPublishIntervalSeconds = 10,
                    MinimumPublishDistanceMeters = 25,
                    MaximumSilenceSeconds = 60,
                },
            }),
            NullLogger<PostgreSqlDriverLocationIngestionService>.Instance);
        return new(dataSource, context, service);
    }

    private static PublishDriverLocationBatchCommand Command(
        Scenario scenario,
        params DriverLocationPointInput[] positions) =>
        new(scenario.UserId, scenario.OrganizationId, positions);

    private static DriverLocationPointInput Point(
        Guid clientEventId,
        DateTimeOffset capturedAt,
        double latitude = 24.8091,
        double longitude = -107.3940) =>
        new(clientEventId, latitude, longitude, 8.5, capturedAt, 180, 7.2);

    private async Task<Scenario> SeedAsync()
    {
        var scenario = new Scenario(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
        await ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
            VALUES (@org,'DRV Synthetic','DRV Synthetic','BUSINESS');
            INSERT INTO identity.users(id,identity_subject,status,created_at)
            VALUES (@user,@subject,'ACTIVE',@created);
            INSERT INTO organizations.organization_memberships
              (id,user_id,organization_id,role,status,is_default,granted_at)
            VALUES (@membership,@user,@org,'DRIVER','ACTIVE',true,@created);
            INSERT INTO locations.cities(id,country_code,state_code,name,timezone,status)
            VALUES (@city,'MX','CHH',@city_name,'America/Chihuahua','ACTIVE');
            INSERT INTO drivers.driver_profiles
              (id,user_id,org_id,home_city_id,driver_type,vehicle_type,status,created_at)
            VALUES (@driver,@user,@org,@city,'OWN','MOTORCYCLE','ACTIVE',@created);
            """,
            Uuid("org", scenario.OrganizationId),
            Uuid("user", scenario.UserId),
            Text("subject", $"drv-{scenario.UserId:N}"),
            Timestamp("created", CapturedAt.AddDays(-1)),
            Uuid("membership", scenario.MembershipId),
            Uuid("city", scenario.CityId),
            Text("city_name", $"DRV-{scenario.CityId:N}"),
            Uuid("driver", scenario.DriverId));
        return scenario;
    }

    private async Task CleanupAsync(Scenario scenario) => await ExecuteAdminAsync(
        """
        DELETE FROM platform.location_outbox_events WHERE owner_org_id=@org;
        DELETE FROM drivers.driver_positions WHERE driver_id=@driver;
        DELETE FROM drivers.driver_profiles WHERE id=@driver;
        DELETE FROM organizations.organization_memberships WHERE id=@membership;
        DELETE FROM identity.users WHERE id=@user;
        DELETE FROM locations.cities WHERE id=@city;
        DELETE FROM organizations.organizations WHERE id=@org;
        """,
        Uuid("org", scenario.OrganizationId),
        Uuid("driver", scenario.DriverId),
        Uuid("membership", scenario.MembershipId),
        Uuid("user", scenario.UserId),
        Uuid("city", scenario.CityId));

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long> CountVisiblePositionsAsync(
        Guid userId,
        Guid organizationId,
        Guid eventId)
    {
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            userId,
            [organizationId]);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM drivers.driver_positions WHERE client_event_id=@event",
            tenant.Connection,
            tenant.Transaction);
        command.Parameters.Add(Uuid("event", eventId));
        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
        await tenant.RollbackAsync();
        return count;
    }

    private static NpgsqlParameter<Guid> Uuid(string name, Guid value) =>
        new(name, NpgsqlDbType.Uuid) { TypedValue = value };

    private static NpgsqlParameter<string> Text(string name, string value) =>
        new(name, NpgsqlDbType.Text) { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(string name, DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz) { TypedValue = value };

    private sealed record Scenario(
        Guid OrganizationId,
        Guid UserId,
        Guid MembershipId,
        Guid CityId,
        Guid DriverId);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class ThrowingInjector(DriverLocationTransactionStage target)
        : IDriverLocationFailureInjector
    {
        public Task OnStageAsync(
            DriverLocationTransactionStage stage,
            CancellationToken cancellationToken) =>
            stage == target
                ? throw new InjectedFailureException()
                : Task.CompletedTask;
    }

    private sealed class InjectedFailureException : Exception;

    private sealed class TransientOnceInjector : IDriverLocationFailureInjector
    {
        private int invocationCount;

        public int InvocationCount => Volatile.Read(ref invocationCount);

        public Task OnStageAsync(
            DriverLocationTransactionStage stage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stage == DriverLocationTransactionStage.AuthorizationCompleted &&
                Interlocked.Increment(ref invocationCount) == 1)
            {
                throw new NpgsqlException(
                    "Synthetic transient failure without request data.",
                    new TimeoutException());
            }

            return Task.CompletedTask;
        }
    }

    private sealed record LocationServiceScope(
        NpgsqlDataSource DataSource,
        DriversDbContext Context,
        PostgreSqlDriverLocationIngestionService Service) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await DataSource.DisposeAsync();
        }
    }
}
