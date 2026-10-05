using Dispatch.Application.Assignments;
using Dispatch.Infrastructure;
using Dispatch.Infrastructure.Assignments;
using Dispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// UI-PHASE2-DRIVER-PICKER-2026-10-05 (listAssignableDrivers) on real PostgreSQL as the NOBYPASSRLS runtime role:
/// own-organization OWN drivers with the eligibility assignDriver applies, never another organization's driver,
/// the uniform not-found for a foreign order, capability first, the owner/operator rule of assignDriver, the
/// order-state conflict and cursor pages. The read never writes.
/// </summary>
public sealed partial class DispatchPostgreSqlContractTests
{
    [PostgreSqlContractFact]
    public async Task Assignable_drivers_lists_own_organization_OWN_drivers_with_assignDriver_eligibility()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var seeded = new SeededDrivers();
        var otherOrganizationId = Guid.NewGuid();
        try
        {
            var expired = await seeded.AddAsync(scenario, scenario.OrganizationId, "OWN", "ACTIVE", "CAR",
                OccurredAt.AddMinutes(-1));
            var suspended = await seeded.AddAsync(scenario, scenario.OrganizationId, "OWN", "SUSPENDED", "VAN",
                OccurredAt.AddDays(30));
            var retired = await seeded.AddAsync(scenario, scenario.OrganizationId, "OWN", "INACTIVE", "MOTORCYCLE",
                OccurredAt.AddDays(30));
            var external = await seeded.AddAsync(scenario, scenario.OrganizationId, "EXTERNAL", "ACTIVE", "MOTORCYCLE",
                OccurredAt.AddDays(30));
            await scenario.ExecuteAdminAsync(
                """
                INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
                VALUES (@other,'Picker other','Picker other','ALLY');
                """,
                P("other", otherOrganizationId));
            var foreign = await seeded.AddAsync(scenario, otherOrganizationId, "OWN", "ACTIVE", "MOTORCYCLE",
                OccurredAt.AddDays(30));

            var page = await CreateAssignableDriversQuery().ListAsync(PickerQuery(scenario), default);

            Assert.Null(page.NextCursor);
            Assert.Equal(
                PostgreSqlUuidOrder([scenario.DriverId, expired, suspended]),
                page.Items.Select(item => item.DriverId));
            Assert.DoesNotContain(page.Items, item => item.DriverId == retired || item.DriverId == external ||
                item.DriverId == foreign);

            var eligible = page.Items.Single(item => item.DriverId == scenario.DriverId);
            Assert.True(eligible.Eligible);
            Assert.Empty(eligible.IneligibilityReasons);
            Assert.Equal("MOTORCYCLE", eligible.VehicleType);
            Assert.Equal(Paqueteria.Application.Privacy.DriverReference.From(scenario.DriverId), eligible.DriverReference);
            Assert.Equal(0, eligible.ActiveAssignmentCount);

            // EligibilityOptions configures MOTORCYCLE only: a CAR has neither a document nor a capacity policy.
            var expiredItem = page.Items.Single(item => item.DriverId == expired);
            Assert.False(expiredItem.Eligible);
            Assert.Equal(
                ["DOCUMENT_POLICY_UNAVAILABLE", "VEHICLE_CAPACITY_POLICY_UNAVAILABLE"],
                expiredItem.IneligibilityReasons);
            var suspendedItem = page.Items.Single(item => item.DriverId == suspended);
            Assert.False(suspendedItem.Eligible);
            Assert.Contains("DRIVER_STATUS_NOT_ACTIVE", suspendedItem.IneligibilityReasons);

            // An expired document on an otherwise eligible driver is reported exactly as assignDriver refuses it.
            await scenario.ExecuteAdminAsync(
                "UPDATE drivers.driver_profiles SET vehicle_type='MOTORCYCLE' WHERE id=@driver",
                P("driver", expired));
            var refreshed = (await CreateAssignableDriversQuery().ListAsync(PickerQuery(scenario), default)).Items
                .Single(item => item.DriverId == expired);
            Assert.Equal(["DOCUMENT_EXPIRED"], refreshed.IneligibilityReasons);
            var refused = await Assert.ThrowsAsync<AssignmentConflictException>(() =>
                CreateAssignmentService(fixture.AppDataSource).CreateOwnDriverAssignmentAsync(
                    Command(scenario) with { DriverId = expired }, default));
            Assert.Equal(AssignmentConflictCode.DriverDocumentExpired, refused.Code);

            // The driver listed as eligible is the one assignDriver accepts; afterwards the order is not listed.
            var created = await CreateAssignmentService(fixture.AppDataSource)
                .CreateOwnDriverAssignmentAsync(Command(scenario), default);
            Assert.Equal(scenario.DriverId, created.DriverId);
            var conflict = await Assert.ThrowsAsync<AssignmentConflictException>(() =>
                CreateAssignableDriversQuery().ListAsync(PickerQuery(scenario), default));
            Assert.Equal(AssignmentConflictCode.InvalidOrderState, conflict.Code);
        }
        finally
        {
            await seeded.DisposeAsync(scenario);
            await scenario.ExecuteAdminAsync(
                "DELETE FROM organizations.organizations WHERE id=@other",
                P("other", otherOrganizationId));
        }
    }

    [PostgreSqlContractFact]
    public async Task Assignable_drivers_count_active_assignments_and_conflict_on_an_order_with_one()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var secondQuoteId = Guid.NewGuid();
        var secondOrderId = Guid.NewGuid();

        // A second order of the organization, already DELIVERING with the scenario driver (synthetic copy; the
        // scenario cleanup removes every order, quote and assignment of the organization).
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO pricing.quotes
            SELECT (jsonb_populate_record(NULL::pricing.quotes, to_jsonb(q) || jsonb_build_object('id',@quote))).*
            FROM pricing.quotes q WHERE q.id=(SELECT quote_id FROM orders.orders WHERE id=@order);
            INSERT INTO orders.orders
            SELECT (jsonb_populate_record(NULL::orders.orders, to_jsonb(o) || jsonb_build_object(
              'id',@second,'quote_id',@quote,'public_id',@public_id,'status','DELIVERING'))).*
            FROM orders.orders o WHERE o.id=@order;
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,operator_org_id,driver_id,route_id,assignment_type,status,cost_cents,
              accepted_at,created_at)
            VALUES (gen_random_uuid(),@second,@org,NULL,@driver,NULL,'OWN','ACTIVE',0,@at,@at);
            """,
            P("quote", secondQuoteId),
            P("order", scenario.OrderId),
            P("second", secondOrderId),
            P("public_id", $"ORD_picker_{secondOrderId:N}"[..24]),
            P("org", scenario.OrganizationId),
            P("driver", scenario.DriverId),
            P("at", OccurredAt));

        var listed = Assert.Single((await CreateAssignableDriversQuery().ListAsync(PickerQuery(scenario), default)).Items);
        Assert.Equal(scenario.DriverId, listed.DriverId);
        Assert.Equal(1, listed.ActiveAssignmentCount);
        Assert.True(listed.Eligible);

        // The DELIVERING order itself is not offered, and neither is a READY_FOR_PICKUP order already holding an
        // ACCEPTED or ACTIVE assignment.
        await Assert.ThrowsAsync<AssignmentConflictException>(() => CreateAssignableDriversQuery()
            .ListAsync(PickerQuery(scenario) with { OrderId = secondOrderId }, default));
        await scenario.ExecuteAdminAsync(
            "UPDATE orders.orders SET status='RESCHEDULED' WHERE id=@second",
            P("second", secondOrderId));
        var conflict = await Assert.ThrowsAsync<AssignmentConflictException>(() => CreateAssignableDriversQuery()
            .ListAsync(PickerQuery(scenario) with { OrderId = secondOrderId }, default));
        Assert.Equal(AssignmentConflictCode.ActiveAssignmentExists, conflict.Code);
    }

    [PostgreSqlContractFact]
    public async Task Assignable_drivers_refuse_other_statuses_foreign_orders_and_roles_without_assignDriver()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var otherOrganizationId = Guid.NewGuid();
        try
        {
            foreach (var status in new[] { "CONFIRMED", "ASSIGNED", "DELIVERED", "CANCELLED" })
            {
                await scenario.ExecuteAdminAsync(
                    "UPDATE orders.orders SET status=@status WHERE id=@order",
                    P("status", status),
                    P("order", scenario.OrderId));
                var conflict = await Assert.ThrowsAsync<AssignmentConflictException>(() =>
                    CreateAssignableDriversQuery().ListAsync(PickerQuery(scenario), default));
                Assert.Equal(AssignmentConflictCode.InvalidOrderState, conflict.Code);
            }

            await scenario.ExecuteAdminAsync(
                "UPDATE orders.orders SET status='RESCHEDULED' WHERE id=@order",
                P("order", scenario.OrderId));
            Assert.Single((await CreateAssignableDriversQuery().ListAsync(PickerQuery(scenario), default)).Items);

            // A missing order and another organization's order are the same not-found.
            await Assert.ThrowsAsync<AssignmentNotFoundException>(() => CreateAssignableDriversQuery()
                .ListAsync(PickerQuery(scenario) with { OrderId = Guid.NewGuid() }, default));
            await scenario.ExecuteAdminAsync(
                """
                INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
                VALUES (@other,'Picker tenant','Picker tenant','BUSINESS');
                INSERT INTO organizations.organization_memberships(
                  id,user_id,organization_id,role,status,is_default,granted_at)
                VALUES (gen_random_uuid(),@dispatcher,@other,'DISPATCHER','ACTIVE',false,now());
                """,
                P("other", otherOrganizationId),
                P("dispatcher", scenario.DispatcherUserId));
            await Assert.ThrowsAsync<AssignmentNotFoundException>(() => CreateAssignableDriversQuery()
                .ListAsync(PickerQuery(scenario) with { OrganizationId = otherOrganizationId }, default));

            // Capability first: a VIEWER, an inactive DISPATCHER and a PLATFORM_ADMIN without MFA read nothing,
            // not even whether the order exists.
            foreach (var (role, status, mfa) in new[]
                     {
                         ("VIEWER", "ACTIVE", true), ("DRIVER", "ACTIVE", true), ("FINANCE", "ACTIVE", true),
                         ("DISPATCHER", "SUSPENDED", false), ("PLATFORM_ADMIN", "ACTIVE", false),
                     })
            {
                await scenario.SetDispatcherMembershipAsync(role, status);
                await Assert.ThrowsAsync<AssignmentForbiddenException>(() => CreateAssignableDriversQuery()
                    .ListAsync(PickerQuery(scenario) with { MfaSatisfied = mfa }, default));
                await Assert.ThrowsAsync<AssignmentForbiddenException>(() => CreateAssignableDriversQuery()
                    .ListAsync(PickerQuery(scenario) with { MfaSatisfied = mfa, OrderId = Guid.NewGuid() }, default));
            }

            await scenario.SetDispatcherMembershipAsync("PLATFORM_ADMIN", "ACTIVE");
            Assert.Single((await CreateAssignableDriversQuery()
                .ListAsync(PickerQuery(scenario) with { MfaSatisfied = true }, default)).Items);
        }
        finally
        {
            await scenario.ExecuteAdminAsync(
                """
                DELETE FROM organizations.organization_memberships WHERE organization_id=@other;
                DELETE FROM organizations.organizations WHERE id=@other;
                """,
                P("other", otherOrganizationId));
        }
    }

    [PostgreSqlContractFact]
    public async Task Assignable_drivers_follow_the_owner_and_operator_rule_of_assignDriver()
    {
        // As in assignDriver, the operator of another owner's order lists and assigns its own drivers, and the owner
        // lists its own; neither ever sees the other's drivers.
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var seeded = new SeededDrivers();
        var operatorId = Guid.NewGuid();
        try
        {
            await scenario.ExecuteAdminAsync(
                """
                INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
                VALUES (@operator,'Picker operator','Picker operator','ALLY');
                INSERT INTO organizations.organization_memberships(
                  id,user_id,organization_id,role,status,is_default,granted_at)
                VALUES (gen_random_uuid(),@dispatcher,@operator,'DISPATCHER','ACTIVE',false,now());
                UPDATE orders.orders SET operator_org_id=@operator WHERE id=@order;
                UPDATE orders.package_items SET operator_org_id=@operator WHERE order_id=@order;
                """,
                P("operator", operatorId),
                P("dispatcher", scenario.DispatcherUserId),
                P("order", scenario.OrderId));
            var operatorDriver = await seeded.AddAsync(scenario, operatorId, "OWN", "ACTIVE", "MOTORCYCLE",
                OccurredAt.AddDays(30));

            var asOperator = await CreateAssignableDriversQuery()
                .ListAsync(PickerQuery(scenario) with { OrganizationId = operatorId }, default);
            var operatorItem = Assert.Single(asOperator.Items);
            Assert.Equal(operatorDriver, operatorItem.DriverId);
            Assert.True(operatorItem.Eligible);

            var asOwner = await CreateAssignableDriversQuery().ListAsync(PickerQuery(scenario), default);
            Assert.Equal(scenario.DriverId, Assert.Single(asOwner.Items).DriverId);

            // The operator's listed driver is accepted by assignDriver in the operator's context.
            var created = await CreateAssignmentService(fixture.AppDataSource).CreateOwnDriverAssignmentAsync(
                Command(scenario) with { OrganizationId = operatorId, DriverId = operatorDriver },
                default);
            Assert.Equal(operatorDriver, created.DriverId);
        }
        finally
        {
            await scenario.ExecuteAdminAsync(
                """
                DELETE FROM dispatch.assignments WHERE order_id=@order;
                UPDATE orders.package_items SET operator_org_id=NULL WHERE order_id=@order;
                UPDATE orders.orders SET operator_org_id=NULL WHERE id=@order;
                """,
                P("order", scenario.OrderId));
            await using (var migrator = await TenantTransaction.BeginAsync(
                fixture.AdminDataSource, "paqueteria_migrator", scenario.DispatcherUserId, [operatorId]))
            {
                await using var cleanup = new NpgsqlCommand(
                    """
                    DELETE FROM orders.order_events WHERE operator_org_id=@operator;
                    DELETE FROM platform.audit_logs WHERE org_id=@operator;
                    """,
                    migrator.Connection,
                    migrator.Transaction);
                cleanup.Parameters.Add(P("operator", operatorId));
                await cleanup.ExecuteNonQueryAsync();
                await migrator.CommitAsync();
            }

            await seeded.DisposeAsync(scenario);
            await scenario.ExecuteAdminAsync(
                """
                DELETE FROM platform.outbox_events WHERE owner_org_id=@operator;
                DELETE FROM platform.idempotency_keys WHERE owner_org_id=@operator;
                DELETE FROM organizations.organization_memberships WHERE organization_id=@operator;
                DELETE FROM organizations.organizations WHERE id=@operator;
                """,
                P("operator", operatorId));
        }
    }

    [PostgreSqlContractFact]
    public async Task Assignable_drivers_pages_by_cursor_without_overlap()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var prefix = $"picker-page-{Guid.NewGuid():N}-";
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
                SELECT driver_id,user_id,@org,@city,'OWN','MOTORCYCLE','ACTIVE',@created FROM created;
                """,
                P("count", AssignableDriverPolicy.PageSize),
                P("prefix", prefix),
                P("created", OccurredAt.AddDays(-1)),
                P("org", scenario.OrganizationId),
                P("city", scenario.CityId));

            var first = await CreateAssignableDriversQuery().ListAsync(PickerQuery(scenario), default);
            Assert.Equal(AssignableDriverPolicy.PageSize, first.Items.Count);
            Assert.NotNull(first.NextCursor);
            Assert.True(AssignableDriverCursorCodec.TryDecode(first.NextCursor, out var cursor));
            var second = await CreateAssignableDriversQuery()
                .ListAsync(PickerQuery(scenario) with { Cursor = cursor }, default);
            var item = Assert.Single(second.Items);
            Assert.Null(second.NextCursor);

            var all = first.Items.Concat(second.Items).Select(value => value.DriverId).ToArray();
            Assert.Equal(AssignableDriverPolicy.PageSize + 1, all.Distinct().Count());
            Assert.Equal(PostgreSqlUuidOrder(all), all);
            Assert.Contains(scenario.DriverId, all);
            // Without a document, the seeded drivers are listed as ineligible; the scenario driver stays eligible.
            Assert.Single(first.Items.Concat(second.Items), value => value.Eligible);
            Assert.All(
                first.Items.Concat(second.Items).Where(value => !value.Eligible),
                value => Assert.Equal(["REQUIRED_DOCUMENT_MISSING"], value.IneligibilityReasons));
            Assert.NotEqual(Guid.Empty, item.DriverId);
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

    /// <summary>PostgreSQL orders uuid by bytes, which is the ordinal order of the lowercase canonical text.</summary>
    private static Guid[] PostgreSqlUuidOrder(IEnumerable<Guid> values) =>
        values.OrderBy(value => value.ToString("D"), StringComparer.Ordinal).ToArray();

    private static ListAssignableDriversQuery PickerQuery(DispatchScenario scenario) => new(
        scenario.DispatcherUserId,
        scenario.OrganizationId,
        false,
        scenario.OrderId,
        null);

    private PostgreSqlAssignableDriversQuery CreateAssignableDriversQuery()
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new DispatchDbContext(dbOptions, state);
        return new PostgreSqlAssignableDriversQuery(
            new TenantTransactionContext<DispatchDbContext>(context, state),
            Options.Create(EligibilityOptions()),
            new DispatchAssignmentAuthorizer(),
            new PostgreSqlDispatchAuthorizationReader(),
            new FixedClock(OccurredAt),
            NullLogger<PostgreSqlAssignableDriversQuery>.Instance);
    }

    /// <summary>Drivers seeded by one test, removed explicitly (documents, profiles, memberships, users).</summary>
    private sealed class SeededDrivers
    {
        private readonly List<Guid> users = [];
        private readonly List<Guid> drivers = [];

        public async Task<Guid> AddAsync(
            DispatchScenario scenario,
            Guid organizationId,
            string driverType,
            string profileStatus,
            string vehicleType,
            DateTimeOffset documentExpiresAt)
        {
            var userId = Guid.NewGuid();
            var driverId = Guid.NewGuid();
            users.Add(userId);
            drivers.Add(driverId);
            await scenario.ExecuteAdminAsync(
                """
                INSERT INTO identity.users(id,identity_subject,status,created_at)
                VALUES (@user,@subject,'ACTIVE',@created);
                INSERT INTO organizations.organization_memberships(
                  id,user_id,organization_id,role,status,is_default,granted_at)
                VALUES (gen_random_uuid(),@user,@org,'DRIVER','ACTIVE',true,@created);
                INSERT INTO drivers.driver_profiles(
                  id,user_id,org_id,home_city_id,driver_type,vehicle_type,status,created_at)
                VALUES (@driver,@user,@org,@city,@type,@vehicle,@status,@created);
                INSERT INTO drivers.driver_documents(
                  id,driver_id,org_id,document_type,object_key,sha256,expires_at,status,created_at)
                VALUES (gen_random_uuid(),@driver,@org,'IDENTITY',@object_key,
                  decode(repeat('ef',32),'hex'),@expires,'VALID',@created);
                """,
                P("user", userId),
                P("subject", $"picker-{userId:N}"),
                P("created", OccurredAt.AddDays(-1)),
                P("org", organizationId),
                P("driver", driverId),
                P("city", scenario.CityId),
                P("type", driverType),
                P("vehicle", vehicleType),
                P("status", profileStatus),
                P("object_key", $"synthetic/picker/{driverId:N}"),
                P("expires", documentExpiresAt));
            return driverId;
        }

        public Task DisposeAsync(DispatchScenario scenario) =>
            scenario.ExecuteAdminAsync(
                """
                DELETE FROM dispatch.assignments WHERE driver_id=ANY(@drivers);
                DELETE FROM drivers.driver_documents WHERE driver_id=ANY(@drivers);
                DELETE FROM drivers.driver_profiles WHERE id=ANY(@drivers);
                DELETE FROM organizations.organization_memberships WHERE user_id=ANY(@users);
                DELETE FROM identity.users WHERE id=ANY(@users);
                """,
                P("drivers", drivers.ToArray()),
                P("users", users.ToArray()));
    }
}
