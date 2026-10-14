using System.Diagnostics;
using Dispatch.Application.Assignments;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// OBS-003-WORKER-AUDIT-TRACE-2026-10-14 (project owner, Q1: "Sí, en un PR aparte (Recommended)"), end to end on
/// PostgreSQL: a request with its own W3C trace unassigns an order through the real ORD-002 transition, which writes
/// its audit row with that trace id as <c>request_id</c> and the Dispatch reaction row with the request's context in
/// <c>trace_parent</c>; the Worker then claims and processes the row inside its own dispatch cycle, and the
/// <c>ASSIGNMENT_CLOSED</c> audit row it writes carries the same trace id, never the cycle's. One query on that id
/// finds both audit rows.
/// </summary>
public sealed partial class DispatchPostgreSqlContractTests
{
    [PostgreSqlContractFact]
    public async Task Worker_assignment_closure_audit_carries_the_trace_id_of_the_request_that_wrote_the_message()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var assignment = await CreateAssignmentService(fixture.AppDataSource)
            .CreateOwnDriverAssignmentAsync(Command(scenario), default);
        Assert.Equal("ACCEPTED", assignment.Status);
        var version = await AdminScalarAsync<int>(
            "SELECT version FROM orders.orders WHERE id=@order", P("order", scenario.OrderId));

        // The unassign request: a new W3C trace whose id the API writes as request_id (HttpContext.TraceIdentifier).
        string requestTraceId;
        Activity.Current = null;
        using (var request = new Activity("Obs003.SyntheticUnassignRequest").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            requestTraceId = TraceCorrelation.TraceIdOf(request)!;
            var unassigned = await CreateOrderTransitionService(OccurredAt.AddMinutes(5)).TransitionAsync(
                TransitionCommand(scenario, scenario.OrderId, "READY_FOR_PICKUP", version, null) with
                {
                    RequestId = requestTraceId,
                },
                default);
            Assert.Equal("READY_FOR_PICKUP", unassigned.Status);
        }

        Activity.Current = null;
        Assert.True(TraceCorrelation.IsTraceId(requestTraceId));
        var reaction = await AdminScalarAsync<Guid>(
            """
            SELECT id FROM platform.outbox_events
            WHERE aggregate_id=@order AND topic='dispatch.order-status-reaction-requested' AND status='PENDING'
              AND substr(trace_parent, 4, 32)=@trace
            """,
            P("order", scenario.OrderId),
            P("trace", requestTraceId));

        // The Worker claims and processes it inside its own dispatch cycle, a different root trace.
        string cycleTraceId;
        TraceCorrelation.ConfigureProcess();
        using (var cycle = TraceCorrelation.StartOutboxDispatchCycle(OutboxLanes.Dispatch))
        {
            cycleTraceId = cycle.TraceId.ToHexString();
            Assert.Equal(AssignmentReactionOutcome.Closed, await ProcessAsync(reaction));
        }

        Activity.Current = null;
        Assert.NotEqual(requestTraceId, cycleTraceId);
        Assert.Equal("CANCELLED", await ReadAssignmentStatusAsync(assignment.Id));
        Assert.Equal(
            requestTraceId,
            await AdminScalarAsync<string>(
                "SELECT request_id FROM platform.audit_logs WHERE action='ASSIGNMENT_CLOSED' AND entity_id=@assignment",
                P("assignment", assignment.Id)));

        // One identifier from the request through the outbox to the Worker's audit row, and none with the cycle's.
        await using (var command = fixture.AdminDataSource.CreateCommand(
                         """
                         SELECT action, entity_type, entity_id FROM platform.audit_logs
                         WHERE request_id=@trace ORDER BY action, entity_type
                         """))
        {
            command.Parameters.AddWithValue("trace", requestTraceId);
            var rows = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add($"{reader.GetString(0)} {reader.GetString(1)} {reader.GetGuid(2):D}");
            }

            Assert.Equal(
                [
                    $"ASSIGNMENT_CLOSED Assignment {assignment.Id:D}",
                    $"ORDER_STATUS_CHANGED Order {scenario.OrderId:D}",
                ],
                rows);
        }

        Assert.Equal(
            0L,
            await AdminScalarAsync<long>(
                "SELECT count(*) FROM platform.audit_logs WHERE request_id=@trace", P("trace", cycleTraceId)));
    }
}
