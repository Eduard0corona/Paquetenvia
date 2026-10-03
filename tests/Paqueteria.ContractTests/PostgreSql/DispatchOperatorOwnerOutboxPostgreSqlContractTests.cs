using Dispatch.Application.Assignments;
using Dispatch.Infrastructure.Persistence.Migrations;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;
using Realtime.Infrastructure.Dispatching;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03 (project owner, 2026-10-03: "Función segura a nombre del dueño";
/// "Sí, el dueño lo ve"): the operator organization of another owner's order assigns its own driver, and the
/// owner-tagged outbox and audit rows are written by the two SECURITY DEFINER functions of
/// paqueteria_operator_outbox_executor, exactly once, while every other caller, context or row shape fails closed.
/// </summary>
public sealed partial class DispatchPostgreSqlContractTests
{
    private const string OperatorOutboxCall =
        """
        SELECT security.append_operator_order_outbox(
          @id,@owner,@tenant,@topic,@aggregate_type,@order,@version,@payload,@priority,@available,@created)
        """;

    private const string OperatorAuditCall =
        """
        SELECT security.append_operator_order_audit(
          @id,@org,@actor,@action,@entity_type,@entity_id,@request_id,@payload,@occurred)
        """;

    [PostgreSqlContractFact]
    public async Task Operator_owner_outbox_operator_assignment_writes_owner_tagged_rows_exactly_once()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var operatorId = Guid.NewGuid();
        await MakeOperatorAsync(scenario, operatorId);
        try
        {
            var service = CreateAssignmentService(fixture.AppDataSource);
            var command = Command(scenario, costCents: 4_500) with { OrganizationId = operatorId };
            var created = await service.CreateOwnDriverAssignmentAsync(command, default);
            var replay = await service.CreateOwnDriverAssignmentAsync(command, default);
            Assert.Equal(created, replay);
            Assert.Equal("ACCEPTED", created.Status);

            await using var query = fixture.AdminDataSource.CreateCommand(
                """
                SELECT
                  (SELECT count(*) FROM dispatch.assignments
                    WHERE order_id=@order AND owner_org_id=@owner AND operator_org_id=@operator),
                  (SELECT status FROM orders.orders WHERE id=@order),
                  (SELECT count(*) FROM orders.order_events
                    WHERE order_id=@order AND owner_org_id=@owner AND operator_org_id=@operator AND aggregate_version=2),
                  (SELECT count(*) FROM platform.outbox_events
                    WHERE aggregate_id=@order AND owner_org_id=@owner AND aggregate_version=2 AND status='PENDING'
                      AND priority=50 AND attempts=0 AND lease_token IS NULL
                      AND tenant_context=jsonb_build_object('organization_ids',jsonb_build_array(@owner::text))),
                  (SELECT string_agg(topic, ',' ORDER BY topic) FROM platform.outbox_events WHERE aggregate_id=@order),
                  (SELECT count(*) FROM platform.outbox_events WHERE aggregate_id=@order AND owner_org_id<>@owner),
                  (SELECT count(*) FROM platform.audit_logs
                    WHERE org_id=@owner AND actor_id=@actor AND action='ASSIGNMENT_CREATED'
                      AND entity_type='Assignment' AND entity_id=@assignment),
                  (SELECT count(*) FROM platform.audit_logs
                    WHERE org_id=@owner AND actor_id=@actor AND action='ORDER_STATUS_CHANGED'
                      AND entity_type='Order' AND entity_id=@order AND payload_redacted->'new_version'='2'::jsonb),
                  (SELECT count(*) FROM platform.audit_logs WHERE org_id=@operator),
                  (SELECT count(*) FROM platform.idempotency_keys
                    WHERE owner_org_id=@operator AND scope='DSP-002:ASSIGN_OWN_DRIVER' AND response_status=201),
                  (SELECT payload->>'authorized_driver_id' FROM platform.outbox_events
                    WHERE aggregate_id=@order AND topic='orders.status-changed');
                """);
            query.Parameters.AddWithValue("order", scenario.OrderId);
            query.Parameters.AddWithValue("owner", scenario.OrganizationId);
            query.Parameters.AddWithValue("operator", operatorId);
            query.Parameters.AddWithValue("actor", scenario.DispatcherUserId);
            query.Parameters.AddWithValue("assignment", created.Id);
            await using (var reader = await query.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal("ASSIGNED", reader.GetString(1));
                Assert.Equal(1L, reader.GetInt64(2));
                Assert.Equal(3L, reader.GetInt64(3));
                Assert.Equal(
                    "dispatch.assignment-changed,orders.status-changed,orders.timeline-event-added",
                    reader.GetString(4));
                Assert.Equal(0L, reader.GetInt64(5));
                Assert.Equal(1L, reader.GetInt64(6));
                Assert.Equal(1L, reader.GetInt64(7));
                Assert.Equal(0L, reader.GetInt64(8));
                Assert.Equal(1L, reader.GetInt64(9));
                Assert.Equal(scenario.DriverId.ToString("D"), reader.GetString(10));
            }

            // The owner sees the rows in its own tenant context; the operator's context still cannot read them.
            Assert.Equal(2L, await CountAuditVisibleAsync(scenario.DispatcherUserId, scenario.OrganizationId, scenario.OrderId, created.Id));
            Assert.Equal(0L, await CountAuditVisibleAsync(scenario.DispatcherUserId, operatorId, scenario.OrderId, created.Id));
        }
        finally
        {
            await RemoveOperatorAsync(scenario, operatorId);
        }
    }

    [PostgreSqlContractFact]
    public async Task Operator_owner_outbox_refuses_every_other_caller_context_and_row_shape()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var operatorId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();
        var thirdUserId = Guid.NewGuid();
        await MakeOperatorAsync(scenario, operatorId);
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
            VALUES (@third,'DSP third','DSP third','BUSINESS');
            INSERT INTO identity.users(id,identity_subject,status,created_at)
            VALUES (@third_user,@subject,'ACTIVE',now());
            INSERT INTO organizations.organization_memberships(
              id,user_id,organization_id,role,status,is_default,granted_at)
            VALUES (gen_random_uuid(),@third_user,@third,'DISPATCHER','ACTIVE',true,now());
            """,
            P("third", thirdId),
            P("third_user", thirdUserId),
            P("subject", $"dsp-op-third-{thirdUserId:N}"));
        try
        {
            var created = await CreateAssignmentService(fixture.AppDataSource)
                .CreateOwnDriverAssignmentAsync(Command(scenario) with { OrganizationId = operatorId }, default);
            var status = await ReadOutboxRowAsync(scenario.OrderId, "orders.status-changed");
            var assignmentAudit = await ReadAuditRowAsync(scenario.OrganizationId, "ASSIGNMENT_CREATED", created.Id);
            var transitionAudit = await ReadAuditRowAsync(scenario.OrganizationId, "ORDER_STATUS_CHANGED", scenario.OrderId);
            var dispatcher = scenario.DispatcherUserId;
            var owner = scenario.OrganizationId;

            // The exact rows already written: a second write of the same topic, version or audit is refused.
            await AssertOutboxRefusedAsync(dispatcher, [operatorId], status, "DSP_OPERATOR_OWNER_DUPLICATE_REFUSED");
            await AssertAuditRefusedAsync(dispatcher, [operatorId], assignmentAudit, "DSP_OPERATOR_OWNER_DUPLICATE_REFUSED");
            await AssertAuditRefusedAsync(dispatcher, [operatorId], transitionAudit, "DSP_OPERATOR_OWNER_DUPLICATE_REFUSED");

            // Tenant context: the owner, a third organization, two organizations, none.
            await AssertOutboxRefusedAsync(dispatcher, [owner], status, "DSP_OPERATOR_OWNER_ORDER_REFUSED");
            await AssertOutboxRefusedAsync(thirdUserId, [thirdId], status, "DSP_OPERATOR_OWNER_ORDER_REFUSED");
            await AssertAuditRefusedAsync(thirdUserId, [thirdId], transitionAudit with { ActorId = thirdUserId },
                "DSP_OPERATOR_OWNER_ORDER_REFUSED");
            await AssertOutboxRefusedAsync(dispatcher, [operatorId, owner], status, "DSP_OPERATOR_OWNER_CONTEXT_REFUSED");
            await AssertOutboxRefusedAsync(dispatcher, [], status, "DSP_OPERATOR_OWNER_CONTEXT_REFUSED");

            // Actor: the operator's DRIVER (not a dispatcher or admin), a stranger, or not the caller.
            await AssertOutboxRefusedAsync(scenario.DriverUserId, [operatorId], status, "DSP_OPERATOR_OWNER_ACTOR_REFUSED");
            await AssertOutboxRefusedAsync(thirdUserId, [operatorId], status, "DSP_OPERATOR_OWNER_ACTOR_REFUSED");
            await AssertAuditRefusedAsync(dispatcher, [operatorId], transitionAudit with { ActorId = thirdUserId },
                "DSP_OPERATOR_OWNER_ACTOR_REFUSED");

            // Owner, topic, audience, version, priority, payload and action outside the DSP-002 allow-list.
            await AssertOutboxRefusedAsync(dispatcher, [operatorId], status with { Owner = thirdId },
                "DSP_OPERATOR_OWNER_ORDER_REFUSED");
            await AssertOutboxRefusedAsync(dispatcher, [operatorId], status with { Topic = "orders.created" },
                "DSP_OPERATOR_OWNER_TOPIC_REFUSED");
            await AssertOutboxRefusedAsync(dispatcher, [operatorId],
                status with { Tenant = $$"""{"organization_ids":["{{operatorId:D}}"]}""" },
                "DSP_OPERATOR_OWNER_OUTBOX_REFUSED");
            await AssertOutboxRefusedAsync(dispatcher, [operatorId], status with { AggregateType = "Assignment" },
                "DSP_OPERATOR_OWNER_OUTBOX_REFUSED");
            await AssertOutboxRefusedAsync(dispatcher, [operatorId], status with { Version = 1 },
                "DSP_OPERATOR_OWNER_OUTBOX_REFUSED");
            await AssertOutboxRefusedAsync(dispatcher, [operatorId], status with { Priority = 100 },
                "DSP_OPERATOR_OWNER_OUTBOX_REFUSED");
            await AssertOutboxRefusedAsync(dispatcher, [operatorId],
                status with { Payload = status.Payload.Replace("{", "{\"recipient_phone\":\"6671234567\",", StringComparison.Ordinal) },
                "DSP_OPERATOR_OWNER_PAYLOAD_REFUSED");
            await AssertOutboxRefusedAsync(dispatcher, [operatorId],
                status with { Payload = status.Payload.Replace(scenario.DriverId.ToString("D"), thirdUserId.ToString("D"), StringComparison.Ordinal) },
                "DSP_OPERATOR_OWNER_PAYLOAD_REFUSED");
            await AssertAuditRefusedAsync(dispatcher, [operatorId], transitionAudit with { Action = "ORDER_CANCELLED" },
                "DSP_OPERATOR_OWNER_ACTION_REFUSED");
            await AssertAuditRefusedAsync(dispatcher, [operatorId], transitionAudit with { EntityType = "Assignment" },
                "DSP_OPERATOR_OWNER_ACTION_REFUSED");
            await AssertAuditRefusedAsync(dispatcher, [operatorId],
                transitionAudit with { Payload = transitionAudit.Payload.Replace("{", "{\"note\":\"x\",", StringComparison.Ordinal) },
                "DSP_OPERATOR_OWNER_PAYLOAD_REFUSED");

            // An order this operator does not operate: refused even with a well-formed row.
            await scenario.ExecuteAdminAsync(
                "UPDATE orders.orders SET operator_org_id=@third WHERE id=@order;",
                P("third", thirdId),
                P("order", scenario.OrderId));
            await AssertOutboxRefusedAsync(dispatcher, [operatorId], status with { Topic = "orders.timeline-event-added" },
                "DSP_OPERATOR_OWNER_ORDER_REFUSED");
            await scenario.ExecuteAdminAsync(
                "UPDATE orders.orders SET operator_org_id=@operator WHERE id=@order;",
                P("operator", operatorId),
                P("order", scenario.OrderId));

            // Nothing was written by any refused call.
            await using var count = fixture.AdminDataSource.CreateCommand(
                """
                SELECT (SELECT count(*) FROM platform.outbox_events WHERE aggregate_id=@order),
                       (SELECT count(*) FROM platform.audit_logs WHERE entity_id IN (@order,@assignment))
                """);
            count.Parameters.AddWithValue("order", scenario.OrderId);
            count.Parameters.AddWithValue("assignment", created.Id);
            await using var reader = await count.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(3L, reader.GetInt64(0));
            Assert.Equal(2L, reader.GetInt64(1));
        }
        finally
        {
            await scenario.ExecuteAdminAsync(
                """
                DELETE FROM organizations.organization_memberships WHERE organization_id=@third OR user_id=@third_user;
                DELETE FROM identity.users WHERE id=@third_user;
                DELETE FROM organizations.organizations WHERE id=@third;
                """,
                P("third", thirdId),
                P("third_user", thirdUserId));
            await RemoveOperatorAsync(scenario, operatorId);
        }
    }

    [PostgreSqlContractFact]
    public async Task Operator_owner_outbox_owner_path_is_unchanged_and_never_uses_the_functions()
    {
        // The owner of the order assigns its own driver: the rows are inserted directly under RLS, exactly as
        // before. Calling the operator functions from the owner's context is refused.
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var created = await CreateAssignmentService(fixture.AppDataSource)
            .CreateOwnDriverAssignmentAsync(Command(scenario), default);
        var status = await ReadOutboxRowAsync(scenario.OrderId, "orders.status-changed");
        var audit = await ReadAuditRowAsync(scenario.OrganizationId, "ASSIGNMENT_CREATED", created.Id);
        Assert.Equal(scenario.OrganizationId, status.Owner);
        await AssertOutboxRefusedAsync(
            scenario.DispatcherUserId, [scenario.OrganizationId], status, "DSP_OPERATOR_OWNER_ORDER_REFUSED");
        await AssertAuditRefusedAsync(
            scenario.DispatcherUserId, [scenario.OrganizationId], audit, "DSP_OPERATOR_OWNER_ORDER_REFUSED");
    }

    [PostgreSqlContractFact]
    public async Task Operator_owner_outbox_functions_are_executable_only_by_the_api_role()
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT r.name, f.signature, has_function_privilege(r.name, f.signature, 'EXECUTE')
            FROM unnest(@roles::text[]) r(name)
            CROSS JOIN unnest(@functions::text[]) f(signature)
            ORDER BY 1, 2
            """);
        command.Parameters.AddWithValue(
            "roles",
            new[]
            {
                "public", "paqueteria_app", "paqueteria_worker", "paqueteria_bootstrap", "paqueteria_migrator",
                "paqueteria_outbox_executor", "paqueteria_maintenance", "paqueteria_master_data_loader",
            });
        command.Parameters.AddWithValue("functions", AddOperatorOwnerOutboxExecutor.OwnedFunctions.ToArray());
        await using (var reader = await command.ExecuteReaderAsync())
        {
            var rows = 0;
            while (await reader.ReadAsync())
            {
                rows++;
                Assert.Equal(reader.GetString(0) == "paqueteria_app", reader.GetBoolean(2));
            }

            Assert.Equal(16, rows);
        }

        // The Worker credential gets permission denied before any check runs.
        await using var worker = await TenantTransaction.BeginAsync(
            fixture.WorkerDataSource, "paqueteria_worker", Guid.NewGuid(), [Guid.NewGuid()]);
        var denied = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOutboxCallAsync(worker, OperatorOutboxRow.Empty));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        Assert.Contains("permission denied", denied.MessageText, StringComparison.Ordinal);

        // The executor grants and functions match the AI-18 contract and the Dispatch lane, and the lane is recorded.
        Assert.Equal(AddOperatorOwnerOutboxExecutor.ExecutorColumnGrants, DatabaseBaselineAssertions.OperatorOutboxExecutorGrants);
        Assert.Equal(AddOperatorOwnerOutboxExecutor.OwnedFunctions, DatabaseBaselineAssertions.OperatorOutboxFunctions);
        Assert.Equal(AddOperatorOwnerOutboxExecutor.MigrationId, E002OperatorOutboxStateReader.MigrationId);
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        Assert.True(await E002OperatorOutboxStateReader.IsAppliedAsync(connection));
        await using var transaction = await connection.BeginTransactionAsync();
        await DatabaseBaselineAssertions.AssertOperatorOutboxExecutorInstalledAsync(connection, transaction);
    }

    [PostgreSqlContractFact]
    public async Task Operator_owner_outbox_down_restores_the_fail_closed_path_and_up_reinstalls_the_boundary()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var operatorId = Guid.NewGuid();
        await MakeOperatorAsync(scenario, operatorId);
        try
        {
            var created = await CreateAssignmentService(fixture.AppDataSource)
                .CreateOwnDriverAssignmentAsync(Command(scenario) with { OrganizationId = operatorId }, default);
            var status = await ReadOutboxRowAsync(scenario.OrderId, "orders.status-changed");

            // Down and Up inside one transaction that is rolled back, so the shared fixture keeps the lane.
            await using var connection = new NpgsqlConnection(fixture.DeploymentConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, AddOperatorOwnerOutboxExecutor.DownSql);
            await ExecuteAsync(connection, transaction, "RESET ROLE");
            Assert.False(await ScalarAsync<bool>(connection, transaction,
                $"SELECT to_regprocedure('{AddOperatorOwnerOutboxExecutor.OutboxFunctionSignature}') IS NOT NULL"));
            Assert.False(await ScalarAsync<bool>(connection, transaction,
                $"SELECT to_regprocedure('{AddOperatorOwnerOutboxExecutor.AuditFunctionSignature}') IS NOT NULL"));
            // Role and grants stay (AI-18 declares them) and the baseline boundary still holds without the functions.
            Assert.True(await ScalarAsync<bool>(connection, transaction,
                "SELECT to_regrole('paqueteria_operator_outbox_executor') IS NOT NULL"));
            await new DatabaseBaselineAssertions().AssertAsync(connection, transaction);

            // Without the functions the operator's direct owner-tagged insert is refused by RLS, as before the lane.
            await ExecuteAsync(connection, transaction, "SAVEPOINT direct_insert");
            await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_app");
            await ExecuteAsync(
                connection,
                transaction,
                $"SELECT set_config('app.current_user_id','{scenario.DispatcherUserId:D}',true), " +
                $"set_config('app.current_org_ids','{{{operatorId:D}}}',true)");
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                connection,
                transaction,
                $$"""
                INSERT INTO platform.outbox_events(
                  id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,
                  priority,status,attempts,available_at,created_at)
                VALUES ('{{Guid.NewGuid():D}}','{{scenario.OrganizationId:D}}','{}','orders.status-changed','Order',
                  '{{scenario.OrderId:D}}',2,'{}',50,'PENDING',0,now(),now())
                """));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
            Assert.Contains("row-level security", refused.MessageText, StringComparison.Ordinal);
            await ExecuteAsync(connection, transaction, "ROLLBACK TO SAVEPOINT direct_insert");
            await ExecuteAsync(connection, transaction, "RESET ROLE");

            // Up reinstalls both functions with the exact boundary, and refuses a widened executor.
            await ExecuteAsync(connection, transaction, AddOperatorOwnerOutboxExecutor.UpSql);
            await ExecuteAsync(connection, transaction, "RESET ROLE");
            await DatabaseBaselineAssertions.AssertOperatorOutboxExecutorInstalledAsync(connection, transaction);
            await ExecuteAsync(connection, transaction, "SAVEPOINT widened");
            await ExecuteAsync(connection, transaction,
                "GRANT UPDATE (status) ON platform.outbox_events TO paqueteria_operator_outbox_executor");
            var widened = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, transaction, AddOperatorOwnerOutboxExecutor.UpSql));
            Assert.Contains("privileges differ", widened.MessageText, StringComparison.Ordinal);
            await ExecuteAsync(connection, transaction, "ROLLBACK TO SAVEPOINT widened");
            await ExecuteAsync(connection, transaction, "SAVEPOINT member");
            await ExecuteAsync(connection, transaction, "GRANT paqueteria_operator_outbox_executor TO paqueteria_app");
            var assertion = await Assert.ThrowsAsync<DatabaseAssertionException>(() =>
                DatabaseBaselineAssertions.AssertOperatorOutboxExecutorInstalledAsync(connection, transaction));
            Assert.Contains(assertion.Violations, violation => violation.Contains("reachable from paqueteria_app", StringComparison.Ordinal));
            await ExecuteAsync(connection, transaction, "ROLLBACK TO SAVEPOINT member");
            await transaction.RollbackAsync();
            Assert.NotEqual(Guid.Empty, created.Id);
        }
        finally
        {
            await RemoveOperatorAsync(scenario, operatorId);
        }
    }

    [PostgreSqlContractFact]
    public async Task Operator_owner_outbox_realtime_authorizes_the_operator_driver_and_never_a_third_driver()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var operatorId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();
        var thirdUserId = Guid.NewGuid();
        var thirdDriverId = Guid.NewGuid();
        await MakeOperatorAsync(scenario, operatorId);
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
            VALUES (@third,'DSP third','DSP third','BUSINESS');
            INSERT INTO identity.users(id,identity_subject,status,created_at)
            VALUES (@third_user,@subject,'ACTIVE',now());
            INSERT INTO organizations.organization_memberships(
              id,user_id,organization_id,role,status,is_default,granted_at)
            VALUES (gen_random_uuid(),@third_user,@third,'DRIVER','ACTIVE',true,now());
            INSERT INTO drivers.driver_profiles(
              id,user_id,org_id,home_city_id,driver_type,vehicle_type,status,created_at)
            VALUES (@third_driver,@third_user,@third,@city,'OWN','MOTORCYCLE','ACTIVE',now());
            """,
            P("third", thirdId),
            P("third_user", thirdUserId),
            P("subject", $"dsp-op-third-driver-{thirdUserId:N}"),
            P("third_driver", thirdDriverId),
            P("city", scenario.CityId));
        try
        {
            var created = await CreateAssignmentService(fixture.AppDataSource)
                .CreateOwnDriverAssignmentAsync(Command(scenario) with { OrganizationId = operatorId }, default);
            await using var connections = new RealtimeWorkerConnectionFactory(fixture.WorkerConnectionString);
            var evidence = new PostgreSqlRealtimeOutboxEvidenceReader(connections);

            // The owner's operations audience reads the assignment; the operator's driver is the driver audience.
            var assignment = await evidence.ReadAssignmentAsync(scenario.OrganizationId, created.Id, default);
            Assert.NotNull(assignment);
            Assert.Equal(operatorId, assignment.OperatorOrganizationId);
            Assert.Equal(scenario.DriverId, assignment.DriverId);
            Assert.True(assignment.DriverAudienceAuthorized);
            Assert.True(await evidence.IsDriverAudienceAuthorizedAsync(
                scenario.OrganizationId, scenario.OrderId, created.Id, scenario.DriverId, default));
            var orderEvent = await ReadOutboxRowAsync(scenario.OrderId, "orders.status-changed");
            Assert.NotNull(await evidence.ReadOrderEventAsync(
                scenario.OrganizationId, Guid.Parse(orderEvent.PayloadValue("order_event_id")), default));

            // The operator's context is not an owner audience: it reads nothing as the owner of the assignment.
            Assert.Null(await evidence.ReadAssignmentAsync(operatorId, created.Id, default));

            // A third organization's driver is never authorized, neither as the named driver nor on the assignment.
            Assert.False(await evidence.IsDriverAudienceAuthorizedAsync(
                scenario.OrganizationId, scenario.OrderId, created.Id, thirdDriverId, default));
            await scenario.ExecuteAdminAsync(
                "UPDATE dispatch.assignments SET driver_id=@third_driver WHERE id=@assignment;",
                P("third_driver", thirdDriverId),
                P("assignment", created.Id));
            Assert.False(await evidence.IsDriverAudienceAuthorizedAsync(
                scenario.OrganizationId, scenario.OrderId, created.Id, thirdDriverId, default));
            var foreign = await evidence.ReadAssignmentAsync(scenario.OrganizationId, created.Id, default);
            Assert.NotNull(foreign);
            Assert.False(foreign.DriverAudienceAuthorized);
            await scenario.ExecuteAdminAsync(
                "UPDATE dispatch.assignments SET driver_id=@driver WHERE id=@assignment;",
                P("driver", scenario.DriverId),
                P("assignment", created.Id));

            // The operator's driver loses the audience with its DRIVER membership.
            await scenario.ExecuteAdminAsync(
                """
                UPDATE organizations.organization_memberships SET status='SUSPENDED'
                WHERE user_id=@driver_user AND organization_id=@operator;
                """,
                P("driver_user", scenario.DriverUserId),
                P("operator", operatorId));
            Assert.False(await evidence.IsDriverAudienceAuthorizedAsync(
                scenario.OrganizationId, scenario.OrderId, created.Id, scenario.DriverId, default));
            var suspended = await evidence.ReadAssignmentAsync(scenario.OrganizationId, created.Id, default);
            Assert.NotNull(suspended);
            Assert.False(suspended.DriverAudienceAuthorized);
        }
        finally
        {
            await scenario.ExecuteAdminAsync(
                """
                DELETE FROM drivers.driver_profiles WHERE id=@third_driver;
                DELETE FROM organizations.organization_memberships WHERE organization_id=@third;
                DELETE FROM identity.users WHERE id=@third_user;
                DELETE FROM organizations.organizations WHERE id=@third;
                """,
                P("third", thirdId),
                P("third_user", thirdUserId),
                P("third_driver", thirdDriverId));
            await RemoveOperatorAsync(scenario, operatorId);
        }
    }

    private static async Task MakeOperatorAsync(DispatchScenario scenario, Guid operatorId) =>
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
            VALUES (@operator,'DSP operator','DSP operator','ALLY');
            INSERT INTO organizations.organization_memberships(
              id,user_id,organization_id,role,status,is_default,granted_at)
            VALUES (gen_random_uuid(),@dispatcher,@operator,'DISPATCHER','ACTIVE',false,now()),
                   (gen_random_uuid(),@driver_user,@operator,'DRIVER','ACTIVE',false,now());
            DELETE FROM organizations.organization_memberships
            WHERE user_id=@driver_user AND organization_id=@org;
            UPDATE drivers.driver_profiles SET org_id=@operator WHERE id=@driver;
            UPDATE drivers.driver_documents SET org_id=@operator WHERE driver_id=@driver;
            UPDATE orders.orders SET operator_org_id=@operator WHERE id=@order;
            UPDATE orders.package_items SET operator_org_id=@operator WHERE order_id=@order;
            """,
            P("operator", operatorId),
            P("org", scenario.OrganizationId),
            P("dispatcher", scenario.DispatcherUserId),
            P("driver_user", scenario.DriverUserId),
            P("driver", scenario.DriverId),
            P("order", scenario.OrderId));

    private async Task RemoveOperatorAsync(DispatchScenario scenario, Guid operatorId)
    {
        // Append-only order events and audit rows are removed only as the migrator (test cleanup).
        await using (var migrator = await TenantTransaction.BeginAsync(
            fixture.AdminDataSource, "paqueteria_migrator", scenario.DispatcherUserId, [scenario.OrganizationId]))
        {
            await using var events = new NpgsqlCommand(
                """
                DELETE FROM orders.order_events WHERE order_id=@order;
                DELETE FROM platform.audit_logs WHERE org_id=@operator;
                """,
                migrator.Connection,
                migrator.Transaction);
            events.Parameters.AddWithValue("order", scenario.OrderId);
            events.Parameters.AddWithValue("operator", operatorId);
            await events.ExecuteNonQueryAsync();
            await migrator.CommitAsync();
        }

        await scenario.ExecuteAdminAsync(
            """
            DELETE FROM dispatch.assignments WHERE order_id=@order;
            DELETE FROM platform.idempotency_keys WHERE owner_org_id=@operator;
            UPDATE orders.package_items SET operator_org_id=NULL WHERE order_id=@order;
            UPDATE orders.orders SET operator_org_id=NULL WHERE id=@order;
            UPDATE drivers.driver_documents SET org_id=@org WHERE driver_id=@driver;
            UPDATE drivers.driver_profiles SET org_id=@org WHERE id=@driver;
            DELETE FROM organizations.organization_memberships WHERE organization_id=@operator;
            DELETE FROM organizations.organizations WHERE id=@operator;
            """,
            P("operator", operatorId),
            P("org", scenario.OrganizationId),
            P("driver", scenario.DriverId),
            P("order", scenario.OrderId));
    }

    private async Task<long> CountAuditVisibleAsync(Guid userId, Guid organizationId, Guid orderId, Guid assignmentId)
    {
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource, "paqueteria_app", userId, [organizationId]);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM platform.audit_logs WHERE entity_id IN (@order,@assignment)",
            tenant.Connection,
            tenant.Transaction);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("assignment", assignmentId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<OperatorOutboxRow> ReadOutboxRowAsync(Guid orderId, string topic)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT owner_org_id,tenant_context::text,topic,aggregate_type,aggregate_id,aggregate_version,payload::text,
                   priority,available_at,created_at
            FROM platform.outbox_events
            WHERE aggregate_id=@order AND topic=@topic
            """);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("topic", topic);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var row = new OperatorOutboxRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetGuid(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetInt16(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetFieldValue<DateTimeOffset>(9));
        Assert.False(await reader.ReadAsync());
        return row;
    }

    private async Task<OperatorAuditRow> ReadAuditRowAsync(Guid organizationId, string action, Guid entityId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted::text,occurred_at
            FROM platform.audit_logs
            WHERE org_id=@org AND action=@action AND entity_id=@entity
            """);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entity", entityId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var row = new OperatorAuditRow(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetGuid(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7));
        Assert.False(await reader.ReadAsync());
        return row;
    }

    private async Task AssertOutboxRefusedAsync(Guid userId, Guid[] organizations, OperatorOutboxRow row, string code)
    {
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource, "paqueteria_app", userId, organizations);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOutboxCallAsync(tenant, row));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        Assert.Equal(code, exception.MessageText);
    }

    private async Task AssertAuditRefusedAsync(Guid userId, Guid[] organizations, OperatorAuditRow row, string code)
    {
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource, "paqueteria_app", userId, organizations);
        await using var command = new NpgsqlCommand(OperatorAuditCall, tenant.Connection, tenant.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter<Guid>("org", NpgsqlDbType.Uuid) { TypedValue = row.Organization });
        command.Parameters.Add(new NpgsqlParameter<Guid>("actor", NpgsqlDbType.Uuid) { TypedValue = row.ActorId });
        command.Parameters.Add(new NpgsqlParameter<string>("action", NpgsqlDbType.Text) { TypedValue = row.Action });
        command.Parameters.Add(new NpgsqlParameter<string>("entity_type", NpgsqlDbType.Text) { TypedValue = row.EntityType });
        command.Parameters.Add(new NpgsqlParameter<Guid>("entity_id", NpgsqlDbType.Uuid) { TypedValue = row.EntityId });
        command.Parameters.Add(new NpgsqlParameter<string?>("request_id", NpgsqlDbType.Text) { TypedValue = row.RequestId });
        command.Parameters.Add(new NpgsqlParameter<string>("payload", NpgsqlDbType.Jsonb) { TypedValue = row.Payload });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("occurred", NpgsqlDbType.TimestampTz) { TypedValue = row.OccurredAt });
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        Assert.Equal(code, exception.MessageText);
    }

    private static async Task ExecuteOutboxCallAsync(TenantTransaction tenant, OperatorOutboxRow row)
    {
        await using var command = new NpgsqlCommand(OperatorOutboxCall, tenant.Connection, tenant.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter<Guid>("owner", NpgsqlDbType.Uuid) { TypedValue = row.Owner });
        command.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Jsonb) { TypedValue = row.Tenant });
        command.Parameters.Add(new NpgsqlParameter<string>("topic", NpgsqlDbType.Text) { TypedValue = row.Topic });
        command.Parameters.Add(new NpgsqlParameter<string>("aggregate_type", NpgsqlDbType.Text) { TypedValue = row.AggregateType });
        command.Parameters.Add(new NpgsqlParameter<Guid>("order", NpgsqlDbType.Uuid) { TypedValue = row.OrderId });
        command.Parameters.Add(new NpgsqlParameter<int>("version", NpgsqlDbType.Integer) { TypedValue = row.Version });
        command.Parameters.Add(new NpgsqlParameter<string>("payload", NpgsqlDbType.Jsonb) { TypedValue = row.Payload });
        command.Parameters.Add(new NpgsqlParameter<short>("priority", NpgsqlDbType.Smallint) { TypedValue = row.Priority });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("available", NpgsqlDbType.TimestampTz) { TypedValue = row.AvailableAt });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created", NpgsqlDbType.TimestampTz) { TypedValue = row.CreatedAt });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed record OperatorOutboxRow(
        Guid Owner,
        string Tenant,
        string Topic,
        string AggregateType,
        Guid OrderId,
        int Version,
        string Payload,
        short Priority,
        DateTimeOffset AvailableAt,
        DateTimeOffset CreatedAt)
    {
        public static OperatorOutboxRow Empty { get; } = new(
            Guid.NewGuid(), "{}", "orders.status-changed", "Order", Guid.NewGuid(), 1, "{}", 50,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        public string PayloadValue(string name)
        {
            using var document = System.Text.Json.JsonDocument.Parse(Payload);
            return document.RootElement.GetProperty(name).GetString()!;
        }
    }

    private sealed record OperatorAuditRow(
        Guid Organization,
        Guid ActorId,
        string Action,
        string EntityType,
        Guid EntityId,
        string? RequestId,
        string Payload,
        DateTimeOffset OccurredAt);
}
