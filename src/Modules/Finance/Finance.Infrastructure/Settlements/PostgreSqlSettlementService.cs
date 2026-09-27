using Finance.Application;
using Finance.Application.Cod;
using Finance.Application.Settlements;
using Finance.Domain.Settlements;
using Finance.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;

namespace Finance.Infrastructure.Settlements;

/// <summary>
/// SET-001 settlement vertical on top of the Slice 1 ledger. Every operation is one tenant transaction:
/// shape is decided before it opens, capability before any settlement or idempotency evidence is read, and
/// every accepted mutation writes its append-only audit entry and completes its idempotency record in the
/// same transaction as the ledger rows it changes. The database guards stay the last word on lifecycle,
/// immutability, reconciliation and source uniqueness; this service decides the same rules first so that a
/// refused request is a stable conflict, and re-verifies the persisted ledger wherever it relies on it.
/// </summary>
/// <remarks>
/// Like FIN-001, settlements publish no outbox event: an unrouted topic would be claimed as UNOWNED and
/// settled DEAD with UNKNOWN_TOPIC, and routing one is a Realtime decision outside SET-001.
/// </remarks>
public sealed partial class PostgreSqlSettlementService(
    FinanceTenantGateway gateway,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor,
    IFinanceFailureInjector failureInjector,
    IClock clock,
    IOptions<FinanceOptions> options) : ISettlementService
{
    public const string CreateIdempotencyScope = "SET-001:CREATE_SETTLEMENT";
    public const string AdjustIdempotencyScope = "SET-001:ADD_SETTLEMENT_ADJUSTMENT";
    public const string ApproveIdempotencyScope = "SET-001:APPROVE_SETTLEMENT";
    public const string PayIdempotencyScope = "SET-001:MARK_SETTLEMENT_PAID";
    public const string VoidIdempotencyScope = "SET-001:VOID_SETTLEMENT";

    public const string AuditEntityType = "Settlement";

    public async Task<SettlementResult> CreateAsync(
        CreateSettlementCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!FinanceOperationalTimeZone.TryResolve(options.Value.OperationalTimeZone, out var zone))
        {
            throw new FinanceUnavailableException("The Finance operational time zone is not configured.");
        }

        if (!SettlementInputPolicy.IsValid(command) ||
            !SettlementPeriod.TryResolve(command.PeriodFrom, command.PeriodTo, zone, out var period))
        {
            throw Conflict(SettlementConflictCode.InvalidRequest);
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        var requestHash = SettlementCanonicalizer.Create(command);
        return await gateway.ExecuteAsync(
            command.ActorId,
            command.OrganizationId,
            async (connection, transaction, token) =>
            {
                await AuthorizeAsync(
                    connection, transaction, command.ActorId, command.OrganizationId, command.MfaSatisfied, token);
                var replay = await BeginIdempotencyAsync(
                    connection, transaction, command.OrganizationId, CreateIdempotencyScope,
                    command.IdempotencyKey, requestHash, StatusCodes.Status201Created, now, token);
                if (replay is not null)
                {
                    return replay;
                }

                // A missing driver and another tenant's driver are the same uniform 404.
                if (!await DriverIsVisibleAsync(
                        connection, transaction, command.OrganizationId, command.DriverId, token))
                {
                    throw new FinanceNotFoundException();
                }

                // One calculation per payee at a time: a concurrent calculation for the same driver waits and
                // then, on a fresh READ COMMITTED snapshot, no longer sees the sources the first one settled.
                await AcquirePayeeLockAsync(
                    connection, transaction, command.OrganizationId, command.DriverId, token);

                var settlementId = Guid.NewGuid();
                await InsertDraftAsync(connection, transaction, settlementId, command, now, token);

                var sources = await ReadEligibleSourcesAsync(
                    connection, transaction, command.OrganizationId, command.DriverId, period, token);
                foreach (var source in sources)
                {
                    await InsertLineAsync(
                        connection, transaction, Guid.NewGuid(), settlementId, command.OrganizationId,
                        source.LineType, source.OrderId, source.AmountCents,
                        SettlementSourcePolicy.AssignmentSource(source.AssignmentId), now, token);
                }

                var total = SettlementLedger.TryTotal(sources.Select(source => source.AmountCents))
                    ?? throw new FinanceUnavailableException("The settlement total cannot be represented.");
                await UpdateHeaderAsync(
                    connection, transaction, command.OrganizationId, settlementId,
                    SettlementStatus.Draft, 0, SettlementStatus.Calculated, total, token);

                var result = (await ReadAsync(
                    connection, transaction, command.OrganizationId, settlementId, false, token))!.Result;
                await WriteAuditAsync(
                    connection, transaction, Guid.NewGuid(), command.ActorId, command.OrganizationId,
                    "finance.settlement.calculated", settlementId, command.RequestId,
                    new
                    {
                        settlement_id = settlementId,
                        payee_type = result.PayeeType,
                        payee_id = result.PayeeId,
                        period_from = result.PeriodFrom,
                        period_to = result.PeriodTo,
                        status = result.Status,
                        total_cents = result.TotalCents,
                        line_count = result.Lines.Count,
                    },
                    now,
                    token);
                await CompleteIdempotencyAsync(
                    connection, transaction, command.OrganizationId, CreateIdempotencyScope,
                    command.IdempotencyKey, requestHash, StatusCodes.Status201Created, settlementId, result,
                    now, token);
                await failureInjector.OnStageAsync(FinanceTransactionStage.BeforeCommit, token);
                return result;
            },
            cancellationToken);
    }

    public async Task<SettlementResult> GetAsync(GetSettlementQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!SettlementInputPolicy.IsValid(query))
        {
            throw Conflict(SettlementConflictCode.InvalidRequest);
        }

        return await gateway.ExecuteAsync(
            query.ActorId,
            query.OrganizationId,
            async (connection, transaction, token) =>
            {
                await AuthorizeAsync(
                    connection, transaction, query.ActorId, query.OrganizationId, query.MfaSatisfied, token);
                var snapshot = await ReadAsync(
                    connection, transaction, query.OrganizationId, query.SettlementId, false, token)
                    ?? throw new FinanceNotFoundException();
                return snapshot.Result;
            },
            cancellationToken);
    }

    /// <summary>
    /// AI05-LIST-SETTLEMENTS: the same capability as <see cref="GetAsync"/>, decided before any settlement is
    /// read, then one keyset page of the selected organization's persisted settlements, newest first. Every
    /// item is verified exactly like a single read, so an unreconciled settlement fails the page closed.
    /// </summary>
    public async Task<SettlementPageResult> ListAsync(ListSettlementsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!SettlementInputPolicy.IsValid(query))
        {
            throw Conflict(SettlementConflictCode.InvalidRequest);
        }

        return await gateway.ExecuteAsync(
            query.ActorId,
            query.OrganizationId,
            async (connection, transaction, token) =>
            {
                await AuthorizeAsync(
                    connection, transaction, query.ActorId, query.OrganizationId, query.MfaSatisfied, token);
                var page = await ReadPageAsync(connection, transaction, query, SettlementListPolicy.PageSize + 1, token);
                var items = page.Take(SettlementListPolicy.PageSize).ToArray();
                var next = page.Count > SettlementListPolicy.PageSize
                    ? SettlementCursorCodec.Encode(new SettlementCursor(items[^1].CreatedAt, items[^1].Id))
                    : null;
                return new SettlementPageResult(items, next);
            },
            cancellationToken);
    }

    /// <summary>
    /// The export is rendered from exactly what <see cref="GetAsync"/> reads — the persisted header and its
    /// persisted lines, reconciled — and never recomputed from assignments or orders.
    /// </summary>
    public async Task<SettlementCsvDocument> ExportCsvAsync(
        GetSettlementQuery query,
        CancellationToken cancellationToken) =>
        SettlementCsvWriter.Write(await GetAsync(query, cancellationToken));

    public async Task<SettlementResult> AddAdjustmentAsync(
        AddSettlementAdjustmentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!SettlementInputPolicy.IsValid(command))
        {
            throw Conflict(SettlementConflictCode.InvalidRequest);
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        var requestHash = SettlementCanonicalizer.Adjust(command);
        return await gateway.ExecuteAsync(
            command.ActorId,
            command.OrganizationId,
            async (connection, transaction, token) =>
            {
                await AuthorizeAsync(
                    connection, transaction, command.ActorId, command.OrganizationId, command.MfaSatisfied, token);
                var replay = await BeginIdempotencyAsync(
                    connection, transaction, command.OrganizationId, AdjustIdempotencyScope,
                    command.IdempotencyKey, requestHash, StatusCodes.Status201Created, now, token);
                if (replay is not null)
                {
                    return replay;
                }

                var current = await ReadAsync(
                    connection, transaction, command.OrganizationId, command.SettlementId, true, token)
                    ?? throw new FinanceNotFoundException();
                if (!SettlementLifecyclePolicy.CanAdjust(current.Status))
                {
                    throw Conflict(SettlementConflictCode.SettlementStateConflict);
                }

                var total = SettlementLedger.TryApply(current.Result.TotalCents, command.AmountCents)
                    ?? throw Conflict(SettlementConflictCode.InvalidRequest);

                // The adjustment's source is the audit entry that records it, so its id is chosen first.
                var auditId = Guid.NewGuid();
                var lineId = Guid.NewGuid();
                await InsertLineAsync(
                    connection, transaction, lineId, command.SettlementId, command.OrganizationId,
                    SettlementLineType.Adjustment, null, command.AmountCents,
                    SettlementSourcePolicy.AdjustmentSource(auditId), now, token);
                await UpdateHeaderAsync(
                    connection, transaction, command.OrganizationId, command.SettlementId,
                    current.Status, current.Result.TotalCents, current.Status, total, token);

                var result = (await ReadAsync(
                    connection, transaction, command.OrganizationId, command.SettlementId, false, token))!.Result;
                await WriteAuditAsync(
                    connection, transaction, auditId, command.ActorId, command.OrganizationId,
                    "finance.settlement.adjusted", command.SettlementId, command.RequestId,
                    new
                    {
                        settlement_id = command.SettlementId,
                        line_id = lineId,
                        amount_cents = command.AmountCents,
                        total_cents = total,
                        reason = command.Reason,
                    },
                    now,
                    token);
                await CompleteIdempotencyAsync(
                    connection, transaction, command.OrganizationId, AdjustIdempotencyScope,
                    command.IdempotencyKey, requestHash, StatusCodes.Status201Created, command.SettlementId,
                    result, now, token);
                await failureInjector.OnStageAsync(FinanceTransactionStage.BeforeCommit, token);
                return result;
            },
            cancellationToken);
    }

    public async Task<SettlementResult> ApproveAsync(
        SettlementTransitionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await TransitionAsync(
            command.ActorId,
            command.OrganizationId,
            command.IdempotencyKey,
            command.SettlementId,
            command.MfaSatisfied,
            command.RequestId,
            ApproveIdempotencyScope,
            SettlementInputPolicy.IsValid(command),
            SettlementCanonicalizer.Transition(command),
            SettlementLifecyclePolicy.CanApprove,
            _ => SettlementStatus.Approved,
            "finance.settlement.approved",
            null,
            RequireApprovableSourcesAsync,
            cancellationToken);
    }

    public async Task<SettlementResult> MarkPaidAsync(
        SettlementTransitionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await TransitionAsync(
            command.ActorId,
            command.OrganizationId,
            command.IdempotencyKey,
            command.SettlementId,
            command.MfaSatisfied,
            command.RequestId,
            PayIdempotencyScope,
            SettlementInputPolicy.IsValid(command),
            SettlementCanonicalizer.Transition(command),
            SettlementLifecyclePolicy.CanMarkPaid,
            _ => SettlementStatus.Paid,
            "finance.settlement.paid",
            null,
            null,
            cancellationToken);
    }

    /// <summary>
    /// Voiding keeps every line and the total exactly as they are; it only releases the settlement's
    /// economic sources so a new settlement may pay them.
    /// </summary>
    public async Task<SettlementResult> VoidAsync(VoidSettlementCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await TransitionAsync(
            command.ActorId,
            command.OrganizationId,
            command.IdempotencyKey,
            command.SettlementId,
            command.MfaSatisfied,
            command.RequestId,
            VoidIdempotencyScope,
            SettlementInputPolicy.IsValid(command),
            SettlementCanonicalizer.Void(command),
            SettlementLifecyclePolicy.CanVoid,
            _ => SettlementStatus.Void,
            "finance.settlement.voided",
            command.Reason,
            null,
            cancellationToken);
    }

    /// <summary>
    /// A header-only lifecycle step. The settlement row is locked before its status is read, so racing
    /// steps serialize on it: exactly one wins and the others see its outcome as a state conflict.
    /// </summary>
    private async Task<SettlementResult> TransitionAsync(
        Guid actorId,
        Guid organizationId,
        string idempotencyKey,
        Guid settlementId,
        bool mfaSatisfied,
        string? requestId,
        string scope,
        bool validShape,
        byte[] requestHash,
        Func<SettlementStatus, bool> allowed,
        Func<SettlementStatus, SettlementStatus> target,
        string auditAction,
        string? reason,
        Func<NpgsqlConnection, NpgsqlTransaction, Guid, Guid, CancellationToken, Task>? precondition,
        CancellationToken cancellationToken)
    {
        if (!validShape)
        {
            throw Conflict(SettlementConflictCode.InvalidRequest);
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        return await gateway.ExecuteAsync(
            actorId,
            organizationId,
            async (connection, transaction, token) =>
            {
                await AuthorizeAsync(connection, transaction, actorId, organizationId, mfaSatisfied, token);
                var replay = await BeginIdempotencyAsync(
                    connection, transaction, organizationId, scope, idempotencyKey, requestHash,
                    StatusCodes.Status200OK, now, token);
                if (replay is not null)
                {
                    return replay;
                }

                var current = await ReadAsync(connection, transaction, organizationId, settlementId, true, token)
                    ?? throw new FinanceNotFoundException();
                if (!allowed(current.Status))
                {
                    throw Conflict(SettlementConflictCode.SettlementStateConflict);
                }

                if (precondition is not null)
                {
                    await precondition(connection, transaction, organizationId, settlementId, token);
                }

                var next = target(current.Status);
                await UpdateHeaderAsync(
                    connection, transaction, organizationId, settlementId,
                    current.Status, current.Result.TotalCents, next, current.Result.TotalCents, token);

                var result = (await ReadAsync(
                    connection, transaction, organizationId, settlementId, false, token))!.Result;
                await WriteAuditAsync(
                    connection, transaction, Guid.NewGuid(), actorId, organizationId, auditAction, settlementId,
                    requestId,
                    reason is null
                        ? new
                        {
                            settlement_id = settlementId,
                            previous_status = current.Result.Status,
                            status = result.Status,
                            total_cents = result.TotalCents,
                        }
                        : (object)new
                        {
                            settlement_id = settlementId,
                            previous_status = current.Result.Status,
                            status = result.Status,
                            total_cents = result.TotalCents,
                            reason,
                        },
                    now,
                    token);
                await CompleteIdempotencyAsync(
                    connection, transaction, organizationId, scope, idempotencyKey, requestHash,
                    StatusCodes.Status200OK, settlementId, result, now, token);
                await failureInjector.OnStageAsync(FinanceTransactionStage.BeforeCommit, token);
                return result;
            },
            cancellationToken);
    }

    /// <summary>
    /// "El cierre bloquea efectivo/incidente pendiente": the current state of every included order is read
    /// inside the approving transaction, after the settlement row lock, and any pending cash, incident or
    /// claim refuses the approval with its stable code.
    /// </summary>
    private async Task RequireApprovableSourcesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid settlementId,
        CancellationToken cancellationToken)
    {
        var sources = await ReadSourceStatesAsync(
            connection, transaction, organizationId, settlementId, cancellationToken);
        switch (SettlementApprovalPolicy.Evaluate(sources))
        {
            case SettlementApprovalBlocker.CashPending:
                throw Conflict(SettlementConflictCode.CashPending);
            case SettlementApprovalBlocker.IncidentPending:
                throw Conflict(SettlementConflictCode.IncidentPending);
            case SettlementApprovalBlocker.ClaimPending:
                throw Conflict(SettlementConflictCode.ClaimPending);
        }
    }

    private async Task AuthorizeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        bool mfaSatisfied,
        CancellationToken cancellationToken)
    {
        var authorization = await ReadAuthorizationAsync(
            connection, transaction, actorId, organizationId, mfaSatisfied, cancellationToken);
        if (!SettlementAuthorizationPolicy.CanOperate(authorization))
        {
            throw new FinanceForbiddenException();
        }
    }

    private static SettlementConflictException Conflict(SettlementConflictCode code, Exception? inner = null) =>
        new(code, inner);
}
