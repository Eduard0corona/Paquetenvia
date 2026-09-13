using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;
using Routing.Application.Routes;
using Routing.Infrastructure;
using Routing.Infrastructure.Persistence;
using Routing.Infrastructure.Routes;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class RoutingPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 18, 0, 0, TimeSpan.Zero);

    [PostgreSqlContractFact]
    public async Task Manual_routes_are_tenant_safe_atomic_and_serialized_under_real_concurrency()
    {
        await using var scenario = await RoutingScenario.CreateAsync(fixture);
        var service = CreateService(fixture.AppDataSource);
        var first = await service.CreateAsync(scenario.CreateRoute("route-a"), default);
        var second = await service.CreateAsync(scenario.CreateRoute("route-b"), default);

        Assert.Equal("DRAFT", first.Status);
        Assert.Equal(1, first.Version);
        Assert.Equal(first, await service.CreateAsync(scenario.CreateRoute("route-a"), default));
        var initialDetail = await service.GetAsync(
            new(scenario.DispatcherId, scenario.OrganizationId, first.Id), default);
        Assert.Equal(first.Id, initialDetail.Id);
        Assert.Empty(initialDetail.Stops);
        var page = await service.ListAsync(new(
            scenario.DispatcherId, scenario.OrganizationId, "DRAFT", scenario.DriverId,
            scenario.ScheduledFor, null), default);
        Assert.Contains(page.Items, route => route.Id == first.Id);
        Assert.Contains(page.Items, route => route.Id == second.Id);

        using var addGate = new ManualResetEventSlim(false);
        var addCommands = new[]
        {
            scenario.Add(first.Id, scenario.Order4500Id, 1, "race-a"),
            scenario.Add(second.Id, scenario.Order4500Id, 1, "race-b"),
        };
        var addTasks = addCommands.Select(command => Task.Run(async () =>
        {
            addGate.Wait();
            try
            {
                return (Result: await CreateService(fixture.AppDataSource).AddStopAsync(command, default),
                    Conflict: (RoutingConflictCode?)null);
            }
            catch (RoutingConflictException exception)
            {
                return (Result: (RouteDetailResult?)null, Conflict: (RoutingConflictCode?)exception.Code);
            }
        })).ToArray();
        addGate.Set();
        var addResults = await Task.WhenAll(addTasks);
        var winnerIndex = Array.FindIndex(addResults, result => result.Result is not null);
        Assert.True(winnerIndex >= 0);
        var raceWinner = addResults[winnerIndex].Result!;
        Assert.Equal(RoutingConflictCode.OrderAlreadyRouted,
            Assert.Single(addResults, result => result.Conflict is not null).Conflict);
        AssertRouteDetailEqual(
            raceWinner,
            await service.AddStopAsync(addCommands[winnerIndex], default));
        var winnerRouteId = raceWinner.Id;
        var winnerVersion = raceWinner.Version;

        var duplicate = await Assert.ThrowsAsync<RoutingConflictException>(() =>
            service.AddStopAsync(
                scenario.Add(winnerRouteId, scenario.Order4500Id, winnerVersion, "duplicate"), default));
        Assert.Equal(RoutingConflictCode.DuplicateOrder, duplicate.Code);

        var after5200 = await service.AddStopAsync(
            scenario.Add(winnerRouteId, scenario.Order5200Id, winnerVersion, "add-5200"), default);
        var staleAdd = await Assert.ThrowsAsync<RoutingConflictException>(() =>
            service.AddStopAsync(
                scenario.Add(winnerRouteId, scenario.OrderBigId, winnerVersion, "stale-add"), default));
        Assert.Equal(RoutingConflictCode.VersionConflict, staleAdd.Code);
        var afterBig = await service.AddStopAsync(
            scenario.Add(winnerRouteId, scenario.OrderBigId, after5200.Version, "add-big"), default);
        Assert.Equal(3, afterBig.StopCount);
        Assert.Equal(3_000_009_700L, afterBig.AssignmentCostCentsTotal);
        Assert.Equal([1, 2, 3], afterBig.Stops.Select(stop => stop.Sequence));

        using var reorderGate = new ManualResetEventSlim(false);
        var forward = afterBig.Stops.Select(stop => stop.Id).Reverse().ToArray();
        var alternate = new[] { forward[1], forward[2], forward[0] };
        var reorderTasks = new[] { forward, alternate }.Select((ids, index) => Task.Run(async () =>
        {
            reorderGate.Wait();
            try
            {
                return (Result: await CreateService(fixture.AppDataSource).ReorderStopsAsync(
                    scenario.Reorder(winnerRouteId, afterBig.Version, ids, $"reorder-{index}"), default),
                    Conflict: (RoutingConflictCode?)null);
            }
            catch (RoutingConflictException exception)
            {
                return (Result: (RouteDetailResult?)null, Conflict: (RoutingConflictCode?)exception.Code);
            }
        })).ToArray();
        reorderGate.Set();
        var reorderResults = await Task.WhenAll(reorderTasks);
        var reorderWinner = Assert.Single(reorderResults, result => result.Result is not null).Result!;
        Assert.Equal(RoutingConflictCode.VersionConflict,
            Assert.Single(reorderResults, result => result.Conflict is not null).Conflict);
        Assert.Equal([1, 2, 3], reorderWinner.Stops.Select(stop => stop.Sequence));
        var invalidSet = await Assert.ThrowsAsync<RoutingConflictException>(() =>
            service.ReorderStopsAsync(
                scenario.Reorder(winnerRouteId, reorderWinner.Version,
                    reorderWinner.Stops.Take(2).Select(stop => stop.Id).ToArray(), "subset"), default));
        Assert.Equal(RoutingConflictCode.InvalidStopSet, invalidSet.Code);

        var removed = reorderWinner.Stops[1];
        var afterRemove = await service.RemoveStopAsync(
            scenario.Remove(winnerRouteId, removed.Id, reorderWinner.Version, "remove"), default);
        Assert.Equal([1, 2], afterRemove.Stops.Select(stop => stop.Sequence));
        Assert.Equal(reorderWinner.Version + 1, afterRemove.Version);
        AssertRouteDetailEqual(afterRemove, await service.RemoveStopAsync(
            scenario.Remove(winnerRouteId, removed.Id, reorderWinner.Version, "remove"), default));

        await using (var evidence = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT count(*) FROM routes.route_stops WHERE route_id=@route),
              (SELECT count(*) FROM dispatch.assignments WHERE route_id=@route),
              (SELECT route_id IS NULL FROM dispatch.assignments WHERE order_id=@removed_order),
              (SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND entity_type='Route'),
              (SELECT count(*) FROM platform.outbox_events WHERE owner_org_id=@org
                 AND aggregate_id=@route AND topic='routes.route-changed'),
              security.resolve_outbox_consumer('routes.route-changed'),
              (SELECT bool_and(NOT consolidated_route AND financial_override IS NULL)
                 FROM orders.orders WHERE id IN (@order4500,@order5200)),
              (SELECT array_agg(total_cents ORDER BY total_cents)
                 FROM orders.orders WHERE id IN (@order4500,@order5200));
            """))
        {
            evidence.Parameters.AddWithValue("route", winnerRouteId);
            evidence.Parameters.AddWithValue("removed_order", removed.OrderId);
            evidence.Parameters.AddWithValue("org", scenario.OrganizationId);
            evidence.Parameters.AddWithValue("order4500", scenario.Order4500Id);
            evidence.Parameters.AddWithValue("order5200", scenario.Order5200Id);
            await using var reader = await evidence.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(2L, reader.GetInt64(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetInt64(3) >= 7);
            Assert.Equal((long)afterRemove.Version, reader.GetInt64(4));
            Assert.Equal("REALTIME", reader.GetString(5));
            Assert.True(reader.GetBoolean(6));
            Assert.Equal([4_500L, 5_200L], reader.GetFieldValue<long[]>(7));
        }

        var missing = await Assert.ThrowsAsync<RoutingNotFoundException>(() =>
            service.GetAsync(new(scenario.DispatcherId, scenario.OrganizationId, Guid.NewGuid()), default));
        await using var foreign = await RoutingScenario.CreateAsync(fixture);
        var hidden = await Assert.ThrowsAsync<RoutingNotFoundException>(() =>
            service.GetAsync(new(foreign.DispatcherId, foreign.OrganizationId, winnerRouteId), default));
        Assert.Equal(missing.Message, hidden.Message);

        var beforeRollback = await CountRouteEvidenceAsync(scenario.OrganizationId);
        var failing = CreateService(fixture.AppDataSource, new ThrowingInjector(RoutingTransactionStage.AssignmentLinked));
        await Assert.ThrowsAsync<SyntheticRoutingFailure>(() =>
            failing.AddStopAsync(
                scenario.Add(winnerRouteId, scenario.OrderRollbackId, afterRemove.Version, "rollback"), default));
        Assert.Equal(beforeRollback, await CountRouteEvidenceAsync(scenario.OrganizationId));
        var afterRollback = await service.GetAsync(
            new(scenario.DispatcherId, scenario.OrganizationId, winnerRouteId), default);
        AssertRouteDetailEqual(afterRemove, afterRollback);
        Assert.True(await scenario.AssignmentIsUnroutedAsync(scenario.OrderRollbackId));
    }

    [PostgreSqlContractFact]
    public async Task Routing_capability_and_OWN_eligibility_fail_closed_before_mutation()
    {
        await using var scenario = await RoutingScenario.CreateAsync(fixture);
        var service = CreateService(fixture.AppDataSource);
        await scenario.ExecuteAsync(
            "UPDATE organizations.organization_memberships SET role='DRIVER' WHERE user_id=@actor AND organization_id=@org",
            P("actor", scenario.DispatcherId), P("org", scenario.OrganizationId));
        await Assert.ThrowsAsync<RoutingForbiddenException>(() =>
            service.CreateAsync(scenario.CreateRoute("forbidden"), default));
        await scenario.ExecuteAsync(
            "UPDATE organizations.organization_memberships SET role='DISPATCHER' WHERE user_id=@actor AND organization_id=@org",
            P("actor", scenario.DispatcherId), P("org", scenario.OrganizationId));

        await scenario.ExecuteAsync("UPDATE drivers.driver_profiles SET driver_type='EXTERNAL' WHERE id=@driver",
            P("driver", scenario.DriverId));
        var ineligible = await Assert.ThrowsAsync<RoutingConflictException>(() =>
            service.CreateAsync(scenario.CreateRoute("external"), default));
        Assert.Equal(RoutingConflictCode.DriverIneligible, ineligible.Code);
        await scenario.ExecuteAsync("UPDATE drivers.driver_profiles SET driver_type='OWN',status='SUSPENDED' WHERE id=@driver",
            P("driver", scenario.DriverId));
        ineligible = await Assert.ThrowsAsync<RoutingConflictException>(() =>
            service.CreateAsync(scenario.CreateRoute("suspended"), default));
        Assert.Equal(RoutingConflictCode.DriverIneligible, ineligible.Code);

        await using var foreign = await RoutingScenario.CreateAsync(fixture);
        await Assert.ThrowsAsync<RoutingNotFoundException>(() =>
            service.CreateAsync(
                scenario.CreateRoute("foreign-driver") with { DriverId = foreign.DriverId }, default));
    }

    private PostgreSqlRouteService CreateService(NpgsqlDataSource dataSource, IRoutingFailureInjector? injector = null)
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<RoutingDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(new TenantTransactionGuardInterceptor(state), new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new RoutingDbContext(options, state);
        return new PostgreSqlRouteService(
            new TenantTransactionContext<RoutingDbContext>(context, state),
            Options.Create(new RoutingOptions { Provider = RoutingProviderKind.PostgreSql }),
            Options.Create(Eligibility()),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            injector ?? new NoOpRoutingFailureInjector(),
            new FixedClock(Now));
    }

    private static void AssertRouteDetailEqual(RouteDetailResult expected, RouteDetailResult actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.DriverId, actual.DriverId);
        Assert.Equal(expected.CityId, actual.CityId);
        Assert.Equal(expected.ServiceAreaId, actual.ServiceAreaId);
        Assert.Equal(expected.ScheduledFor, actual.ScheduledFor);
        Assert.Equal(expected.AssignmentCostCentsTotal, actual.AssignmentCostCentsTotal);
        Assert.Equal(expected.StopCount, actual.StopCount);
        Assert.Equal(expected.Stops.ToArray(), actual.Stops.ToArray());
    }

    private async Task<(long Routes, long Audits, long Outbox)> CountRouteEvidenceAsync(Guid organizationId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT count(*) FROM routes.routes WHERE operator_org_id=@org),
              (SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND entity_type='Route'),
              (SELECT count(*) FROM platform.outbox_events WHERE owner_org_id=@org AND topic='routes.route-changed');
            """);
        command.Parameters.AddWithValue("org", organizationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static RoutingDriverEligibilityOptions Eligibility() => new()
    {
        PolicyVersion = "dsp-001-contract-v1",
        RequiredDocumentTypesByVehicleType = new(StringComparer.Ordinal) { ["MOTORCYCLE"] = ["IDENTITY"] },
        VehicleCapacity = new(StringComparer.Ordinal)
        {
            ["MOTORCYCLE"] = new()
            {
                MaximumPackageCount = 8,
                MaximumTotalWeightGrams = 10_000,
                MaximumSinglePackageWeightGrams = 5_000,
                MaximumLengthMillimeters = 800,
                MaximumWidthMillimeters = 600,
                MaximumHeightMillimeters = 500,
                RequireDimensions = true,
            },
        },
    };

    private static NpgsqlParameter P(string name, object value) => new(name, value);
    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; } = now; }
    private sealed class ThrowingInjector(RoutingTransactionStage target) : IRoutingFailureInjector
    {
        public Task OnStageAsync(RoutingTransactionStage stage, CancellationToken cancellationToken) =>
            stage == target ? throw new SyntheticRoutingFailure(stage) : Task.CompletedTask;
    }
    private sealed class SyntheticRoutingFailure(RoutingTransactionStage stage) : Exception(stage.ToString());

    private sealed class RoutingScenario : IAsyncDisposable
    {
        private readonly SyntheticOrderScenario scenario;
        private readonly PostgreSqlContractFixture fixture;
        private readonly Guid driverUserId = Guid.NewGuid();
        private readonly Guid driverDocumentId = Guid.NewGuid();
        private readonly List<Guid> orderIds = [];

        private RoutingScenario(PostgreSqlContractFixture fixture)
        {
            this.fixture = fixture;
            scenario = new SyntheticOrderScenario(fixture);
        }

        public Guid OrganizationId => scenario.OrganizationId;
        public Guid DispatcherId => scenario.UserId;
        public Guid DriverId { get; } = Guid.NewGuid();
        public Guid Order4500Id => orderIds[0];
        public Guid Order5200Id => orderIds[1];
        public Guid OrderBigId => orderIds[2];
        public Guid OrderRollbackId => orderIds[3];
        public DateOnly ScheduledFor { get; } = new(2026, 8, 30);

        public static async Task<RoutingScenario> CreateAsync(PostgreSqlContractFixture fixture)
        {
            var value = new RoutingScenario(fixture);
            await value.scenario.InitializeAsync("READY_FOR_PICKUP", "USED");
            await value.ExecuteAsync(
                """
                INSERT INTO identity.users(id,identity_subject,status,created_at)
                VALUES (@driver_user,@subject,'ACTIVE',@now);
                INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default,granted_at)
                VALUES (gen_random_uuid(),@driver_user,@org,'DRIVER','ACTIVE',true,@now);
                INSERT INTO drivers.driver_profiles(id,user_id,org_id,home_city_id,driver_type,vehicle_type,status,created_at)
                VALUES (@driver,@driver_user,@org,@city,'OWN','MOTORCYCLE','ACTIVE',@now);
                INSERT INTO drivers.driver_documents(id,driver_id,org_id,document_type,object_key,sha256,expires_at,status,created_at)
                VALUES (@document,@driver,@org,'IDENTITY','synthetic/rte001',decode(repeat('ef',32),'hex'),@expires,'VALID',@now);
                """,
                P("driver_user", value.driverUserId), P("subject", $"rte001-{value.driverUserId:N}"),
                P("now", Now.AddDays(-1)), P("org", value.OrganizationId), P("driver", value.DriverId),
                P("city", value.scenario.CityId), P("document", value.driverDocumentId), P("expires", Now.AddDays(30)));
            await value.AddOrderAsync(4_500);
            await value.AddOrderAsync(5_200);
            await value.AddOrderAsync(3_000_000_000L);
            await value.AddOrderAsync(6_000);
            return value;
        }

        private async Task AddOrderAsync(long cost)
        {
            var quote = Guid.NewGuid();
            var order = Guid.NewGuid();
            orderIds.Add(order);
            await ExecuteAsync(
                """
                INSERT INTO pricing.quotes(
                  id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,
                  consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,
                  currency,pricing_policy_version,request_snapshot_redacted,package_snapshot,breakdown,input_hash,status,expires_at)
                VALUES (@quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,@cost,0,0,@cost,0,
                  'MXN','rte-001-price-snapshot-v1','{}','[{"weight_grams":500}]','{}',decode(repeat('01',32),'hex'),'USED',@expires);
                INSERT INTO orders.orders(
                  id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,
                  consolidated_route,payer_type,status,subtotal_cents,discount_cents,tax_cents,total_cents,
                  minimum_total_cents_snapshot,currency,pricing_policy_version,package_snapshot,financial_override,cod_expected_cents,version)
                VALUES (@order,@public_id,@quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,'SENDER',
                  'ASSIGNED',@cost,0,0,@cost,0,'MXN','rte-001-price-snapshot-v1','[{"weight_grams":500}]',NULL,0,1);
                INSERT INTO orders.package_items(id,order_id,owner_org_id,description,weight_grams,declared_value_cents,dimensions_mm)
                VALUES (gen_random_uuid(),@order,@org,'synthetic RTE package',500,0,'{"length_mm":100,"width_mm":80,"height_mm":60}');
                INSERT INTO dispatch.assignments(
                  id,order_id,owner_org_id,driver_id,route_id,assignment_type,status,cost_cents,accepted_at,created_at)
                VALUES (gen_random_uuid(),@order,@org,@driver,NULL,'OWN','ACCEPTED',@cost,@now,@now);
                """,
                P("quote", quote), P("order", order), P("public_id", $"RTE001-{Guid.NewGuid():N}"),
                P("org", OrganizationId), P("city", scenario.CityId), P("origin", scenario.OriginLocationId),
                P("destination", scenario.DestinationLocationId), P("cost", cost), P("expires", Now.AddDays(1)),
                P("driver", DriverId), P("now", Now));
        }

        public CreateRouteCommand CreateRoute(string key) => new(
            DispatcherId, OrganizationId, ValidIdempotencyKey(key), DriverId, scenario.CityId, null,
            ScheduledFor, false, key);
        public AddRouteStopCommand Add(Guid route, Guid order, int version, string key) => new(
            DispatcherId, OrganizationId, ValidIdempotencyKey(key), route, order, version, false, key);
        public RemoveRouteStopCommand Remove(Guid route, Guid stop, int version, string key) => new(
            DispatcherId, OrganizationId, ValidIdempotencyKey(key), route, stop, version, false, key);
        public ReorderRouteStopsCommand Reorder(Guid route, int version, IReadOnlyList<Guid> stops, string key) => new(
            DispatcherId, OrganizationId, ValidIdempotencyKey(key), route, version, stops, false, key);
        public Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters) => scenario.ExecuteAdminAsync(sql, parameters);

        private static string ValidIdempotencyKey(string logicalKey) =>
            $"rte001-contract-{logicalKey}";

        public async Task<bool> AssignmentIsUnroutedAsync(Guid orderId)
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                "SELECT route_id IS NULL FROM dispatch.assignments WHERE order_id=@order;");
            command.Parameters.AddWithValue("order", orderId);
            return await command.ExecuteScalarAsync() is true;
        }

        public async ValueTask DisposeAsync()
        {
            await ExecuteAsync(
                """
                DELETE FROM routes.route_stops WHERE operator_org_id=@org;
                UPDATE dispatch.assignments SET route_id=NULL WHERE owner_org_id=@org;
                DELETE FROM routes.routes WHERE operator_org_id=@org;
                DELETE FROM dispatch.assignments WHERE owner_org_id=@org;
                DELETE FROM drivers.driver_documents WHERE id=@document;
                DELETE FROM drivers.driver_profiles WHERE id=@driver;
                DELETE FROM organizations.organization_memberships WHERE user_id=@driver_user;
                DELETE FROM identity.users WHERE id=@driver_user;
                """,
                P("org", OrganizationId), P("document", driverDocumentId), P("driver", DriverId),
                P("driver_user", driverUserId));
            await scenario.DisposeAsync();
        }
    }
}
