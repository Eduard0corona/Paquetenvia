using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.Infrastructure.Auditing;

public sealed class PostgreSqlAppendOnlyAuditWriter(TenantDatabaseExecutionState executionState)
    : IAppendOnlyAuditWriter
{
    private const string InsertSql =
        "INSERT INTO platform.audit_logs " +
        "(id, org_id, actor_id, action, entity_type, entity_id, request_id, payload_redacted, occurred_at) " +
        "VALUES (@id, @org_id, @actor_id, @action, @entity_type, @entity_id, @request_id, @payload_redacted, @occurred_at);";

    public async Task WriteAsync(
        DbConnection connection,
        DbTransaction transaction,
        AuditEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(entry);

        if (connection is not NpgsqlConnection npgsqlConnection ||
            transaction is not NpgsqlTransaction npgsqlTransaction ||
            !ReferenceEquals(npgsqlTransaction.Connection, npgsqlConnection))
        {
            throw new InvalidOperationException("Audit writes require the active Npgsql connection and transaction.");
        }

        if (!executionState.IsApplied || !executionState.OrganizationIds.Contains(entry.OrganizationId))
        {
            throw new InvalidOperationException("Audit writes require an applied tenant context for the audited organization.");
        }

        if (entry.ActorId is { } actorId && executionState.UserId != actorId)
        {
            throw new InvalidOperationException("The audit actor must match the active internal user context.");
        }

        await using var command = new NpgsqlCommand(InsertSql, npgsqlConnection, npgsqlTransaction)
        {
            CommandTimeout = 30,
        };
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = entry.AuditId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("org_id", NpgsqlDbType.Uuid) { TypedValue = entry.OrganizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid?>("actor_id", NpgsqlDbType.Uuid) { TypedValue = entry.ActorId });
        command.Parameters.Add(new NpgsqlParameter<string>("action", NpgsqlDbType.Text) { TypedValue = entry.Action });
        command.Parameters.Add(new NpgsqlParameter<string>("entity_type", NpgsqlDbType.Text) { TypedValue = entry.EntityType });
        command.Parameters.Add(new NpgsqlParameter<Guid>("entity_id", NpgsqlDbType.Uuid) { TypedValue = entry.EntityId });
        command.Parameters.Add(new NpgsqlParameter<string?>("request_id", NpgsqlDbType.Text) { TypedValue = entry.RequestId });
        command.Parameters.Add(new NpgsqlParameter<string>("payload_redacted", NpgsqlDbType.Jsonb) { TypedValue = entry.Payload.Json });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("occurred_at", NpgsqlDbType.TimestampTz) { TypedValue = entry.OccurredAt });

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The append-only audit record was not persisted.");
        }
    }
}
