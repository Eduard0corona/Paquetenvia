using Orders.Domain;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Realtime.Infrastructure.Dispatching;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// ORD-002-OPERATOR-DRIVER-EVENTS-2026-10-03 (project owner: "Solo avisar a su repartidor"): only the owner
/// transitions an order through ORD-002, and when the order is operated by another organization with a current
/// assignment of that operator, the operator's driver is the driver audience of the owner-tagged
/// orders.status-changed row. Realtime publishes to that driver only after the operator-context evidence check;
/// a third organization's driver never qualifies, and the owner-only audience is unchanged.
/// </summary>
public sealed partial class OrderAttemptCustodyPostgreSqlContractTests
{
    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Operator_driver_is_the_audience_of_owner_transitions_and_a_third_driver_never_is()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.CanonicalPathTo("ASSIGNED"));
        var operatorId = Guid.NewGuid();
        var third = await AddThirdOrganizationDriverAsync(world);
        await MoveDriverToOperatorAsync(world, operatorId);
        try
        {
            var result = await TransitionAsync(world, OrderStatus.AtPickup);

            var (owner, driver, assignment) = await ReadStatusAudienceAsync(world, result.Version);
            Assert.Equal(world.Scenario.OrganizationId, owner);
            Assert.Equal(world.DriverId.ToString("D"), driver);
            Assert.Equal(world.AssignmentId.ToString("D"), assignment);

            await using var connections = new RealtimeWorkerConnectionFactory(fixture.WorkerConnectionString);
            var evidence = new PostgreSqlRealtimeOutboxEvidenceReader(connections);
            Assert.True(await evidence.IsDriverAudienceAuthorizedAsync(
                world.Scenario.OrganizationId, world.Scenario.OrderId, world.AssignmentId, world.DriverId, default));

            // A third organization's driver: neither named nor placed on the assignment is ever authorized.
            Assert.False(await evidence.IsDriverAudienceAuthorizedAsync(
                world.Scenario.OrganizationId, world.Scenario.OrderId, world.AssignmentId, third.DriverId, default));
            await world.Scenario.ExecuteAdminAsync(
                "UPDATE dispatch.assignments SET driver_id=@third_driver WHERE id=@assignment;",
                SyntheticOrderScenario.P("third_driver", third.DriverId),
                SyntheticOrderScenario.P("assignment", world.AssignmentId));
            Assert.False(await evidence.IsDriverAudienceAuthorizedAsync(
                world.Scenario.OrganizationId, world.Scenario.OrderId, world.AssignmentId, third.DriverId, default));
            await world.Scenario.ExecuteAdminAsync(
                "UPDATE dispatch.assignments SET driver_id=@driver WHERE id=@assignment;",
                SyntheticOrderScenario.P("driver", world.DriverId),
                SyntheticOrderScenario.P("assignment", world.AssignmentId));

            // The assignment's operator must still be the order's stored operator.
            await world.Scenario.ExecuteAdminAsync(
                "UPDATE orders.orders SET operator_org_id=@third WHERE id=@order;",
                SyntheticOrderScenario.P("third", third.OrganizationId),
                SyntheticOrderScenario.P("order", world.Scenario.OrderId));
            Assert.False(await evidence.IsDriverAudienceAuthorizedAsync(
                world.Scenario.OrganizationId, world.Scenario.OrderId, world.AssignmentId, world.DriverId, default));
            await world.Scenario.ExecuteAdminAsync(
                "UPDATE orders.orders SET operator_org_id=@operator WHERE id=@order;",
                SyntheticOrderScenario.P("operator", operatorId),
                SyntheticOrderScenario.P("order", world.Scenario.OrderId));

            // The operator's driver loses the audience with its DRIVER membership.
            await world.Scenario.ExecuteAdminAsync(
                """
                UPDATE organizations.organization_memberships SET status='SUSPENDED'
                WHERE user_id=@driver_user AND organization_id=@operator;
                """,
                SyntheticOrderScenario.P("driver_user", world.DriverUserId),
                SyntheticOrderScenario.P("operator", operatorId));
            Assert.False(await evidence.IsDriverAudienceAuthorizedAsync(
                world.Scenario.OrganizationId, world.Scenario.OrderId, world.AssignmentId, world.DriverId, default));
        }
        finally
        {
            await RestoreDriverAsync(world, operatorId);
            await RemoveThirdOrganizationAsync(world, third);
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Owner_transition_names_no_driver_when_the_assignment_operator_is_not_the_order_operator()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.CanonicalPathTo("ASSIGNED"));
        var operatorId = Guid.NewGuid();
        var third = await AddThirdOrganizationDriverAsync(world);
        await MoveDriverToOperatorAsync(world, operatorId);
        await world.Scenario.ExecuteAdminAsync(
            "UPDATE orders.orders SET operator_org_id=@third WHERE id=@order;",
            SyntheticOrderScenario.P("third", third.OrganizationId),
            SyntheticOrderScenario.P("order", world.Scenario.OrderId));
        try
        {
            var result = await TransitionAsync(world, OrderStatus.AtPickup);

            var (owner, driver, assignment) = await ReadStatusAudienceAsync(world, result.Version);
            Assert.Equal(world.Scenario.OrganizationId, owner);
            Assert.Null(driver);
            Assert.Null(assignment);
        }
        finally
        {
            await RestoreDriverAsync(world, operatorId);
            await RemoveThirdOrganizationAsync(world, third);
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Owner_only_driver_audience_is_unchanged()
    {
        // No operator: the owner's active driver is named, exactly as before.
        await using (var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.CanonicalPathTo("ASSIGNED")))
        {
            var result = await TransitionAsync(world, OrderStatus.AtPickup);
            var (owner, driver, assignment) = await ReadStatusAudienceAsync(world, result.Version);
            Assert.Equal(world.Scenario.OrganizationId, owner);
            Assert.Equal(world.DriverId.ToString("D"), driver);
            Assert.Equal(world.AssignmentId.ToString("D"), assignment);
        }

        // No operator and a suspended DRIVER membership: still nobody, exactly as before.
        await using (var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.CanonicalPathTo("ASSIGNED")))
        {
            await world.Scenario.ExecuteAdminAsync(
                """
                UPDATE organizations.organization_memberships SET status='SUSPENDED'
                WHERE user_id=@driver_user AND organization_id=@org;
                """,
                SyntheticOrderScenario.P("driver_user", world.DriverUserId),
                SyntheticOrderScenario.P("org", world.Scenario.OrganizationId));
            var result = await TransitionAsync(world, OrderStatus.AtPickup);
            var (_, driver, assignment) = await ReadStatusAudienceAsync(world, result.Version);
            Assert.Null(driver);
            Assert.Null(assignment);
        }
    }

    private static async Task MoveDriverToOperatorAsync(AttemptWorld world, Guid operatorId) =>
        await world.Scenario.ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
            VALUES (@operator,'ORD-002 operator','ORD-002 operator','ALLY');
            INSERT INTO organizations.organization_memberships(
              id,user_id,organization_id,role,status,is_default)
            VALUES (gen_random_uuid(),@driver_user,@operator,'DRIVER','ACTIVE',false);
            UPDATE organizations.organization_memberships SET status='SUSPENDED'
            WHERE user_id=@driver_user AND organization_id=@org;
            UPDATE drivers.driver_profiles SET org_id=@operator WHERE id=@driver;
            UPDATE drivers.driver_documents SET org_id=@operator WHERE driver_id=@driver;
            UPDATE orders.orders SET operator_org_id=@operator WHERE id=@order;
            UPDATE orders.package_items SET operator_org_id=@operator WHERE order_id=@order;
            UPDATE dispatch.assignments SET operator_org_id=@operator WHERE id=@assignment;
            """,
            SyntheticOrderScenario.P("operator", operatorId),
            SyntheticOrderScenario.P("org", world.Scenario.OrganizationId),
            SyntheticOrderScenario.P("driver_user", world.DriverUserId),
            SyntheticOrderScenario.P("driver", world.DriverId),
            SyntheticOrderScenario.P("order", world.Scenario.OrderId),
            SyntheticOrderScenario.P("assignment", world.AssignmentId));

    private async Task RestoreDriverAsync(AttemptWorld world, Guid operatorId)
    {
        // ORD-002 events carry the operator; append-only rows are removed only as the migrator (test cleanup).
        await using (var migrator = await TenantTransaction.BeginAsync(
            fixture.AdminDataSource, "paqueteria_migrator", world.Scenario.UserId, [world.Scenario.OrganizationId]))
        {
            await using var events = new Npgsql.NpgsqlCommand(
                "DELETE FROM orders.order_events WHERE order_id=@order;",
                migrator.Connection,
                migrator.Transaction);
            events.Parameters.AddWithValue("order", world.Scenario.OrderId);
            await events.ExecuteNonQueryAsync();
            await migrator.CommitAsync();
        }

        await world.Scenario.ExecuteAdminAsync(
            """
            UPDATE dispatch.assignments SET operator_org_id=NULL WHERE id=@assignment;
            UPDATE orders.package_items SET operator_org_id=NULL WHERE order_id=@order;
            UPDATE orders.orders SET operator_org_id=NULL WHERE id=@order;
            UPDATE drivers.driver_documents SET org_id=@org WHERE driver_id=@driver;
            UPDATE drivers.driver_profiles SET org_id=@org WHERE id=@driver;
            DELETE FROM organizations.organization_memberships WHERE organization_id=@operator;
            DELETE FROM organizations.organizations WHERE id=@operator;
            """,
            SyntheticOrderScenario.P("operator", operatorId),
            SyntheticOrderScenario.P("org", world.Scenario.OrganizationId),
            SyntheticOrderScenario.P("driver", world.DriverId),
            SyntheticOrderScenario.P("order", world.Scenario.OrderId),
            SyntheticOrderScenario.P("assignment", world.AssignmentId));
    }

    private static async Task<ThirdOrganizationDriver> AddThirdOrganizationDriverAsync(AttemptWorld world)
    {
        var third = new ThirdOrganizationDriver(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.Scenario.ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
            VALUES (@third,'ORD-002 third','ORD-002 third','BUSINESS');
            INSERT INTO identity.users(id,identity_subject,status) VALUES (@third_user,@subject,'ACTIVE');
            INSERT INTO organizations.organization_memberships(
              id,user_id,organization_id,role,status,is_default)
            VALUES (gen_random_uuid(),@third_user,@third,'DRIVER','ACTIVE',true);
            INSERT INTO drivers.driver_profiles(
              id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
            VALUES (@third_driver,@third_user,@third,@city,'OWN','MOTORCYCLE','ACTIVE');
            """,
            SyntheticOrderScenario.P("third", third.OrganizationId),
            SyntheticOrderScenario.P("third_user", third.UserId),
            SyntheticOrderScenario.P("subject", $"ord002-third-driver|{third.UserId:N}"),
            SyntheticOrderScenario.P("third_driver", third.DriverId),
            SyntheticOrderScenario.P("city", world.Scenario.CityId));
        return third;
    }

    private static Task RemoveThirdOrganizationAsync(AttemptWorld world, ThirdOrganizationDriver third) =>
        world.Scenario.ExecuteAdminAsync(
            """
            DELETE FROM drivers.driver_profiles WHERE id=@third_driver;
            DELETE FROM organizations.organization_memberships WHERE organization_id=@third;
            DELETE FROM identity.users WHERE id=@third_user;
            DELETE FROM organizations.organizations WHERE id=@third;
            """,
            SyntheticOrderScenario.P("third", third.OrganizationId),
            SyntheticOrderScenario.P("third_user", third.UserId),
            SyntheticOrderScenario.P("third_driver", third.DriverId));

    private async Task<(Guid Owner, string? Driver, string? Assignment)> ReadStatusAudienceAsync(
        AttemptWorld world,
        int version)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT owner_org_id,payload->>'authorized_driver_id',payload->>'assignment_id'
            FROM platform.outbox_events
            WHERE aggregate_id=@order AND aggregate_version=@version AND topic='orders.status-changed'
            """);
        command.Parameters.AddWithValue("order", world.Scenario.OrderId);
        command.Parameters.AddWithValue("version", version);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var row = (reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
        Assert.False(await reader.ReadAsync());
        return row;
    }

    private sealed record ThirdOrganizationDriver(Guid OrganizationId, Guid UserId, Guid DriverId);
}
