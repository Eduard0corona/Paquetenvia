using Dispatch.Application.Assignments;
using Dispatch.Application.Fleet;
using Dispatch.Infrastructure.Assignments;
using Dispatch.Infrastructure.Fleet;
using Dispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// UI-PHASE3-DRIVERS-2026-10-11 (listOrganizationDrivers) on real PostgreSQL as the NOBYPASSRLS runtime role: the
/// selected organization's OWN drivers that are not INACTIVE, never another organization's, an EXTERNAL or a retired
/// profile; the stops listMyStops shows each driver and the capture time of its latest position; capability first;
/// cursor pages. The read never writes.
/// </summary>
public sealed partial class DispatchPostgreSqlContractTests
{
    [PostgreSqlContractFact]
    public async Task Organization_drivers_list_the_own_fleet_with_current_stops_and_last_position_time()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var seeded = new SeededDrivers();
        var otherOrganizationId = Guid.NewGuid();
        var positioned = new List<Guid>();
        try
        {
            var suspended = await seeded.AddAsync(scenario, scenario.OrganizationId, "OWN", "SUSPENDED", "VAN",
                OccurredAt.AddDays(30));
            var pending = await seeded.AddAsync(scenario, scenario.OrganizationId, "OWN", "PENDING", "CAR",
                OccurredAt.AddDays(30));
            var retired = await seeded.AddAsync(scenario, scenario.OrganizationId, "OWN", "INACTIVE", "BICYCLE",
                OccurredAt.AddDays(30));
            var external = await seeded.AddAsync(scenario, scenario.OrganizationId, "EXTERNAL", "ACTIVE",
                "MOTORCYCLE", OccurredAt.AddDays(30));
            await scenario.ExecuteAdminAsync(
                """
                INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
                VALUES (@other,'Fleet other','Fleet other','ALLY');
                INSERT INTO organizations.organization_memberships(
                  id,user_id,organization_id,role,status,is_default,granted_at)
                VALUES (gen_random_uuid(),@dispatcher,@other,'DISPATCHER','ACTIVE',false,now());
                """,
                P("other", otherOrganizationId),
                P("dispatcher", scenario.DispatcherUserId));
            var foreign = await seeded.AddAsync(scenario, otherOrganizationId, "OWN", "ACTIVE", "MOTORCYCLE",
                OccurredAt.AddDays(30));

            // Two stops of the scenario driver; an ACCEPTED or ACTIVE assignment whose order is not (or no longer) a
            // stop and a finished or cancelled assignment are not stops. The suspended driver keeps one stop.
            await AddAssignedOrderAsync(scenario, "DELIVERING", "ACTIVE", scenario.DriverId);
            await AddAssignedOrderAsync(scenario, "ASSIGNED", "ACCEPTED", scenario.DriverId);
            await AddAssignedOrderAsync(scenario, "DELIVERED", "ACTIVE", scenario.DriverId);
            await AddAssignedOrderAsync(scenario, "READY_FOR_PICKUP", "ACCEPTED", scenario.DriverId);
            await AddAssignedOrderAsync(scenario, "IN_TRANSIT", "COMPLETED", scenario.DriverId);
            await AddAssignedOrderAsync(scenario, "RESCHEDULED", "CANCELLED", scenario.DriverId);
            await AddAssignedOrderAsync(scenario, "AT_PICKUP", "ACCEPTED", suspended);

            // The latest capture wins even when an older capture arrived later; the retired and the foreign drivers'
            // positions are never read for this organization.
            positioned.AddRange([scenario.DriverId, retired, foreign]);
            var latest = OccurredAt.AddMinutes(-5);
            await AddPositionAsync(scenario, scenario.DriverId, scenario.OrganizationId, latest, OccurredAt.AddMinutes(-4));
            await AddPositionAsync(
                scenario, scenario.DriverId, scenario.OrganizationId, OccurredAt.AddMinutes(-30), OccurredAt);
            await AddPositionAsync(scenario, retired, scenario.OrganizationId, OccurredAt, OccurredAt);
            await AddPositionAsync(scenario, foreign, otherOrganizationId, OccurredAt.AddMinutes(-1), OccurredAt);

            var page = await CreateOrganizationDriversQuery().ListAsync(RosterQuery(scenario), default);

            Assert.Null(page.NextCursor);
            Assert.Equal(
                PostgreSqlUuidOrder([scenario.DriverId, suspended, pending]),
                page.Items.Select(item => item.DriverId));
            Assert.DoesNotContain(page.Items, item =>
                item.DriverId == retired || item.DriverId == external || item.DriverId == foreign);

            var own = page.Items.Single(item => item.DriverId == scenario.DriverId);
            Assert.Equal(
                (Paqueteria.Application.Privacy.DriverReference.From(scenario.DriverId), "MOTORCYCLE", "ACTIVE", 2),
                (own.DriverReference, own.VehicleType, own.Status, own.ActiveStopCount));
            Assert.Equal(latest, own.LastPositionAt);
            var suspendedItem = page.Items.Single(item => item.DriverId == suspended);
            Assert.Equal(("VAN", "SUSPENDED", 1), (suspendedItem.VehicleType, suspendedItem.Status, suspendedItem.ActiveStopCount));
            Assert.Null(suspendedItem.LastPositionAt);
            var pendingItem = page.Items.Single(item => item.DriverId == pending);
            Assert.Equal(("CAR", "PENDING", 0), (pendingItem.VehicleType, pendingItem.Status, pendingItem.ActiveStopCount));
            Assert.Null(pendingItem.LastPositionAt);

            // The other organization's dispatcher lists only its own driver and its own position, never this one's.
            var other = await CreateOrganizationDriversQuery()
                .ListAsync(RosterQuery(scenario) with { OrganizationId = otherOrganizationId }, default);
            var foreignItem = Assert.Single(other.Items);
            Assert.Equal((foreign, 0), (foreignItem.DriverId, foreignItem.ActiveStopCount));
            Assert.Equal(OccurredAt.AddMinutes(-1), foreignItem.LastPositionAt);
        }
        finally
        {
            await scenario.ExecuteAdminAsync(
                "DELETE FROM drivers.driver_positions WHERE driver_id=ANY(@drivers);",
                P("drivers", positioned.ToArray()));
            await seeded.DisposeAsync(scenario);
            await scenario.ExecuteAdminAsync(
                """
                DELETE FROM organizations.organization_memberships WHERE organization_id=@other;
                DELETE FROM organizations.organizations WHERE id=@other;
                """,
                P("other", otherOrganizationId));
        }
    }

    [PostgreSqlContractFact]
    public async Task Organization_drivers_count_the_stops_of_orders_the_organization_operates()
    {
        // As the operator of another owner's order, the organization's driver holds that stop; the owner lists its
        // own drivers and never the operator's.
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var seeded = new SeededDrivers();
        var operatorId = Guid.NewGuid();
        try
        {
            await scenario.ExecuteAdminAsync(
                """
                INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
                VALUES (@operator,'Fleet operator','Fleet operator','ALLY');
                INSERT INTO organizations.organization_memberships(
                  id,user_id,organization_id,role,status,is_default,granted_at)
                VALUES (gen_random_uuid(),@dispatcher,@operator,'DISPATCHER','ACTIVE',false,now());
                UPDATE orders.orders SET operator_org_id=@operator,status='ASSIGNED' WHERE id=@order;
                """,
                P("operator", operatorId),
                P("dispatcher", scenario.DispatcherUserId),
                P("order", scenario.OrderId));
            var operatorDriver = await seeded.AddAsync(scenario, operatorId, "OWN", "ACTIVE", "MOTORCYCLE",
                OccurredAt.AddDays(30));
            await scenario.ExecuteAdminAsync(
                """
                INSERT INTO dispatch.assignments(
                  id,order_id,owner_org_id,operator_org_id,driver_id,route_id,assignment_type,status,cost_cents,
                  accepted_at,created_at)
                VALUES (gen_random_uuid(),@order,@owner,@operator,@driver,NULL,'OWN','ACCEPTED',0,@at,@at);
                """,
                P("order", scenario.OrderId),
                P("owner", scenario.OrganizationId),
                P("operator", operatorId),
                P("driver", operatorDriver),
                P("at", OccurredAt));

            var asOperator = await CreateOrganizationDriversQuery()
                .ListAsync(RosterQuery(scenario) with { OrganizationId = operatorId }, default);
            var item = Assert.Single(asOperator.Items);
            Assert.Equal((operatorDriver, 1), (item.DriverId, item.ActiveStopCount));

            var asOwner = await CreateOrganizationDriversQuery().ListAsync(RosterQuery(scenario), default);
            var owned = Assert.Single(asOwner.Items);
            Assert.Equal((scenario.DriverId, 0), (owned.DriverId, owned.ActiveStopCount));
        }
        finally
        {
            await scenario.ExecuteAdminAsync(
                """
                DELETE FROM dispatch.assignments WHERE order_id=@order;
                UPDATE orders.orders SET operator_org_id=NULL WHERE id=@order;
                """,
                P("order", scenario.OrderId));
            await seeded.DisposeAsync(scenario);
            await scenario.ExecuteAdminAsync(
                """
                DELETE FROM organizations.organization_memberships WHERE organization_id=@operator;
                DELETE FROM organizations.organizations WHERE id=@operator;
                """,
                P("operator", operatorId));
        }
    }

    [PostgreSqlContractFact]
    public async Task Organization_drivers_refuse_every_role_but_the_picker_roles_before_reading()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        foreach (var (role, status, mfa) in new[]
                 {
                     ("VIEWER", "ACTIVE", true), ("DRIVER", "ACTIVE", true), ("FINANCE", "ACTIVE", true),
                     ("BUSINESS_ADMIN", "ACTIVE", true), ("DISPATCHER", "SUSPENDED", false),
                     ("PLATFORM_ADMIN", "ACTIVE", false),
                 })
        {
            await scenario.SetDispatcherMembershipAsync(role, status);
            await Assert.ThrowsAsync<AssignmentForbiddenException>(() => CreateOrganizationDriversQuery()
                .ListAsync(RosterQuery(scenario) with { MfaSatisfied = mfa }, default));
        }

        await scenario.SetDispatcherMembershipAsync("PLATFORM_ADMIN", "ACTIVE");
        Assert.Equal(
            scenario.DriverId,
            Assert.Single((await CreateOrganizationDriversQuery()
                .ListAsync(RosterQuery(scenario) with { MfaSatisfied = true }, default)).Items).DriverId);
        await scenario.SetDispatcherMembershipAsync("DISPATCHER", "ACTIVE");
        Assert.Single((await CreateOrganizationDriversQuery().ListAsync(RosterQuery(scenario), default)).Items);
    }

    [PostgreSqlContractFact]
    public async Task Organization_drivers_page_by_cursor_without_overlap()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var prefix = $"fleet-page-{Guid.NewGuid():N}-";
        try
        {
            await scenario.ExecuteAdminAsync(
                """
                WITH created AS (
                  SELECT gen_random_uuid() AS user_id,gen_random_uuid() AS driver_id,n
                  FROM generate_series(1,@count) n
                ), users AS (
                  INSERT INTO identity.users(id,identity_subject,status,created_at)
                  SELECT user_id,@prefix||n,'ACTIVE',@created FROM created
                ), memberships AS (
                  INSERT INTO organizations.organization_memberships(
                    id,user_id,organization_id,role,status,is_default,granted_at)
                  SELECT gen_random_uuid(),user_id,@org,'DRIVER','ACTIVE',true,@created FROM created
                )
                INSERT INTO drivers.driver_profiles(
                  id,user_id,org_id,home_city_id,driver_type,vehicle_type,status,created_at)
                SELECT driver_id,user_id,@org,@city,'OWN','MOTORCYCLE','PENDING',@created FROM created;
                """,
                P("count", OrganizationDriverPolicy.PageSize),
                P("prefix", prefix),
                P("created", OccurredAt.AddDays(-1)),
                P("org", scenario.OrganizationId),
                P("city", scenario.CityId));

            var first = await CreateOrganizationDriversQuery().ListAsync(RosterQuery(scenario), default);
            Assert.Equal(OrganizationDriverPolicy.PageSize, first.Items.Count);
            Assert.NotNull(first.NextCursor);
            Assert.True(OrganizationDriverCursorCodec.TryDecode(first.NextCursor, out var after));
            Assert.Equal(first.Items[^1].DriverId, after);
            var second = await CreateOrganizationDriversQuery()
                .ListAsync(RosterQuery(scenario) with { AfterDriverId = after }, default);
            Assert.Single(second.Items);
            Assert.Null(second.NextCursor);

            var all = first.Items.Concat(second.Items).Select(value => value.DriverId).ToArray();
            Assert.Equal(OrganizationDriverPolicy.PageSize + 1, all.Distinct().Count());
            Assert.Equal(PostgreSqlUuidOrder(all), all);
            Assert.Contains(scenario.DriverId, all);
        }
        finally
        {
            await scenario.ExecuteAdminAsync(
                """
                DELETE FROM drivers.driver_profiles p USING identity.users u
                WHERE p.user_id=u.id AND u.identity_subject LIKE @pattern;
                DELETE FROM organizations.organization_memberships m USING identity.users u
                WHERE m.user_id=u.id AND u.identity_subject LIKE @pattern;
                DELETE FROM identity.users WHERE identity_subject LIKE @pattern;
                """,
                P("pattern", prefix + "%"));
        }
    }

    private static ListOrganizationDriversQuery RosterQuery(DispatchScenario scenario) => new(
        scenario.DispatcherUserId,
        scenario.OrganizationId,
        false,
        null);

    private PostgreSqlOrganizationDriversQuery CreateOrganizationDriversQuery()
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new DispatchDbContext(dbOptions, state);
        return new PostgreSqlOrganizationDriversQuery(
            new TenantTransactionContext<DispatchDbContext>(context, state),
            new DispatchAssignmentAuthorizer(),
            new PostgreSqlDispatchAuthorizationReader(),
            NullLogger<PostgreSqlOrganizationDriversQuery>.Instance);
    }

    /// <summary>
    /// A copy of the scenario order in <paramref name="orderStatus"/> with one OWN assignment of
    /// <paramref name="driverId"/> in <paramref name="assignmentStatus"/> (the scenario cleanup removes every order,
    /// quote and assignment of the organization).
    /// </summary>
    private static async Task AddAssignedOrderAsync(
        DispatchScenario scenario,
        string orderStatus,
        string assignmentStatus,
        Guid driverId)
    {
        var orderId = Guid.NewGuid();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO pricing.quotes
            SELECT (jsonb_populate_record(NULL::pricing.quotes, to_jsonb(q) || jsonb_build_object('id',@quote))).*
            FROM pricing.quotes q WHERE q.id=(SELECT quote_id FROM orders.orders WHERE id=@order);
            INSERT INTO orders.orders
            SELECT (jsonb_populate_record(NULL::orders.orders, to_jsonb(o) || jsonb_build_object(
              'id',@copy,'quote_id',@quote,'public_id',@public_id,'status',@status))).*
            FROM orders.orders o WHERE o.id=@order;
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,operator_org_id,driver_id,route_id,assignment_type,status,cost_cents,
              accepted_at,created_at)
            VALUES (gen_random_uuid(),@copy,@org,NULL,@driver,NULL,'OWN',@assignment_status,0,@at,@at);
            """,
            P("quote", Guid.NewGuid()),
            P("order", scenario.OrderId),
            P("copy", orderId),
            P("public_id", $"ORD_fleet_{orderId:N}"[..24]),
            P("status", orderStatus),
            P("org", scenario.OrganizationId),
            P("driver", driverId),
            P("assignment_status", assignmentStatus),
            P("at", OccurredAt));
    }

    private static Task AddPositionAsync(
        DispatchScenario scenario,
        Guid driverId,
        Guid organizationId,
        DateTimeOffset capturedAt,
        DateTimeOffset receivedAt) =>
        scenario.ExecuteAdminAsync(
            """
            INSERT INTO drivers.driver_positions(
              id,driver_id,org_id,city_id,client_event_id,point,accuracy_m,captured_at,received_at,publish_realtime)
            VALUES (
              gen_random_uuid(),@driver,@org,@city,gen_random_uuid(),
              public.ST_SetSRID(public.ST_MakePoint(-107.39,24.80),4326),5,@captured,@received,false);
            """,
            P("driver", driverId),
            P("org", organizationId),
            P("city", scenario.CityId),
            P("captured", capturedAt),
            P("received", receivedAt));
}
