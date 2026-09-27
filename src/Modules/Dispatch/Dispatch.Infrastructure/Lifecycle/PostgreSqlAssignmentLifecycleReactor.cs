using System.Text.Json;
using Dispatch.Application.Assignments;
using Dispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Dispatch.Infrastructure.Lifecycle;

public interface IAssignmentLifecycleReactor
{
    Task<AssignmentReactionOutcome> ReactAsync(
        ClaimedDispatchOutboxMessage message,
        OrderStatusChangedFact fact,
        CancellationToken cancellationToken);
}

/// <summary>
/// D8 (<c>D8-DISPATCH-OUTBOX-CLOSURE</c>) reaction executed through the Worker tenant transaction
/// (<c>paqueteria_worker</c>, <c>set_config(..., true)</c> for the event owner after BEGIN), so FORCE RLS
/// applies. In that single transaction it closes the assignment the fact names, writes the
/// <c>AssignmentChanged</c> outbox row (<c>AI12-ASSIGNMENT-TERMINAL-STATES</c>) and the audit record
/// through the shared append-only writer, and settles the claimed DISPATCH row with its lease token.
/// A lost lease rolls everything back.
/// </summary>
/// <remarks>
/// Idempotency: only the assignment Orders found active under the order lock is eligible, and only while
/// it is still ACCEPTED/ACTIVE. A replay finds it closed and a newer assignment (for example the one
/// DSP-002 creates after a reschedule, <c>D8-REASSIGNMENT-NEW-ASSIGNMENT</c>) has a different id, so a
/// stale redelivery never closes it. This is not a sixth AI-13 §4 atomic flow: the transaction touches
/// Dispatch state plus its own outbox and audit rows only.
/// </remarks>
public sealed class PostgreSqlAssignmentLifecycleReactor(
    WorkerTenantTransactionContext<DispatchDbContext> transactionContext,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor,
    IClock clock) : IAssignmentLifecycleReactor
{
    public const string AssignmentChangedTopic = "dispatch.assignment-changed";
    public const string AuditAction = "ASSIGNMENT_CLOSED";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<AssignmentReactionOutcome> ReactAsync(
        ClaimedDispatchOutboxMessage message,
        OrderStatusChangedFact fact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(fact);
        try
        {
            // The claimed outbox row id is the synthetic system identity of this Worker transaction;
            // the audit record itself carries no actor.
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(message.Id, [fact.OwnerOrganizationId]),
                (dbContext, token) => ReactWithinTransactionAsync(dbContext, message, fact, token),
                cancellationToken);
        }
        catch (LeaseLostException)
        {
            return AssignmentReactionOutcome.LeaseLost;
        }
    }

    private async Task<AssignmentReactionOutcome> ReactWithinTransactionAsync(
        DispatchDbContext dbContext,
        ClaimedDispatchOutboxMessage message,
        OrderStatusChangedFact fact,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
        var closure = AssignmentLifecyclePolicy.Resolve(fact.PreviousStatus, fact.NewStatus);
        var outcome = AssignmentReactionOutcome.NoOp;
        if (closure != AssignmentClosure.None)
        {
            var target = closure.ToContractValue();
            var driverId = await CloseAsync(connection, transaction, fact, target, cancellationToken);
            if (driverId is { } closedDriverId)
            {
                await InsertAssignmentChangedAsync(connection, transaction, fact, closedDriverId, target, cancellationToken);
                await WriteAuditAsync(connection, transaction, message, fact, closedDriverId, target, cancellationToken);
                outcome = AssignmentReactionOutcome.Closed;
            }
        }

        if (!await PostgreSqlDispatchOutboxStore.SettleAsync(
                connection,
                transaction,
                message.Id,
                message.LeaseToken,
                "PROCESSED",
                null,
                null,
                cancellationToken))
        {
            throw new LeaseLostException();
        }

        return outcome;
    }

    private static async Task<Guid?> CloseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrderStatusChangedFact fact,
        string target,
        CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand(
            """
            UPDATE dispatch.assignments
               SET status=@target
             WHERE id=@assignment
               AND order_id=@order
               AND owner_org_id=@owner
               AND status IN ('ACCEPTED','ACTIVE')
            RETURNING driver_id
            """,
            connection,
            transaction);
        update.Parameters.Add(new NpgsqlParameter<string>("target", NpgsqlDbType.Text) { TypedValue = target });
        update.Parameters.Add(new NpgsqlParameter<Guid>("assignment", NpgsqlDbType.Uuid) { TypedValue = fact.AssignmentId });
        update.Parameters.Add(new NpgsqlParameter<Guid>("order", NpgsqlDbType.Uuid) { TypedValue = fact.OrderId });
        update.Parameters.Add(new NpgsqlParameter<Guid>("owner", NpgsqlDbType.Uuid) { TypedValue = fact.OwnerOrganizationId });
        return await update.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    private static async Task InsertAssignmentChangedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrderStatusChangedFact fact,
        Guid driverId,
        string target,
        CancellationToken cancellationToken)
    {
        var tenantContext = JsonSerializer.Serialize(new
        {
            organization_ids = new[] { fact.OwnerOrganizationId },
        }, JsonOptions);
        var payload = JsonSerializer.Serialize(new
        {
            schema_version = "assignment-changed-v1",
            order_id = fact.OrderId,
            assignment_id = fact.AssignmentId,
            driver_id = driverId,
            assignment_status = target,
            occurred_at = fact.OccurredAt,
        }, JsonOptions);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,
              priority,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,
              last_error,created_at,processed_at)
            VALUES (@id,@owner,@tenant,@topic,'Order',@order,@version,@payload,
                    50,'PENDING',0,@available,NULL,NULL,NULL,NULL,NULL,@created,NULL)
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter<Guid>("owner", NpgsqlDbType.Uuid) { TypedValue = fact.OwnerOrganizationId });
        command.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Jsonb) { TypedValue = tenantContext });
        command.Parameters.Add(new NpgsqlParameter<string>("topic", NpgsqlDbType.Text) { TypedValue = AssignmentChangedTopic });
        command.Parameters.Add(new NpgsqlParameter<Guid>("order", NpgsqlDbType.Uuid) { TypedValue = fact.OrderId });
        command.Parameters.Add(new NpgsqlParameter<int>("version", NpgsqlDbType.Integer) { TypedValue = fact.OrderVersion });
        command.Parameters.Add(new NpgsqlParameter<string>("payload", NpgsqlDbType.Jsonb) { TypedValue = payload });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("available", NpgsqlDbType.TimestampTz) { TypedValue = fact.OccurredAt });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created", NpgsqlDbType.TimestampTz) { TypedValue = fact.OccurredAt });
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The AssignmentChanged outbox event was not inserted.");
        }
    }

    private async Task WriteAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClaimedDispatchOutboxMessage message,
        OrderStatusChangedFact fact,
        Guid driverId,
        string target,
        CancellationToken cancellationToken)
    {
        var payload = auditRedactor.Redact(JsonSerializer.SerializeToElement(new
        {
            assignment_id = fact.AssignmentId,
            order_id = fact.OrderId,
            driver_id = driverId,
            status = target,
            order_previous_status = fact.PreviousStatus,
            order_new_status = fact.NewStatus,
            order_event_id = fact.OrderEventId,
            source_outbox_event_id = message.Id,
        }, JsonOptions));
        await auditWriter.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                Guid.NewGuid(),
                fact.OwnerOrganizationId,
                null,
                AuditAction,
                "Assignment",
                fact.AssignmentId,
                null,
                payload,
                UtcMicrosecondPrecision.Normalize(clock.UtcNow)),
            cancellationToken);
    }

    private sealed class LeaseLostException : Exception;
}
