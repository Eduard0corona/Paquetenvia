using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Auditing;
using Paqueteria.Infrastructure.Database.Outbox;
using Paqueteria.Infrastructure.Observability;

namespace Dispatch.Infrastructure.Assignments;

/// <summary>
/// DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03 (project owner: "Función segura a nombre del dueño"; "Sí, el
/// dueño lo ve"). When the operator organization of an order (distinct from its owner) assigns its own driver,
/// the transaction runs with the operator as the only tenant, so RLS refuses the owner-tagged outbox and audit
/// rows the owner's operations audience needs. Those rows are written only through the two SECURITY DEFINER
/// functions of <c>paqueteria_operator_outbox_executor</c> (Dispatch lane
/// <c>20261003000100_AddOperatorOwnerOutboxExecutor</c>), inside the caller's transaction, with every value
/// supplied here and nothing read back. The functions verify the caller's tenant context, actor, order, order event,
/// assignment, topic, action and payload before writing and raise 42501 otherwise, which rolls the whole
/// assignment back. The owner path keeps inserting directly under RLS.
/// <para>
/// OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13: the outbox definer keeps its published signature, so an application version
/// that predates the <c>trace_parent</c> column keeps calling it during a deploy. The producing trace context reaches it
/// the way the tenant and the actor do: the transaction-local setting <c>app.trace_parent</c>
/// (<see cref="OutboxTraceContextColumn.TransactionSetting"/>), set here with <c>set_config(..., true)</c> right before each
/// call, empty outside a trace. The definer stores it in the row it inserts.
/// </para>
/// </summary>
internal static class OperatorOwnerEventWriter
{
    /// <summary>OBS-003: the producing trace context for the definer, transaction-local like the tenant context.</summary>
    internal const string TraceContextSql =
        "SELECT pg_catalog.set_config('app.trace_parent', @trace_parent, true)";

    internal const string OutboxSql =
        """
        SELECT security.append_operator_order_outbox(
          @id,@owner,@tenant,@topic,'Order',@order,@version,@payload,@priority,@available,@created)
        """;

    internal const string AuditSql =
        """
        SELECT security.append_operator_order_audit(
          @id,@org,@actor,@action,@entity_type,@entity_id,@request_id,@payload,@occurred)
        """;

    private const short Priority = 50;

    public static async Task AppendOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        Guid outboxId,
        Guid ownerOrganizationId,
        string tenantContextJson,
        string topic,
        Guid orderId,
        int aggregateVersion,
        string payloadJson,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using (var trace = new NpgsqlCommand(TraceContextSql, connection, transaction)
        {
            CommandTimeout = commandTimeoutSeconds,
        })
        {
            trace.Parameters.Add(new NpgsqlParameter<string>(OutboxTraceContextColumn.ParameterName, NpgsqlDbType.Text)
            {
                TypedValue = OutboxTraceContext.Current() ?? string.Empty,
            });
            await trace.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(OutboxSql, connection, transaction)
        {
            CommandTimeout = commandTimeoutSeconds,
        };
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = outboxId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("owner", NpgsqlDbType.Uuid) { TypedValue = ownerOrganizationId });
        command.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Jsonb) { TypedValue = tenantContextJson });
        command.Parameters.Add(new NpgsqlParameter<string>("topic", NpgsqlDbType.Text) { TypedValue = topic });
        command.Parameters.Add(new NpgsqlParameter<Guid>("order", NpgsqlDbType.Uuid) { TypedValue = orderId });
        command.Parameters.Add(new NpgsqlParameter<int>("version", NpgsqlDbType.Integer) { TypedValue = aggregateVersion });
        command.Parameters.Add(new NpgsqlParameter<string>("payload", NpgsqlDbType.Jsonb) { TypedValue = payloadJson });
        command.Parameters.Add(new NpgsqlParameter<short>("priority", NpgsqlDbType.Smallint) { TypedValue = Priority });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("available", NpgsqlDbType.TimestampTz) { TypedValue = occurredAt });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created", NpgsqlDbType.TimestampTz) { TypedValue = occurredAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task AppendAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        AuditEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.ActorId is not { } actorId)
        {
            throw new InvalidOperationException("An operator audit row requires the acting user.");
        }

        await using var command = new NpgsqlCommand(AuditSql, connection, transaction)
        {
            CommandTimeout = commandTimeoutSeconds,
        };
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = entry.AuditId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("org", NpgsqlDbType.Uuid) { TypedValue = entry.OrganizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("actor", NpgsqlDbType.Uuid) { TypedValue = actorId });
        command.Parameters.Add(new NpgsqlParameter<string>("action", NpgsqlDbType.Text) { TypedValue = entry.Action });
        command.Parameters.Add(new NpgsqlParameter<string>("entity_type", NpgsqlDbType.Text) { TypedValue = entry.EntityType });
        command.Parameters.Add(new NpgsqlParameter<Guid>("entity_id", NpgsqlDbType.Uuid) { TypedValue = entry.EntityId });
        command.Parameters.Add(new NpgsqlParameter<string?>("request_id", NpgsqlDbType.Text) { TypedValue = entry.RequestId });
        command.Parameters.Add(new NpgsqlParameter<string>("payload", NpgsqlDbType.Jsonb) { TypedValue = entry.Payload.Json });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("occurred", NpgsqlDbType.TimestampTz) { TypedValue = entry.OccurredAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
