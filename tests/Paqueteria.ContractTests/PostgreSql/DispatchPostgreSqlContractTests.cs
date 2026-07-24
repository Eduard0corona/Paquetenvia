using Dispatch.Application.Assignments;
using Dispatch.Infrastructure;
using Dispatch.Infrastructure.Assignments;
using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Stops;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Orders;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class DispatchPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 7, 23, 21, 0, 0, TimeSpan.Zero);

    [PostgreSqlContractFact]
    public async Task Coordinator_creates_one_accepted_assignment_transition_event_outbox_audits_and_replay()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var service = CreateAssignmentService(fixture.AppDataSource);
        var command = Command(scenario, costCents: (long)int.MaxValue + 100);

        var created = await service.CreateOwnDriverAssignmentAsync(command, default);
        var replay = await service.CreateOwnDriverAssignmentAsync(command, default);

        Assert.Equal(created, replay);
        Assert.Equal("ACCEPTED", created.Status);
        Assert.Equal("MXN", created.Cost.Currency);
        Assert.Equal((long)int.MaxValue + 100, created.Cost.AmountCents);

        await using var query = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT count(*) FROM dispatch.assignments WHERE order_id=@order),
              (SELECT status FROM dispatch.assignments WHERE order_id=@order),
              (SELECT accepted_at=created_at FROM dispatch.assignments WHERE order_id=@order),
              (SELECT status FROM orders.orders WHERE id=@order),
              (SELECT version FROM orders.orders WHERE id=@order),
              (SELECT count(*) FROM orders.order_events WHERE order_id=@order AND event_type='ORDER_STATUS_CHANGED'),
              (SELECT count(*) FROM platform.outbox_events WHERE aggregate_id=@order AND topic='orders.status-changed'),
              (SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND action='ASSIGNMENT_CREATED'),
              (SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND action='ORDER_STATUS_CHANGED'),
              (SELECT count(*) FROM platform.idempotency_keys
                WHERE owner_org_id=@org AND scope='DSP-002:ASSIGN_OWN_DRIVER' AND response_status=201);
            """);
        query.Parameters.AddWithValue("order", scenario.OrderId);
        query.Parameters.AddWithValue("org", scenario.OrganizationId);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("ACCEPTED", reader.GetString(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal("ASSIGNED", reader.GetString(3));
        Assert.Equal(2, reader.GetInt32(4));
        Assert.Equal(1L, reader.GetInt64(5));
        Assert.Equal(1L, reader.GetInt64(6));
        Assert.Equal(1L, reader.GetInt64(7));
        Assert.Equal(1L, reader.GetInt64(8));
        Assert.Equal(1L, reader.GetInt64(9));
    }

    [PostgreSqlContractFact]
    public async Task Same_key_concurrency_replays_and_different_keys_or_drivers_have_exactly_one_success()
    {
        await using (var sameKeyScenario = await DispatchScenario.CreateAsync(fixture))
        {
            var command = Command(sameKeyScenario);
            var results = await Task.WhenAll(
                CreateAssignmentService(fixture.CreateAppDataSource()).CreateOwnDriverAssignmentAsync(command, default),
                CreateAssignmentService(fixture.CreateAppDataSource()).CreateOwnDriverAssignmentAsync(command, default));
            Assert.Equal(results[0], results[1]);
            Assert.Equal(1L, await CountAssignmentsAsync(sameKeyScenario.OrderId));
        }

        await using (var differentKeyScenario = await DispatchScenario.CreateAsync(fixture))
        {
            var attempts = new[]
            {
                ExecuteOutcomeAsync(
                    CreateAssignmentService(fixture.CreateAppDataSource()),
                    Command(differentKeyScenario, idempotencyKey: Key())),
                ExecuteOutcomeAsync(
                    CreateAssignmentService(fixture.CreateAppDataSource()),
                    Command(differentKeyScenario, idempotencyKey: Key())),
            };
            var outcomes = await Task.WhenAll(attempts);
            Assert.Equal(1, outcomes.Count(value => value));
            Assert.Equal(1, outcomes.Count(value => !value));
            Assert.Equal(1L, await CountAssignmentsAsync(differentKeyScenario.OrderId));
        }
    }

    [PostgreSqlContractFact]
    public async Task Every_fault_injection_stage_rolls_back_all_effects()
    {
        foreach (var stage in Enum.GetValues<AssignmentTransactionStage>())
        {
            await using var scenario = await DispatchScenario.CreateAsync(fixture);
            var injector = new ThrowingInjector(stage);
            var service = CreateAssignmentService(fixture.AppDataSource, injector);

            await Assert.ThrowsAsync<SyntheticDispatchFailure>(() =>
                service.CreateOwnDriverAssignmentAsync(Command(scenario), default));

            await using var query = fixture.AdminDataSource.CreateCommand(
                """
                SELECT
                  (SELECT count(*) FROM dispatch.assignments WHERE order_id=@order),
                  (SELECT status FROM orders.orders WHERE id=@order),
                  (SELECT version FROM orders.orders WHERE id=@order),
                  (SELECT count(*) FROM orders.order_events WHERE order_id=@order),
                  (SELECT count(*) FROM platform.outbox_events WHERE aggregate_id=@order),
                  (SELECT count(*) FROM platform.audit_logs WHERE org_id=@org),
                  (SELECT count(*) FROM platform.idempotency_keys
                    WHERE owner_org_id=@org AND scope='DSP-002:ASSIGN_OWN_DRIVER');
                """);
            query.Parameters.AddWithValue("order", scenario.OrderId);
            query.Parameters.AddWithValue("org", scenario.OrganizationId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal("READY_FOR_PICKUP", reader.GetString(1));
            Assert.Equal(1, reader.GetInt32(2));
            Assert.Equal(0L, reader.GetInt64(3));
            Assert.Equal(0L, reader.GetInt64(4));
            Assert.Equal(0L, reader.GetInt64(5));
            Assert.Equal(0L, reader.GetInt64(6));
        }
    }

    [PostgreSqlContractFact]
    public async Task Stops_query_uses_direct_projection_custody_and_minimized_addresses()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var assignment = await CreateAssignmentService(fixture.AppDataSource)
            .CreateOwnDriverAssignmentAsync(Command(scenario), default);
        var query = CreateStopsQuery(fixture.AppDataSource);

        var pickup = Assert.Single(await query.ListCurrentDriverStopsAsync(
            scenario.DriverUserId,
            scenario.OrganizationId,
            default));
        Assert.Equal("PICKUP", pickup.StopType);
        Assert.Equal("READY origin summary", pickup.AddressSummary);

        await scenario.ExecuteAdminAsync(
            "UPDATE orders.orders SET status='IN_TRANSIT' WHERE id=@order",
            P("order", scenario.OrderId));
        var delivery = Assert.Single(await query.ListCurrentDriverStopsAsync(
            scenario.DriverUserId,
            scenario.OrganizationId,
            default));
        Assert.Equal("DELIVERY", delivery.StopType);
        Assert.Equal("READY destination summary", delivery.AddressSummary);

        await scenario.ExecuteAdminAsync(
            """
            UPDATE orders.orders SET status='FAILED_ATTEMPT' WHERE id=@order;
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,aggregate_version,event_type,payload,occurred_at)
            VALUES (
              @event,@order,@org,3,'ORDER_STATUS_CHANGED',
              '{"previous_status":"IN_TRANSIT","new_status":"DELIVERING"}',@occurred);
            """,
            P("event", Guid.NewGuid()),
            P("order", scenario.OrderId),
            P("org", scenario.OrganizationId),
            P("occurred", OccurredAt.AddMinutes(1)));
        var custodyDelivery = Assert.Single(await query.ListCurrentDriverStopsAsync(
            scenario.DriverUserId,
            scenario.OrganizationId,
            default));
        Assert.Equal("DELIVERY", custodyDelivery.StopType);
        Assert.Equal("FAILED_ATTEMPT", custodyDelivery.Status);

        await scenario.ExecuteAdminAsync(
            "UPDATE orders.orders SET status='RETURNING' WHERE id=@order",
            P("order", scenario.OrderId));
        var returning = Assert.Single(await query.ListCurrentDriverStopsAsync(
            scenario.DriverUserId,
            scenario.OrganizationId,
            default));
        Assert.Equal("RETURN", returning.StopType);
        Assert.Equal("READY origin summary", returning.AddressSummary);
        Assert.DoesNotContain(assignment.Id.ToString("D"), returning.AddressSummary, StringComparison.Ordinal);
    }

    [PostgreSqlContractFact]
    public async Task Dispatch_adoption_RLS_history_and_partial_index_are_canonical()
    {
        await using (var connection = new NpgsqlConnection(fixture.DeploymentConnectionString))
        {
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DispatchDbContext>()
                .UseNpgsql(connection, postgres =>
                {
                    postgres.MigrationsAssembly(typeof(DispatchDbContext).Assembly.FullName);
                    postgres.MigrationsHistoryTable("__ef_migrations_history_dispatch", "platform");
                }).Options;
            await using var context = new DispatchDbContext(options, new TenantDatabaseExecutionState());
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }

        await using var security = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT h."MigrationId" FROM platform.__ef_migrations_history_dispatch h),
              (SELECT pg_get_userbyid(c.relowner)
               FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
               WHERE n.nspname='platform' AND c.relname='__ef_migrations_history_dispatch'),
              c.relrowsecurity,c.relforcerowsecurity,
              (SELECT NOT rolbypassrls FROM pg_roles WHERE rolname='paqueteria_app'),
              (SELECT pg_get_expr(i.indpred,i.indrelid)
               FROM pg_index i WHERE i.indexrelid='dispatch.one_active_assignment_per_order'::regclass),
              (SELECT count(*) FROM pg_policies
               WHERE schemaname='dispatch' AND tablename='assignments'
                 AND policyname='assignments_tenant')
            FROM pg_class c
            WHERE c.oid='dispatch.assignments'::regclass;
            """);
        await using var reader = await security.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("20260723_AdoptCanonicalDispatchAssignmentsBaseline", reader.GetString(0));
        Assert.Equal("paqueteria_migrator", reader.GetString(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        var predicate = reader.GetString(5);
        Assert.Contains("ACCEPTED", predicate, StringComparison.Ordinal);
        Assert.Contains("ACTIVE", predicate, StringComparison.Ordinal);
        Assert.Equal(1L, reader.GetInt64(6));

        await using var empty = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            Guid.NewGuid(),
            []);
        await using var emptyQuery = new NpgsqlCommand(
            "SELECT count(*) FROM dispatch.assignments",
            empty.Connection,
            empty.Transaction);
        Assert.Equal(0L, await emptyQuery.ExecuteScalarAsync());
        await empty.RollbackAsync();
    }

    private PostgreSqlAssignmentToOrderCoordinator CreateAssignmentService(
        NpgsqlDataSource dataSource,
        IAssignmentFailureInjector? injector = null)
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new DispatchDbContext(dbOptions, state);
        return new PostgreSqlAssignmentToOrderCoordinator(
            new TenantTransactionContext<DispatchDbContext>(context, state),
            Options.Create(new DispatchOptions
            {
                Provider = DispatchProviderKind.PostgreSql,
                AssignmentPolicyVersion = "dsp-002-contract-v1",
            }),
            Options.Create(EligibilityOptions()),
            new DispatchAssignmentAuthorizer(),
            new PostgreSqlDispatchAuthorizationReader(),
            new PostgreSqlDispatchDriverEligibilityReader(),
            new PostgreSqlAssignmentReplayEvidenceReader(),
            new OrderTransitionGuardRegistry(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            injector ?? new NoOpAssignmentFailureInjector(),
            new FixedClock(OccurredAt),
            NullLogger<PostgreSqlAssignmentToOrderCoordinator>.Instance);
    }

    private static PostgreSqlDriverStopsQuery CreateStopsQuery(NpgsqlDataSource dataSource)
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new DispatchDbContext(dbOptions, state);
        return new PostgreSqlDriverStopsQuery(
            new TenantTransactionContext<DispatchDbContext>(context, state),
            NullLogger<PostgreSqlDriverStopsQuery>.Instance);
    }

    private static DispatchDriverEligibilityOptions EligibilityOptions() => new()
    {
        PolicyVersion = "dsp-001-contract-v1",
        RequiredDocumentTypesByVehicleType = new(StringComparer.Ordinal)
        {
            ["MOTORCYCLE"] = ["IDENTITY"],
        },
        VehicleCapacity = new(StringComparer.Ordinal)
        {
            ["MOTORCYCLE"] = new DispatchVehicleCapacityOptions
            {
                MaximumPackageCount = 4,
                MaximumTotalWeightGrams = 4_000,
                MaximumSinglePackageWeightGrams = 2_000,
                MaximumLengthMillimeters = 500,
                MaximumWidthMillimeters = 400,
                MaximumHeightMillimeters = 300,
                RequireDimensions = true,
            },
        },
    };

    private static CreateOwnDriverAssignmentCommand Command(
        DispatchScenario scenario,
        string? idempotencyKey = null,
        long costCents = 0) => new(
        scenario.DispatcherUserId,
        scenario.OrganizationId,
        idempotencyKey ?? Key(),
        scenario.OrderId,
        scenario.DriverId,
        "OWN",
        costCents,
        null,
        false,
        "dsp-002-contract-request");

    private async Task<long> CountAssignmentsAsync(Guid orderId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT count(*) FROM dispatch.assignments WHERE order_id=@order");
        command.Parameters.AddWithValue("order", orderId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> ExecuteOutcomeAsync(
        IAssignmentService service,
        CreateOwnDriverAssignmentCommand command)
    {
        try
        {
            await service.CreateOwnDriverAssignmentAsync(command, default);
            return true;
        }
        catch (AssignmentConflictException)
        {
            return false;
        }
    }

    private static string Key() => $"dsp-002-contract-{Guid.NewGuid():N}";

    private static NpgsqlParameter P(string name, object value) => new(name, value);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class ThrowingInjector(AssignmentTransactionStage target) : IAssignmentFailureInjector
    {
        public Task OnStageAsync(AssignmentTransactionStage stage, CancellationToken cancellationToken) =>
            stage == target
                ? throw new SyntheticDispatchFailure(stage)
                : Task.CompletedTask;
    }

    private sealed class SyntheticDispatchFailure(AssignmentTransactionStage stage)
        : Exception($"Synthetic failure at {stage}.");

    private sealed class DispatchScenario : IAsyncDisposable
    {
        private readonly SyntheticOrderScenario order;

        private DispatchScenario(PostgreSqlContractFixture fixture)
        {
            order = new SyntheticOrderScenario(fixture);
        }

        public Guid OrganizationId => order.OrganizationId;
        public Guid DispatcherUserId => order.UserId;
        public Guid OrderId => order.OrderId;
        public Guid DriverId { get; } = Guid.NewGuid();
        public Guid DriverUserId { get; } = Guid.NewGuid();
        private Guid DriverMembershipId { get; } = Guid.NewGuid();
        private Guid DriverDocumentId { get; } = Guid.NewGuid();
        public Guid OriginLocationId => order.OriginLocationId;
        public Guid DestinationLocationId => order.DestinationLocationId;

        public static async Task<DispatchScenario> CreateAsync(PostgreSqlContractFixture fixture)
        {
            var scenario = new DispatchScenario(fixture);
            await scenario.order.InitializeAsync("READY_FOR_PICKUP", "USED");
            await scenario.ExecuteAdminAsync(
                """
                UPDATE locations.locations SET address_summary='READY origin summary' WHERE id=@origin;
                UPDATE locations.locations SET address_summary='READY destination summary' WHERE id=@destination;
                INSERT INTO orders.package_items(
                  id,order_id,owner_org_id,description,weight_grams,declared_value_cents,dimensions_mm)
                VALUES (
                  @package,@order,@org,'synthetic package',500,0,
                  '{"length_mm":100,"width_mm":80,"height_mm":60}');
                INSERT INTO identity.users(id,identity_subject,status,created_at)
                VALUES (@driver_user,@subject,'ACTIVE',@created);
                INSERT INTO organizations.organization_memberships(
                  id,user_id,organization_id,role,status,is_default,granted_at)
                VALUES (@membership,@driver_user,@org,'DRIVER','ACTIVE',true,@created);
                INSERT INTO drivers.driver_profiles(
                  id,user_id,org_id,home_city_id,driver_type,vehicle_type,status,created_at)
                VALUES (@driver,@driver_user,@org,@city,'OWN','MOTORCYCLE','ACTIVE',@created);
                INSERT INTO drivers.driver_documents(
                  id,driver_id,org_id,document_type,object_key,sha256,expires_at,status,created_at)
                VALUES (
                  @document,@driver,@org,'IDENTITY','synthetic/dsp002',
                  decode(repeat('ab',32),'hex'),@expires,'VALID',@created);
                """,
                P("origin", scenario.OriginLocationId),
                P("destination", scenario.DestinationLocationId),
                P("package", Guid.NewGuid()),
                P("order", scenario.OrderId),
                P("org", scenario.OrganizationId),
                P("driver_user", scenario.DriverUserId),
                P("subject", $"dsp002-driver-{scenario.DriverUserId:N}"),
                P("created", OccurredAt.AddDays(-1)),
                P("membership", scenario.DriverMembershipId),
                P("driver", scenario.DriverId),
                P("city", scenario.order.CityId),
                P("document", scenario.DriverDocumentId),
                P("expires", OccurredAt.AddDays(30)));
            return scenario;
        }

        public Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters) =>
            order.ExecuteAdminAsync(sql, parameters);

        public async ValueTask DisposeAsync()
        {
            await ExecuteAdminAsync(
                """
                DELETE FROM dispatch.assignments WHERE driver_id=@driver;
                DELETE FROM drivers.driver_documents WHERE id=@document;
                DELETE FROM drivers.driver_profiles WHERE id=@driver;
                DELETE FROM organizations.organization_memberships WHERE id=@membership;
                DELETE FROM identity.users WHERE id=@driver_user;
                """,
                P("document", DriverDocumentId),
                P("driver", DriverId),
                P("membership", DriverMembershipId),
                P("driver_user", DriverUserId));
            await order.DisposeAsync();
        }
    }
}
