using Finance.Application;
using Finance.Application.Cod;
using Finance.Domain;
using Finance.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Idempotency;
using static Finance.Infrastructure.Persistence.FinanceSql;

namespace Finance.Infrastructure.Cod;

/// <summary>
/// FIN-001 cash-on-delivery write path. Recording a collection is the only way a COD row reaches
/// RECORDED, which the order state machine requires before DELIVERED; reconciling it is the only way it
/// reaches RECONCILED, which the same state machine requires before CLOSED.
/// </summary>
/// <remarks>
/// Both operations write append-only audit evidence in the same transaction as the COD row, but publish
/// no outbox event. A new topic is only durable once <c>security.resolve_outbox_consumer</c> routes it;
/// an unrouted topic is claimed as UNOWNED and settled DEAD with UNKNOWN_TOPIC. Registering a
/// <c>finance.cod-changed</c> consumer is a Realtime decision (topic, aggregate type and audience) and is
/// therefore out of FIN-001 scope.
/// </remarks>
public sealed partial class PostgreSqlCodTransactionService(
    FinanceTenantGateway gateway,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor,
    IFinanceFailureInjector failureInjector,
    IClock clock) : ICodTransactionService
{
    public const string RecordIdempotencyScope = "FIN-001:RECORD_COD";
    public const string ReconcileIdempotencyScope = "FIN-001:RECONCILE_COD";

    public async Task<CodTransactionResult> RecordAsync(
        RecordCodCollectionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CodInputPolicy.IsValid(command) || !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey))
        {
            throw Conflict(FinanceConflictCode.InvalidRequest);
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        var requestHash = CodCanonicalizer.Record(command);
        return await gateway.ExecuteAsync(
            command.ActorId,
            command.OrganizationId,
            async (connection, transaction, token) =>
            {
                // Only what the wait for the order lock cannot make stale is decided before it. A DRIVER's
                // claim rests on the order's active assignment, which dispatch changes under the order row
                // lock, so it is decided below — together with the driver credited as collector — from one
                // observation taken while this transaction holds that lock.
                var identity = await gateway.ReadAuthorizationAsync(
                    connection, transaction, command.ActorId, command.OrganizationId, command.MfaSatisfied, token);
                if (!FinanceAuthorizationPolicy.MayAttemptRecordCod(identity))
                {
                    throw new FinanceForbiddenException();
                }

                await FinanceTenantGateway.AcquireOrderLockAsync(connection, transaction, command.OrderId, token);
                var visibleOrder = await ReadOrderForUpdateAsync(
                    connection, transaction, command.OrganizationId, command.OrderId, token);
                var assignment = await ReadActiveAssignmentAsync(
                    connection, transaction, command.OrganizationId, command.OrderId, token);

                // Capability is settled before the order's existence is revealed or any idempotency record is
                // touched, so an unassigned DRIVER learns nothing about the order or about replay evidence.
                if (!FinanceAuthorizationPolicy.CanRecordCod(identity with
                    {
                        HasMatchingDriverAssignment =
                            assignment?.IsHeldBy(command.ActorId, command.OrganizationId) == true,
                    }))
                {
                    throw new FinanceForbiddenException();
                }

                var replay = await BeginIdempotencyAsync<CodTransactionResult>(
                    connection, transaction, command.OrganizationId, RecordIdempotencyScope,
                    command.IdempotencyKey, requestHash, StatusCodes.Status201Created, now, token);
                if (replay is not null)
                {
                    return replay;
                }

                var order = visibleOrder ?? throw new FinanceNotFoundException();
                var expected = new MoneyCents(order.CodExpectedCents);
                var amount = new MoneyCents(command.AmountCents);
                if (!CodLifecyclePolicy.IsExpected(expected))
                {
                    throw Conflict(FinanceConflictCode.CodNotExpected);
                }

                if (!CodLifecyclePolicy.AmountMatchesExpectation(expected, amount))
                {
                    throw Conflict(FinanceConflictCode.CodAmountMismatch);
                }

                if (await ReadCodByOrderForUpdateAsync(
                        connection, transaction, command.OrganizationId, command.OrderId, token) is not null)
                {
                    throw Conflict(FinanceConflictCode.CodAlreadyRecorded);
                }

                if (!CodLifecyclePolicy.CanRecord(order.Status, expected, amount, false))
                {
                    throw Conflict(FinanceConflictCode.OrderStateConflict);
                }

                var collectingDriverId = assignment?.DriverId;
                var codId = Guid.NewGuid();
                await InsertCodAsync(
                    connection, transaction, codId, order, command.AmountCents, command.Reference!,
                    collectingDriverId, now, token);
                await failureInjector.OnStageAsync(FinanceTransactionStage.CodInserted, token);

                var result = new CodTransactionResult(
                    codId,
                    command.OrderId,
                    command.AmountCents,
                    CodStatus.Recorded.ToContractValue(),
                    now,
                    null);
                await WriteAuditAsync(
                    connection, transaction, command.ActorId, command.OrganizationId,
                    "finance.cod.recorded", codId, command.RequestId,
                    new
                    {
                        cod_transaction_id = codId,
                        order_id = command.OrderId,
                        amount_cents = command.AmountCents,
                        cod_status = CodStatus.Recorded.ToContractValue(),
                        collected_by_driver_id = collectingDriverId,
                    },
                    now,
                    token);
                await CompleteIdempotencyAsync(
                    connection, transaction, command.OrganizationId, RecordIdempotencyScope,
                    command.IdempotencyKey, requestHash, StatusCodes.Status201Created, codId, result, now, token);
                await failureInjector.OnStageAsync(FinanceTransactionStage.BeforeCommit, token);
                return result;
            },
            cancellationToken);
    }

    public async Task<CodTransactionResult> ReconcileAsync(
        ReconcileCodCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CodInputPolicy.IsValid(command) || !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey))
        {
            throw Conflict(FinanceConflictCode.InvalidRequest);
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        var requestHash = CodCanonicalizer.Reconcile(command);
        return await gateway.ExecuteAsync(
            command.ActorId,
            command.OrganizationId,
            async (connection, transaction, token) =>
            {
                var authorization = await gateway.ReadAuthorizationAsync(
                    connection, transaction, command.ActorId, command.OrganizationId, command.MfaSatisfied, token);
                if (!FinanceAuthorizationPolicy.CanReconcileCod(authorization))
                {
                    throw new FinanceForbiddenException();
                }

                var replay = await BeginIdempotencyAsync<CodTransactionResult>(
                    connection, transaction, command.OrganizationId, ReconcileIdempotencyScope,
                    command.IdempotencyKey, requestHash, StatusCodes.Status200OK, now, token);
                if (replay is not null)
                {
                    return replay;
                }

                // Both write paths must take the same locks in the same order — advisory order lock, then
                // the order row, then the COD row — so a concurrent record and reconcile on one order
                // cannot deadlock. The unlocked peek only resolves which order to lock.
                var peek = await ReadCodByIdAsync(
                    connection, transaction, command.OrganizationId, command.CodTransactionId, false, token)
                    ?? throw new FinanceNotFoundException();
                await FinanceTenantGateway.AcquireOrderLockAsync(connection, transaction, peek.OrderId, token);
                var order = await ReadOrderForUpdateAsync(
                    connection, transaction, command.OrganizationId, peek.OrderId, token)
                    ?? throw new FinanceNotFoundException();
                var current = await ReadCodByIdAsync(
                    connection, transaction, command.OrganizationId, command.CodTransactionId, true, token)
                    ?? throw new FinanceNotFoundException();

                if (current.OrderId != order.Id ||
                    !FinanceContractValues.TryParseCodStatus(current.Status, out var status) ||
                    !CodLifecyclePolicy.CanReconcile(order.Status, status))
                {
                    throw Conflict(FinanceConflictCode.CodStateConflict);
                }

                await ReconcileCodAsync(connection, transaction, current.Id, now, token);
                await failureInjector.OnStageAsync(FinanceTransactionStage.CodReconciled, token);

                var result = new CodTransactionResult(
                    current.Id,
                    current.OrderId,
                    current.AmountCents,
                    CodStatus.Reconciled.ToContractValue(),
                    current.RecordedAt,
                    now);
                await WriteAuditAsync(
                    connection, transaction, command.ActorId, command.OrganizationId,
                    "finance.cod.reconciled", current.Id, command.RequestId,
                    new
                    {
                        cod_transaction_id = current.Id,
                        order_id = current.OrderId,
                        amount_cents = current.AmountCents,
                        cod_status = CodStatus.Reconciled.ToContractValue(),
                    },
                    now,
                    token);
                await CompleteIdempotencyAsync(
                    connection, transaction, command.OrganizationId, ReconcileIdempotencyScope,
                    command.IdempotencyKey, requestHash, StatusCodes.Status200OK, current.Id, result, now, token);
                await failureInjector.OnStageAsync(FinanceTransactionStage.BeforeCommit, token);
                return result;
            },
            cancellationToken);
    }

    private async Task<OrderRow?> ReadOrderForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = Create(
            connection,
            transaction,
            """
            SELECT id,owner_org_id,operator_org_id,status,cod_expected_cents
            FROM orders.orders
            WHERE id=@order AND (owner_org_id=@organization OR operator_org_id=@organization)
            FOR UPDATE
            """,
            gateway.CommandTimeoutSeconds);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetString(3),
                reader.GetInt64(4))
            : null;
    }

    internal sealed record OrderRow(
        Guid Id,
        Guid OwnerOrganizationId,
        Guid? OperatorOrganizationId,
        string Status,
        long CodExpectedCents);

    /// <summary>
    /// The order's ACCEPTED or ACTIVE assignment. The driver profile columns are null when that profile is
    /// not visible to the acting organization, which can never satisfy a DRIVER's claim.
    /// </summary>
    internal sealed record ActiveAssignment(Guid DriverId, Guid? DriverUserId, Guid? DriverOrganizationId)
    {
        public bool IsHeldBy(Guid actorId, Guid organizationId) =>
            DriverUserId == actorId && DriverOrganizationId == organizationId;
    }

    internal sealed record CodRow(
        Guid Id,
        Guid OrderId,
        long AmountCents,
        string Status,
        DateTimeOffset? RecordedAt,
        DateTimeOffset? ReconciledAt);

    internal sealed record IdempotencyRow(byte[] Hash, int? Status, string? Body, Guid? ResourceId);
}
