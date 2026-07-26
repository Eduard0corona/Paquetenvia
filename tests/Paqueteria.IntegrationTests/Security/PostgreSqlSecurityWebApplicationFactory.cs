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
    public const string ValidPublicOrderId = "ORD_abcdefghijklmnopqrstuv";
    public const string ExpiredTrackingToken = "expired-token-sec002-000000000000";
    public const string RevokedTrackingToken = "revoked-token-sec002-000000000000";
    public static readonly Guid ViewerOrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OperationsOrganizationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ActiveDriverId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd11");
    public static readonly Guid SecondaryDriverId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd12");

    private readonly string _adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _workerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private PostgreSqlContainer? _container;
    private string _adminConnectionString = string.Empty;
    private string _applicationConnectionString = string.Empty;
    private string _workerConnectionString = string.Empty;

    public string PostgreSqlVersion { get; private set; } = string.Empty;
    public string PostGisVersion { get; private set; } = string.Empty;
    internal string AdminConnectionString => _adminConnectionString;
    public string ApplicationConnectionString => _applicationConnectionString;
    public string WorkerConnectionString => _workerConnectionString;

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
            CREATE ROLE paqueteria_sec002_worker LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{{_workerPassword}}';
            GRANT paqueteria_worker TO paqueteria_sec002_worker;
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
        builder.Username = "paqueteria_sec002_worker";
        builder.Password = _workerPassword;
        builder.ApplicationName = "Paqueteria.RTM002.IntegrationTests";
        _workerConnectionString = builder.ConnectionString;
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

    internal async Task RevokeValidTrackingTokenAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE orders.public_tracking_tokens
            SET revoked_at=clock_timestamp()
            WHERE token_hash=extensions.digest(pg_catalog.convert_to(@token,'UTF8'),'sha256')
            """,
            connection);
        command.Parameters.AddWithValue("token", ValidTrackingToken);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    internal async Task RestoreValidTrackingTokenAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE orders.public_tracking_tokens
            SET revoked_at=NULL
            WHERE token_hash=extensions.digest(pg_catalog.convert_to(@token,'UTF8'),'sha256')
            """,
            connection);
        command.Parameters.AddWithValue("token", ValidTrackingToken);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    internal async Task<(
        Guid OutboxId,
        int AggregateVersion,
        Guid OrderEventId,
        DateTimeOffset OccurredAt)>
        EnqueueRealtimeStatusAsync(
        bool isPublic = true,
        bool available = true,
        int payloadAdditionalTicks = 0)
    {
        var eventId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        var previousStatus = isPublic ? "IN_TRANSIT" : "DELIVERING";
        var newStatus = isPublic ? "DELIVERING" : "CLOSED";
        var publicEventCode = isPublic ? "OUT_FOR_DELIVERY" : null;
        var occurredAt = new DateTimeOffset(
            2026,
            7,
            25,
            3,
            0,
            0,
            TimeSpan.Zero).AddTicks(1_234_560);
        var payloadOccurredAt = occurredAt.AddTicks(payloadAdditionalTicks);
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var versionCommand = new NpgsqlCommand(
            """
            SELECT COALESCE(max(aggregate_version),3)::integer + 1
            FROM orders.order_events
            WHERE order_id='66666666-6666-6666-6666-666666666666'
            """,
            connection);
        var aggregateVersion = Convert.ToInt32(
            await versionCommand.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
        await using var command = new NpgsqlCommand(
            """
            UPDATE orders.orders
            SET status=@new_status,version=@aggregate_version
            WHERE id='66666666-6666-6666-6666-666666666666';
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,aggregate_version,event_type,
              public_event_code,payload,occurred_at)
            VALUES (
              @event_id,
              '66666666-6666-6666-6666-666666666666',
              '11111111-1111-1111-1111-111111111111',
              @aggregate_version,
              'ORDER_STATUS_CHANGED',
              @public_event_code,
              jsonb_build_object(
                'previous_status',@previous_status,
                'new_status',@new_status),
              @occurred_at);
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,
              aggregate_version,payload,priority,status,attempts,available_at,created_at)
            VALUES (
              @outbox_id,
              '11111111-1111-1111-1111-111111111111',
              '{"organization_ids":["11111111-1111-1111-1111-111111111111"]}',
              'orders.status-changed',
              'Order',
              '66666666-6666-6666-6666-666666666666',
              @aggregate_version,
              jsonb_build_object(
                'schema_version','order-status-changed-v1',
                'order_event_id',@event_id,
                'order_id','66666666-6666-6666-6666-666666666666',
                'public_order_id','ORD_abcdefghijklmnopqrstuv',
                'previous_status',@previous_status,
                'new_status',@new_status,
                'occurred_at',@payload_occurred_at,
                'public_event_code',@public_event_code,
                'authorized_driver_id',NULL,
                'assignment_id',NULL),
              50,'PENDING',0,
              CASE WHEN @available THEN clock_timestamp()
                   ELSE clock_timestamp()+interval '1 hour' END,
              clock_timestamp());
            """,
            connection);
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("aggregate_version", aggregateVersion);
        command.Parameters.AddWithValue("previous_status", previousStatus);
        command.Parameters.AddWithValue("new_status", newStatus);
        command.Parameters.AddWithValue("available", available);
        command.Parameters.AddWithValue("occurred_at", occurredAt);
        command.Parameters.AddWithValue(
            "payload_occurred_at",
            payloadOccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.Add(new NpgsqlParameter<string?>("public_event_code", publicEventCode));
        await command.ExecuteNonQueryAsync();
        return (outboxId, aggregateVersion, eventId, occurredAt);
    }

    internal async Task MakeBusinessOutboxAvailableAsync(Guid outboxId)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE platform.outbox_events
            SET available_at=clock_timestamp()
            WHERE id=@id AND status='PENDING'
            """,
            connection);
        command.Parameters.AddWithValue("id", outboxId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    internal async Task<Guid> EnqueueDelayedStatusFromEvidenceAsync(Guid orderEventId)
    {
        var outboxId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,
              aggregate_version,payload,priority,status,attempts,available_at,created_at)
            SELECT
              @outbox_id,e.owner_org_id,
              jsonb_build_object(
                'organization_ids',jsonb_build_array(e.owner_org_id::text)),
              'orders.status-changed','Order',e.order_id,e.aggregate_version,
              jsonb_build_object(
                'schema_version','order-status-changed-v1',
                'order_event_id',e.id,
                'order_id',e.order_id,
                'public_order_id',o.public_id,
                'previous_status',e.payload->>'previous_status',
                'new_status',e.payload->>'new_status',
                'occurred_at',e.occurred_at,
                'public_event_code',e.public_event_code,
                'authorized_driver_id',NULL,
                'assignment_id',NULL),
              50,'PENDING',0,clock_timestamp(),clock_timestamp()
            FROM orders.order_events e
            JOIN orders.orders o
              ON o.id=e.order_id AND o.owner_org_id=e.owner_org_id
            WHERE e.id=@event_id
              AND e.event_type='ORDER_STATUS_CHANGED'
            """,
            connection);
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("event_id", orderEventId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        return outboxId;
    }

    internal async Task<(Guid OutboxId, DateTimeOffset OccurredAt)>
        EnqueueHistoricalTimelineAsync(
        Guid orderEventId,
        int payloadAdditionalTicks)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        Guid orderId;
        Guid ownerOrganizationId;
        int aggregateVersion;
        DateTimeOffset occurredAt;
        string newStatus;
        await using (var evidence = new NpgsqlCommand(
                         """
                         SELECT order_id,owner_org_id,aggregate_version,occurred_at,
                                payload->>'new_status'
                         FROM orders.order_events
                         WHERE id=@event_id
                           AND event_type='ORDER_STATUS_CHANGED'
                         """,
                         connection))
        {
            evidence.Parameters.AddWithValue("event_id", orderEventId);
            await using var reader = await evidence.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            orderId = reader.GetGuid(0);
            ownerOrganizationId = reader.GetGuid(1);
            aggregateVersion = reader.GetInt32(2);
            occurredAt = reader.GetFieldValue<DateTimeOffset>(3);
            newStatus = reader.GetString(4);
        }

        var outboxId = Guid.NewGuid();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,
              aggregate_version,payload,priority,status,attempts,available_at,created_at)
            VALUES (
              @outbox_id,@owner_org_id,
              jsonb_build_object(
                'organization_ids',jsonb_build_array(@owner_org_id::text)),
              'orders.timeline-event-added','Order',@order_id,@aggregate_version,
              jsonb_build_object(
                'schema_version','order-timeline-event-added-v1',
                'order_id',@order_id,
                'timeline_event_id',@event_id,
                'category','ORDER_STATUS',
                'summary','Order status changed to ' || @new_status || '.',
                'occurred_at',@payload_occurred_at),
              50,'PENDING',0,clock_timestamp(),clock_timestamp())
            """,
            connection);
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("owner_org_id", ownerOrganizationId);
        command.Parameters.AddWithValue("order_id", orderId);
        command.Parameters.AddWithValue("aggregate_version", aggregateVersion);
        command.Parameters.AddWithValue("event_id", orderEventId);
        command.Parameters.AddWithValue("new_status", newStatus);
        command.Parameters.AddWithValue(
            "payload_occurred_at",
            occurredAt.AddTicks(payloadAdditionalTicks).ToString(
                "O",
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        return (outboxId, occurredAt);
    }

    internal async Task<string?> ReadOutboxStatusAsync(Guid outboxId)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM platform.outbox_events WHERE id=@id",
            connection);
        command.Parameters.AddWithValue("id", outboxId);
        return await command.ExecuteScalarAsync() as string;
    }

    internal async Task<(string? Status, string? LastError)> ReadOutboxResultAsync(Guid outboxId)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status,last_error FROM platform.outbox_events WHERE id=@id",
            connection);
        command.Parameters.AddWithValue("id", outboxId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))
            : (null, null);
    }

    internal async Task<OutboxDeliveryState?> ReadOutboxDeliveryStateAsync(Guid outboxId)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT status,attempts,lease_token,lease_expires_at,last_error
            FROM platform.outbox_events
            WHERE id=@id
            """,
            connection);
        command.Parameters.AddWithValue("id", outboxId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4))
            : null;
    }

    internal async Task<Guid> EnqueueRealtimeLocationAsync() =>
        (await EnqueueRealtimeLocationEvidenceAsync()).OutboxId;

    internal async Task<LocationOutboxScenario> EnqueueRealtimeLocationEvidenceAsync(
        int payloadAdditionalTicks = 0)
    {
        var positionId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        var capturedAt = new DateTimeOffset(
            2026,
            7,
            25,
            3,
            1,
            0,
            TimeSpan.Zero).AddTicks(1_234_560);
        var payloadCapturedAt = capturedAt.AddTicks(payloadAdditionalTicks);
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO drivers.driver_positions(
              id,driver_id,org_id,city_id,client_event_id,point,accuracy_m,
              captured_at,received_at,publish_realtime)
            VALUES (
              @position_id,
              'dddddddd-dddd-dddd-dddd-dddddddddd10',
              '11111111-1111-1111-1111-111111111111',
              '33333333-3333-3333-3333-333333333333',
              @client_event_id,
              public.ST_SetSRID(public.ST_MakePoint(-107.40,24.80),4326),
              5.25,
              @captured_at,
              clock_timestamp(),
              true);
            INSERT INTO platform.location_outbox_events(
              id,owner_org_id,driver_position_id,topic,payload,status,attempts,
              available_at,created_at)
            VALUES (
              @outbox_id,
              '11111111-1111-1111-1111-111111111111',
              @position_id,
              'drivers.location-updated',
              jsonb_build_object(
                'schema_version','driver-location-updated-v1',
                'driver_position_id',@position_id,
                'driver_id','dddddddd-dddd-dddd-dddd-dddddddddd10',
                'lat',24.80,
                'lng',-107.40,
                'accuracy_m',5.25,
                'captured_at',@payload_captured_at),
              'PENDING',0,clock_timestamp(),clock_timestamp());
            """,
            connection);
        command.Parameters.AddWithValue("position_id", positionId);
        command.Parameters.AddWithValue("client_event_id", Guid.NewGuid());
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("captured_at", capturedAt);
        command.Parameters.AddWithValue(
            "payload_captured_at",
            payloadCapturedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
        return new(
            outboxId,
            positionId,
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd10"),
            capturedAt,
            24.80,
            -107.40,
            5.25);
    }

    internal async Task<string?> ReadLocationOutboxStatusAsync(Guid outboxId)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM platform.location_outbox_events WHERE id=@id",
            connection);
        command.Parameters.AddWithValue("id", outboxId);
        return await command.ExecuteScalarAsync() as string;
    }

    internal async Task<(string? Status, string? LastError)> ReadLocationOutboxResultAsync(
        Guid outboxId)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status,last_error FROM platform.location_outbox_events WHERE id=@id",
            connection);
        command.Parameters.AddWithValue("id", outboxId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))
            : (null, null);
    }

    internal async Task<LocationOutboxScenario> ReadHttpLocationScenarioAsync(
        Guid clientEventId)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT o.id,p.id,p.driver_id,p.captured_at,
                   public.ST_Y(p.point),public.ST_X(p.point),p.accuracy_m
            FROM drivers.driver_positions p
            JOIN platform.location_outbox_events o ON o.driver_position_id=p.id
            WHERE p.client_event_id=@client_event_id
              AND p.org_id='11111111-1111-1111-1111-111111111111'
            """,
            connection);
        command.Parameters.AddWithValue("client_event_id", clientEventId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var scenario = new LocationOutboxScenario(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetDouble(4),
            reader.GetDouble(5),
            Convert.ToDouble(
                reader.GetDecimal(6),
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(await reader.ReadAsync());
        return scenario;
    }

    internal async Task<AssignmentEvidenceScenario> CreateAssignmentEvidenceScenarioAsync(
        bool includeDriverMembership = true,
        bool includeViewerMembership = false,
        bool driverMembershipFirst = true,
        string driverMembershipStatus = "ACTIVE",
        string assignmentType = "OWN",
        string assignmentStatus = "ACTIVE",
        string profileStatus = "ACTIVE",
        string userStatus = "ACTIVE")
    {
        var userId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var assignmentId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var versionCommand = new NpgsqlCommand(
            """
            SELECT COALESCE(max(aggregate_version),0)::integer + 1
            FROM orders.order_events
            WHERE order_id='66666666-6666-6666-6666-666666666666'
            """,
            connection,
            transaction);
        var aggregateVersion = Convert.ToInt32(
            await versionCommand.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO identity.users(id,identity_subject,status)
            VALUES (@user_id,@subject,@user_status);
            INSERT INTO drivers.driver_profiles(
              id,user_id,org_id,driver_type,vehicle_type,status,home_city_id)
            VALUES (
              @driver_id,@user_id,
              '11111111-1111-1111-1111-111111111111',
              'OWN','MOTORCYCLE',@profile_status,
              '33333333-3333-3333-3333-333333333333');
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,operator_org_id,driver_id,route_id,
              assignment_type,status,cost_cents,accepted_at,created_at)
            VALUES (
              @assignment_id,
              '66666666-6666-6666-6666-666666666666',
              '11111111-1111-1111-1111-111111111111',
              NULL,@driver_id,NULL,@assignment_type,@assignment_status,0,
              CASE WHEN @assignment_status IN ('ACCEPTED','ACTIVE')
                THEN clock_timestamp() ELSE NULL END,
              clock_timestamp());
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,aggregate_version,event_type,
              public_event_code,payload,occurred_at)
            VALUES (
              @event_id,
              '66666666-6666-6666-6666-666666666666',
              '11111111-1111-1111-1111-111111111111',
              @aggregate_version,'ORDER_STATUS_CHANGED',NULL,
              jsonb_build_object(
                'previous_status','READY_FOR_PICKUP',
                'new_status','ASSIGNED',
                'assignment_id',@assignment_id::text),
              @occurred_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("subject", $"rtm002-evidence-{userId:D}");
        command.Parameters.AddWithValue("user_status", userStatus);
        command.Parameters.AddWithValue("driver_id", driverId);
        command.Parameters.AddWithValue("profile_status", profileStatus);
        command.Parameters.AddWithValue("assignment_id", assignmentId);
        command.Parameters.AddWithValue("assignment_type", assignmentType);
        command.Parameters.AddWithValue("assignment_status", assignmentStatus);
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("aggregate_version", aggregateVersion);
        var occurredAt = new DateTimeOffset(
            2026,
            7,
            25,
            6,
            aggregateVersion % 60,
            0,
            TimeSpan.Zero);
        command.Parameters.AddWithValue("occurred_at", occurredAt);
        await command.ExecuteNonQueryAsync();

        var memberships = new List<(string Role, string Status)>();
        if (includeDriverMembership)
        {
            memberships.Add(("DRIVER", driverMembershipStatus));
        }

        if (includeViewerMembership)
        {
            var viewer = ("VIEWER", "ACTIVE");
            if (driverMembershipFirst)
            {
                memberships.Add(viewer);
            }
            else
            {
                memberships.Insert(0, viewer);
            }
        }

        foreach (var membership in memberships)
        {
            await using var membershipCommand = new NpgsqlCommand(
                """
                INSERT INTO organizations.organization_memberships(
                  id,user_id,organization_id,role,status,is_default)
                VALUES (
                  gen_random_uuid(),@user_id,
                  '11111111-1111-1111-1111-111111111111',
                  @role,@status,false)
                """,
                connection,
                transaction);
            membershipCommand.Parameters.AddWithValue("user_id", userId);
            membershipCommand.Parameters.AddWithValue("role", membership.Role);
            membershipCommand.Parameters.AddWithValue("status", membership.Status);
            await membershipCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return new(
            assignmentId,
            driverId,
            aggregateVersion,
            occurredAt);
    }

    internal async Task RetireAssignmentEvidenceScenarioAsync(Guid assignmentId)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE dispatch.assignments
            SET status='CANCELLED',accepted_at=NULL
            WHERE id=@assignment_id
              AND status IN ('ACCEPTED','ACTIVE')
            """,
            connection);
        command.Parameters.AddWithValue("assignment_id", assignmentId);
        await command.ExecuteNonQueryAsync();
    }

    internal async Task<DriverAudienceOutboxScenario> CreateDriverAudienceOutboxScenarioAsync(
        bool available = false,
        int payloadAdditionalTicks = 0)
    {
        var assignmentId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var statusOutboxId = Guid.NewGuid();
        var assignmentOutboxId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var aggregateVersion = await NextOrderVersionAsync(connection, transaction);
        var occurredAt = RealtimeOccurredAt(aggregateVersion);
        var payloadOccurredAt = occurredAt.AddTicks(payloadAdditionalTicks);
        await using var command = new NpgsqlCommand(
            """
            UPDATE dispatch.assignments
            SET status='CANCELLED',accepted_at=NULL
            WHERE order_id='66666666-6666-6666-6666-666666666666'
              AND status IN ('ACCEPTED','ACTIVE');
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,operator_org_id,driver_id,route_id,
              assignment_type,status,cost_cents,accepted_at,created_at)
            VALUES (
              @assignment_id,
              '66666666-6666-6666-6666-666666666666',
              '11111111-1111-1111-1111-111111111111',
              NULL,@driver_id,NULL,'OWN','ACCEPTED',0,clock_timestamp(),clock_timestamp());
            UPDATE orders.orders
            SET status='ASSIGNED',version=@aggregate_version
            WHERE id='66666666-6666-6666-6666-666666666666';
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,aggregate_version,event_type,
              public_event_code,payload,occurred_at)
            VALUES (
              @event_id,
              '66666666-6666-6666-6666-666666666666',
              '11111111-1111-1111-1111-111111111111',
              @aggregate_version,'ORDER_STATUS_CHANGED',NULL,
              jsonb_build_object(
                'previous_status','READY_FOR_PICKUP',
                'new_status','ASSIGNED',
                'assignment_id',@assignment_id::text),
              @occurred_at);
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,
              aggregate_version,payload,priority,status,attempts,available_at,created_at)
            VALUES
              (
                @status_outbox_id,
                '11111111-1111-1111-1111-111111111111',
                '{"organization_ids":["11111111-1111-1111-1111-111111111111"]}',
                'orders.status-changed','Order',
                '66666666-6666-6666-6666-666666666666',@aggregate_version,
                jsonb_build_object(
                  'schema_version','order-status-changed-v1',
                  'order_event_id',@event_id,
                  'order_id','66666666-6666-6666-6666-666666666666',
                  'public_order_id','ORD_abcdefghijklmnopqrstuv',
                  'previous_status','READY_FOR_PICKUP',
                  'new_status','ASSIGNED',
                  'occurred_at',@payload_occurred_at,
                  'public_event_code',NULL,
                  'authorized_driver_id',@driver_id,
                  'assignment_id',@assignment_id),
                50,'PENDING',0,
                CASE WHEN @available THEN clock_timestamp()
                     ELSE clock_timestamp()+interval '1 hour' END,
                clock_timestamp()),
              (
                @assignment_outbox_id,
                '11111111-1111-1111-1111-111111111111',
                '{"organization_ids":["11111111-1111-1111-1111-111111111111"]}',
                'dispatch.assignment-changed','Order',
                '66666666-6666-6666-6666-666666666666',@aggregate_version,
                jsonb_build_object(
                  'schema_version','assignment-changed-v1',
                  'order_id','66666666-6666-6666-6666-666666666666',
                  'assignment_id',@assignment_id,
                  'driver_id',@driver_id,
                  'assignment_status','ACCEPTED',
                  'occurred_at',@payload_occurred_at),
                50,'PENDING',0,
                CASE WHEN @available THEN clock_timestamp()
                     ELSE clock_timestamp()+interval '1 hour' END,
                clock_timestamp());
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("assignment_id", assignmentId);
        command.Parameters.AddWithValue("driver_id", ActiveDriverId);
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("status_outbox_id", statusOutboxId);
        command.Parameters.AddWithValue("assignment_outbox_id", assignmentOutboxId);
        command.Parameters.AddWithValue("aggregate_version", aggregateVersion);
        command.Parameters.AddWithValue("occurred_at", occurredAt);
        command.Parameters.AddWithValue(
            "payload_occurred_at",
            payloadOccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("available", available);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return new(
            assignmentId,
            ActiveDriverId,
            eventId,
            statusOutboxId,
            assignmentOutboxId,
            aggregateVersion,
            occurredAt);
    }

    internal async Task<(Guid OutboxId, int AggregateVersion)> EnqueueDriverStatusAsync(
        Guid assignmentId,
        bool available = false)
    {
        var eventId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var aggregateVersion = await NextOrderVersionAsync(connection, transaction);
        var occurredAt = RealtimeOccurredAt(aggregateVersion);
        await using var command = new NpgsqlCommand(
            """
            UPDATE orders.orders
            SET status='ASSIGNED',version=@aggregate_version
            WHERE id='66666666-6666-6666-6666-666666666666';
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,aggregate_version,event_type,
              public_event_code,payload,occurred_at)
            VALUES (
              @event_id,
              '66666666-6666-6666-6666-666666666666',
              '11111111-1111-1111-1111-111111111111',
              @aggregate_version,'ORDER_STATUS_CHANGED',NULL,
              jsonb_build_object(
                'previous_status','READY_FOR_PICKUP',
                'new_status','ASSIGNED'),
              @occurred_at);
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,
              aggregate_version,payload,priority,status,attempts,available_at,created_at)
            VALUES (
              @outbox_id,
              '11111111-1111-1111-1111-111111111111',
              '{"organization_ids":["11111111-1111-1111-1111-111111111111"]}',
              'orders.status-changed','Order',
              '66666666-6666-6666-6666-666666666666',@aggregate_version,
              jsonb_build_object(
                'schema_version','order-status-changed-v1',
                'order_event_id',@event_id,
                'order_id','66666666-6666-6666-6666-666666666666',
                'public_order_id','ORD_abcdefghijklmnopqrstuv',
                'previous_status','READY_FOR_PICKUP',
                'new_status','ASSIGNED',
                'occurred_at',@occurred_at,
                'public_event_code',NULL,
                'authorized_driver_id',@driver_id,
                'assignment_id',@assignment_id),
              50,'PENDING',0,
              CASE WHEN @available THEN clock_timestamp()
                   ELSE clock_timestamp()+interval '1 hour' END,
              clock_timestamp());
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("assignment_id", assignmentId);
        command.Parameters.AddWithValue("driver_id", ActiveDriverId);
        command.Parameters.AddWithValue("aggregate_version", aggregateVersion);
        command.Parameters.AddWithValue("occurred_at", occurredAt);
        command.Parameters.AddWithValue("available", available);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return (outboxId, aggregateVersion);
    }

    internal async Task<DriverAssignmentOutboxScenario> CreateDriverAssignmentOutboxScenarioAsync(
        bool available = false)
    {
        var assignmentId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var aggregateVersion = await NextOrderVersionAsync(connection, transaction);
        var occurredAt = RealtimeOccurredAt(aggregateVersion);
        await using var command = new NpgsqlCommand(
            """
            UPDATE dispatch.assignments
            SET status='CANCELLED',accepted_at=NULL
            WHERE order_id='66666666-6666-6666-6666-666666666666'
              AND status IN ('ACCEPTED','ACTIVE');
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,operator_org_id,driver_id,route_id,
              assignment_type,status,cost_cents,accepted_at,created_at)
            VALUES (
              @assignment_id,
              '66666666-6666-6666-6666-666666666666',
              '11111111-1111-1111-1111-111111111111',
              NULL,@driver_id,NULL,'OWN','ACCEPTED',0,clock_timestamp(),clock_timestamp());
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,aggregate_version,event_type,
              public_event_code,payload,occurred_at)
            VALUES (
              @event_id,
              '66666666-6666-6666-6666-666666666666',
              '11111111-1111-1111-1111-111111111111',
              @aggregate_version,'ORDER_STATUS_CHANGED',NULL,
              jsonb_build_object(
                'previous_status','READY_FOR_PICKUP',
                'new_status','ASSIGNED',
                'assignment_id',@assignment_id::text),
              @occurred_at);
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,
              aggregate_version,payload,priority,status,attempts,available_at,created_at)
            VALUES (
              @outbox_id,
              '11111111-1111-1111-1111-111111111111',
              '{"organization_ids":["11111111-1111-1111-1111-111111111111"]}',
              'dispatch.assignment-changed','Order',
              '66666666-6666-6666-6666-666666666666',@aggregate_version,
              jsonb_build_object(
                'schema_version','assignment-changed-v1',
                'order_id','66666666-6666-6666-6666-666666666666',
                'assignment_id',@assignment_id,
                'driver_id',@driver_id,
                'assignment_status','ACCEPTED',
                'occurred_at',@occurred_at),
              50,'PENDING',0,
              CASE WHEN @available THEN clock_timestamp()
                   ELSE clock_timestamp()+interval '1 hour' END,
              clock_timestamp());
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("assignment_id", assignmentId);
        command.Parameters.AddWithValue("driver_id", ActiveDriverId);
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("outbox_id", outboxId);
        command.Parameters.AddWithValue("aggregate_version", aggregateVersion);
        command.Parameters.AddWithValue("occurred_at", occurredAt);
        command.Parameters.AddWithValue("available", available);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return new(assignmentId, ActiveDriverId, outboxId, aggregateVersion, occurredAt);
    }

    internal async Task SetActiveDriverMembershipStatusAsync(string status)
    {
        if (status is not ("ACTIVE" or "SUSPENDED"))
        {
            throw new ArgumentException("Only ACTIVE or SUSPENDED is supported.", nameof(status));
        }

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE organizations.organization_memberships
            SET status=@status
            WHERE user_id='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11'
              AND organization_id='11111111-1111-1111-1111-111111111111'
              AND role='DRIVER'
            """,
            connection);
        command.Parameters.AddWithValue("status", status);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    internal async Task SetAssignmentStatusAsync(Guid assignmentId, string status)
    {
        if (status is not ("ACCEPTED" or "ACTIVE" or "CANCELLED"))
        {
            throw new ArgumentException("The assignment status is not supported.", nameof(status));
        }

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE dispatch.assignments
            SET status=@status,
                accepted_at=CASE WHEN @status IN ('ACCEPTED','ACTIVE')
                  THEN COALESCE(accepted_at,clock_timestamp()) ELSE NULL END
            WHERE id=@assignment_id
            """,
            connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("assignment_id", assignmentId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    internal Task<Guid> EnqueueBusinessPoisonAsync() =>
        EnqueuePoisonAsync(location: false);

    internal Task<Guid> EnqueueLocationPoisonAsync() =>
        EnqueuePoisonAsync(location: true);

    private async Task<Guid> EnqueuePoisonAsync(bool location)
    {
        var outboxId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            location
                ? """
                  INSERT INTO platform.location_outbox_events(
                    id,owner_org_id,driver_position_id,topic,payload,status,attempts,
                    available_at,created_at)
                  VALUES (
                    @outbox_id,
                    '11111111-1111-1111-1111-111111111111',
                    gen_random_uuid(),
                    'drivers.location-updated',
                    '{"schema_version":"invalid"}',
                    'PENDING',0,clock_timestamp(),clock_timestamp())
                  """
                : """
                  INSERT INTO platform.outbox_events(
                    id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,
                    aggregate_version,payload,priority,status,attempts,available_at,created_at)
                  VALUES (
                    @outbox_id,
                    '11111111-1111-1111-1111-111111111111',
                    '{"organization_ids":["11111111-1111-1111-1111-111111111111"]}',
                    'orders.status-changed','Order',
                    '66666666-6666-6666-6666-666666666666',1,
                    '{"schema_version":"invalid"}',
                    50,'PENDING',0,clock_timestamp(),clock_timestamp())
                  """,
            connection);
        command.Parameters.AddWithValue("outbox_id", outboxId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        return outboxId;
    }

    private static async Task<int> NextOrderVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT COALESCE(max(aggregate_version),0)::integer + 1
            FROM orders.order_events
            WHERE order_id='66666666-6666-6666-6666-666666666666'
            """,
            connection,
            transaction);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset RealtimeOccurredAt(int aggregateVersion) =>
        new DateTimeOffset(
            2026,
            7,
            25,
            8,
            aggregateVersion % 60,
            0,
            TimeSpan.Zero).AddTicks(1_234_560);

    internal async Task SetOutboxFunctionExecuteAsync(string signature, bool granted)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "security.claim_outbox(text,integer,interval)",
            "security.settle_outbox(uuid,uuid,text,text,timestamptz)",
            "security.requeue_stale_outbox(interval,integer,integer)",
            "security.claim_location_outbox(text,integer,interval)",
            "security.settle_location_outbox(uuid,uuid,text,text,timestamptz)",
            "security.requeue_stale_location_outbox(interval,integer,integer)",
        };
        if (!allowed.Contains(signature))
        {
            throw new ArgumentException("The function signature is not allow-listed.", nameof(signature));
        }

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            granted
                ? $"GRANT EXECUTE ON FUNCTION {signature} TO paqueteria_worker"
                : $"REVOKE EXECUTE ON FUNCTION {signature} FROM paqueteria_worker",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    internal async Task SetWorkerBypassRlsAsync(bool enabled)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"ALTER ROLE paqueteria_worker {(enabled ? "BYPASSRLS" : "NOBYPASSRLS")}",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    internal async Task SetWorkerRoleMembershipAsync(bool granted)
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            granted
                ? "GRANT paqueteria_worker TO paqueteria_sec002_worker"
                : "REVOKE paqueteria_worker FROM paqueteria_sec002_worker",
            connection);
        await command.ExecuteNonQueryAsync();
        NpgsqlConnection.ClearAllPools();
    }

    internal async Task<string> ReadOutboxStateSnapshotAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT jsonb_build_object(
              'business',COALESCE((
                SELECT jsonb_object_agg(status,row_count)
                FROM (
                  SELECT status,count(*) AS row_count
                  FROM platform.outbox_events
                  GROUP BY status
                  ORDER BY status
                ) business_states
              ),'{}'::jsonb),
              'location',COALESCE((
                SELECT jsonb_object_agg(status,row_count)
                FROM (
                  SELECT status,count(*) AS row_count
                  FROM platform.location_outbox_events
                  GROUP BY status
                  ORDER BY status
                ) location_states
              ),'{}'::jsonb)
            )::text
            """,
            connection);
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The outbox snapshot was not returned."));
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
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa9','mock-subject-no-memberships','ACTIVE'),
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa10','mock-subject-rtm002-driver','ACTIVE'),
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11','mock-subject-active-driver','ACTIVE'),
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa12','mock-subject-secondary-driver','ACTIVE');
            INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default) VALUES
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1','11111111-1111-1111-1111-111111111111','VIEWER','ACTIVE',true),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2','11111111-1111-1111-1111-111111111111','PLATFORM_ADMIN','ACTIVE',true),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3','11111111-1111-1111-1111-111111111111','PLATFORM_ADMIN','ACTIVE',true),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4','11111111-1111-1111-1111-111111111111','VIEWER','ACTIVE',true),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4','22222222-2222-2222-2222-222222222222','DISPATCHER','ACTIVE',false),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa7','11111111-1111-1111-1111-111111111111','VIEWER','SUSPENDED',false),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa8','11111111-1111-1111-1111-111111111111','VIEWER','REVOKED',false),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa10','11111111-1111-1111-1111-111111111111','DRIVER','ACTIVE',false),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11','11111111-1111-1111-1111-111111111111','DRIVER','ACTIVE',false),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11','11111111-1111-1111-1111-111111111111','VIEWER','ACTIVE',false),
              (gen_random_uuid(),'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa12','11111111-1111-1111-1111-111111111111','DRIVER','ACTIVE',false);

            INSERT INTO locations.cities(id,state_code,name,timezone)
              VALUES ('33333333-3333-3333-3333-333333333333','SI','Synthetic City','America/Mazatlan');
            INSERT INTO drivers.driver_profiles(
              id,user_id,org_id,driver_type,vehicle_type,status,home_city_id)
              VALUES (
                'dddddddd-dddd-dddd-dddd-dddddddddd10',
                'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa10',
                '11111111-1111-1111-1111-111111111111',
                'OWN','MOTORCYCLE','ACTIVE',
                '33333333-3333-3333-3333-333333333333'),
                (
                'dddddddd-dddd-dddd-dddd-dddddddddd11',
                'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11',
                '11111111-1111-1111-1111-111111111111',
                'OWN','MOTORCYCLE','ACTIVE',
                '33333333-3333-3333-3333-333333333333'),
                (
                'dddddddd-dddd-dddd-dddd-dddddddddd12',
                'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa12',
                '11111111-1111-1111-1111-111111111111',
                'OWN','MOTORCYCLE','ACTIVE',
                '33333333-3333-3333-3333-333333333333');
            INSERT INTO locations.locations(id,owner_org_id,city_id,point,address_ciphertext,address_summary,pii_key_version) VALUES
              ('44444444-4444-4444-4444-444444444441','11111111-1111-1111-1111-111111111111','33333333-3333-3333-3333-333333333333',public.ST_SetSRID(public.ST_MakePoint(-107.40,24.80),4326),decode('00','hex'),'Synthetic origin','test-v1'),
              ('44444444-4444-4444-4444-444444444442','11111111-1111-1111-1111-111111111111','33333333-3333-3333-3333-333333333333',public.ST_SetSRID(public.ST_MakePoint(-107.39,24.81),4326),decode('01','hex'),'Synthetic destination','test-v1');
            INSERT INTO pricing.quotes(id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,request_snapshot_redacted,package_snapshot,breakdown,input_hash,status,expires_at)
              VALUES ('55555555-5555-5555-5555-555555555555','11111111-1111-1111-1111-111111111111','33333333-3333-3333-3333-333333333333','44444444-4444-4444-4444-444444444441','44444444-4444-4444-4444-444444444442','SAME_DAY','OCCASIONAL',false,10000,0,0,10000,10000,'MXN','sec002-v1','{}','[]','{}',decode(repeat('00',32),'hex'),'ACTIVE',clock_timestamp()+interval '1 day');
            INSERT INTO orders.orders(id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,consolidated_route,payer_type,status,subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,package_snapshot,cod_expected_cents,version)
              VALUES ('66666666-6666-6666-6666-666666666666','ORD_abcdefghijklmnopqrstuv','55555555-5555-5555-5555-555555555555','11111111-1111-1111-1111-111111111111','33333333-3333-3333-3333-333333333333','44444444-4444-4444-4444-444444444441','44444444-4444-4444-4444-444444444442','SAME_DAY','OCCASIONAL',false,'SENDER','DELIVERING',10000,0,0,10000,10000,'MXN','sec002-v1','[]',0,1);
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

    internal sealed record AssignmentEvidenceScenario(
        Guid AssignmentId,
        Guid DriverId,
        int AggregateVersion,
        DateTimeOffset OccurredAt);

    internal sealed record DriverAudienceOutboxScenario(
        Guid AssignmentId,
        Guid DriverId,
        Guid OrderEventId,
        Guid StatusOutboxId,
        Guid AssignmentOutboxId,
        int AggregateVersion,
        DateTimeOffset OccurredAt);

    internal sealed record DriverAssignmentOutboxScenario(
        Guid AssignmentId,
        Guid DriverId,
        Guid OutboxId,
        int AggregateVersion,
        DateTimeOffset OccurredAt);

    internal sealed record LocationOutboxScenario(
        Guid OutboxId,
        Guid DriverPositionId,
        Guid DriverId,
        DateTimeOffset CapturedAt,
        double Lat,
        double Lng,
        double AccuracyM);

    internal sealed record OutboxDeliveryState(
        string Status,
        int Attempts,
        Guid? LeaseToken,
        DateTimeOffset? LeaseExpiresAt,
        string? LastError);
}
