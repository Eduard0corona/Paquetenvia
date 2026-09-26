using Dispatch.Application.Assignments;
using Npgsql;
using NpgsqlTypes;

namespace Dispatch.Infrastructure.Assignments;

/// <summary>
/// D8 reaction executed as <c>paqueteria_worker</c> inside one tenant transaction scoped to the
/// event owner (FORCE RLS applies). It only updates <c>dispatch.assignments</c>; it never reads or
/// mutates the outbox and never touches Orders tables beyond RLS-visible identifiers.
/// </summary>
/// <remarks>
/// Not wired to a consumer yet: <c>orders.status-changed</c> is exclusively routed to the REALTIME
/// lane by <c>security.resolve_outbox_consumer</c>, and the Worker holds no claim function for a
/// Dispatch lane. Wiring requires the normative change proposed with fix/e-assignment-lifecycle
/// (DISPATCH consumer lane plus claim/requeue functions owned by paqueteria_outbox_executor).
/// </remarks>
public sealed class PostgreSqlAssignmentLifecycleReactor(NpgsqlDataSource dataSource) : IAssignmentLifecycleReactor
{
    public async Task<int> ReactAsync(OrderStatusChangedFact fact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var closure = AssignmentLifecyclePolicy.Resolve(fact.PreviousStatus, fact.NewStatus);
        if (closure == AssignmentClosure.None)
        {
            return 0;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var context = new NpgsqlCommand(
            """
            SET LOCAL ROLE paqueteria_worker;
            SELECT set_config('app.current_user_id','',true),
                   set_config('app.current_org_ids',@organization_ids::uuid[]::text,true);
            """,
            connection,
            transaction))
        {
            context.Parameters.Add(new NpgsqlParameter<Guid[]>(
                "organization_ids",
                NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                TypedValue = [fact.OwnerOrganizationId],
            });
            await context.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var update = new NpgsqlCommand(
            """
            UPDATE dispatch.assignments
               SET status=@target
             WHERE order_id=@order
               AND owner_org_id=@owner
               AND status IN ('ACCEPTED','ACTIVE')
               AND created_at<=@occurred_at
               AND (@assignment::uuid IS NULL OR id=@assignment::uuid)
            """,
            connection,
            transaction);
        update.Parameters.Add(new NpgsqlParameter<string>("target", NpgsqlDbType.Text) { TypedValue = closure.ToContractValue() });
        update.Parameters.Add(new NpgsqlParameter<Guid>("order", NpgsqlDbType.Uuid) { TypedValue = fact.OrderId });
        update.Parameters.Add(new NpgsqlParameter<Guid>("owner", NpgsqlDbType.Uuid) { TypedValue = fact.OwnerOrganizationId });
        update.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("occurred_at", NpgsqlDbType.TimestampTz) { TypedValue = fact.OccurredAt });
        update.Parameters.Add(new NpgsqlParameter("assignment", NpgsqlDbType.Uuid)
        {
            Value = fact.AssignmentId is { } assignmentId ? assignmentId : DBNull.Value,
        });
        var closed = await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return closed;
    }
}
