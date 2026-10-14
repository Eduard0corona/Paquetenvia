using System.Diagnostics;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13 (project owner, Q-PR7: "Columna nueva (Recommended)"): a real assignment
/// writes the producing trace context into every outbox row of its transaction, through both producer paths: the
/// owner's direct INSERT under RLS (<c>@trace_parent</c>) and the operator outbox definer (the transaction-local
/// <c>app.trace_parent</c>, its published signature unchanged). Outside any trace the rows keep NULL.
/// </summary>
public sealed partial class DispatchPostgreSqlContractTests
{
    [PostgreSqlContractFact]
    public async Task Outbox_trace_context_is_written_by_the_owner_insert_and_by_the_operator_definer()
    {
        // Owner path, inside a sampled trace.
        await using (var owner = await DispatchScenario.CreateAsync(fixture))
        {
            string expected;
            using (StartProducer(sampled: true))
            {
                expected = OutboxTraceContext.Current()!;
                await CreateAssignmentService(fixture.AppDataSource).CreateOwnDriverAssignmentAsync(Command(owner), default);
            }

            Assert.Equal(Context(expected), await OutboxTraceSummaryAsync(owner.OrderId));
            Assert.EndsWith("-01", expected, StringComparison.Ordinal);
        }

        // Operator path (the definer), inside a trace that is not sampled.
        await using (var operated = await DispatchScenario.CreateAsync(fixture))
        {
            var operatorId = Guid.NewGuid();
            await MakeOperatorAsync(operated, operatorId);
            try
            {
                string expected;
                using (StartProducer(sampled: false))
                {
                    expected = OutboxTraceContext.Current()!;
                    await CreateAssignmentService(fixture.AppDataSource)
                        .CreateOwnDriverAssignmentAsync(Command(operated) with { OrganizationId = operatorId }, default);
                }

                Assert.Equal(Context(expected), await OutboxTraceSummaryAsync(operated.OrderId));
                Assert.EndsWith("-00", expected, StringComparison.Ordinal);
            }
            finally
            {
                await RemoveOperatorAsync(operated, operatorId);
            }
        }

        // Operator path outside any trace: the definer stores NULL (the setting is empty), as for an application
        // version that predates the column and never sets it.
        await using (var untraced = await DispatchScenario.CreateAsync(fixture))
        {
            var operatorId = Guid.NewGuid();
            await MakeOperatorAsync(untraced, operatorId);
            try
            {
                Activity.Current = null;
                await CreateAssignmentService(fixture.AppDataSource)
                    .CreateOwnDriverAssignmentAsync(Command(untraced) with { OrganizationId = operatorId }, default);
                Assert.Equal("NULL", await OutboxTraceSummaryAsync(untraced.OrderId));
            }
            finally
            {
                await RemoveOperatorAsync(untraced, operatorId);
            }
        }
    }

    /// <summary>A W3C producer activity, as a request or job would have, with the requested sampled flag.</summary>
    private static Activity StartProducer(bool sampled)
    {
        var activity = new Activity("Obs003.SyntheticProducer").SetIdFormat(ActivityIdFormat.W3C).Start();
        activity.ActivityTraceFlags = sampled ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None;
        return activity;
    }

    /// <summary>The trace id and flags of a stored context (the span id is the producer's own and may be a child's).</summary>
    private static string Context(string traceParent) => traceParent.Substring(3, 32) + ":" + traceParent[^2..];

    /// <summary>The distinct trace id and flags of the order's outbox rows (NULL for none, NONE when there is no row).</summary>
    private async Task<string> OutboxTraceSummaryAsync(Guid orderId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT coalesce(
                     string_agg(DISTINCT coalesce(substr(trace_parent, 4, 32) || ':' || right(trace_parent, 2), 'NULL'), ','),
                     'NONE')
            FROM platform.outbox_events
            WHERE aggregate_id=@order
            """);
        command.Parameters.AddWithValue("order", orderId);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
