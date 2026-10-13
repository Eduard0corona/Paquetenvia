using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Drivers.Application.Voice;
using Drivers.Domain.Voice;
using Drivers.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;
using Paqueteria.Application.Voice;
using Paqueteria.Infrastructure.Tenancy;
using static Drivers.Infrastructure.Voice.DriverVoiceSql;

namespace Drivers.Infrastructure.Voice;

/// <summary>
/// VOICE-001-MASKED-CALLS-2026-10-11: a driver asks the platform to call their own mobile and bridge it with the
/// delivery recipient through the configured <see cref="IVoiceBridgeProvider"/>.
/// <list type="number">
/// <item>Transaction 1 (tenant, the driver): the actor's ACTIVE driver profile (403 otherwise), the
/// <c>Idempotency-Key</c> (a replay returns the stored answer and calls nobody), the driver's own ACCEPTED or ACTIVE
/// assignment of the order visible to the selected organization (uniform 404 otherwise), the order in
/// <c>DELIVERING</c>, both phones stored and the rate limits; then the idempotency reservation, a REQUESTED row and
/// the <c>RECIPIENT_CALL_REQUESTED</c> audit. A refused request writes nothing. The call request id is chosen before
/// the (retrying) transaction, so a retry after a commit whose answer was lost recognizes its own reservation and goes
/// on placing the call.</item>
/// <item>No transaction held: the phones are decrypted in memory (live: ADP-001 Key Vault envelope) and the provider
/// is called once (no retry: creating a call is not idempotent).</item>
/// <item>Transaction 2: the outcome (PLACED, UNCONFIRMED or FAILED, result code, provider call id), the idempotency
/// completion (a FAILED request releases its key) and the outcome audit; a retry after its commit changes nothing.</item>
/// </list>
/// A request left REQUESTED between its transactions (for example by a restart) is ended UNCONFIRMED by the next
/// replay of its key once <see cref="RecipientCallOptions.StaleRequestSeconds"/> have passed: the call may have rung,
/// so it is never retried and keeps counting toward the limits only inside their windows.
/// <para>
/// VOICE-001-VOICEMAIL-KEYPRESS-2026-10-11: the provider reports key presses. A first, timely 1 of the driver returns
/// the recipient's number for the provider to dial (decrypted in memory only); the recipient's own 1 connects both
/// legs. Anything else hangs up and records <c>bridge_stage</c>; nothing is ever retried.
/// </para>
/// Reads Dispatch, Orders and Locations rows only under RLS and never writes them. No phone number is returned to the
/// driver, logged, audited or stored in clear.
/// </summary>
public sealed class PostgreSqlRecipientCallService(
    TenantTransactionContext<DriversDbContext> transactionContext,
    IVoiceBridgeStatus voiceStatus,
    IVoiceBridgeProvider provider,
    IRecipientCallPhoneResolver phoneResolver,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor,
    IClock clock,
    IOptions<DriversOptions> options,
    ILogger<PostgreSqlRecipientCallService> logger,
    Func<Guid>? newCallRequestId = null) : IRecipientCallService
{
    public const string IdempotencyScope = "VOICE-001:REQUEST_RECIPIENT_CALL";
    public const string EntityType = "RecipientCallRequest";
    public const string RequestedAction = "RECIPIENT_CALL_REQUESTED";
    public const string PlacedAction = "RECIPIENT_CALL_PLACED";
    public const string UnconfirmedAction = "RECIPIENT_CALL_UNCONFIRMED";
    public const string FailedAction = "RECIPIENT_CALL_FAILED";
    public const string StatusReportedAction = "RECIPIENT_CALL_STATUS_REPORTED";
    public const string DriverConfirmedAction = "RECIPIENT_CALL_DRIVER_CONFIRMED";
    public const string NotConfirmedAction = "RECIPIENT_CALL_NOT_CONFIRMED";
    public const string BridgedAction = "RECIPIENT_CALL_BRIDGED";
    public const string BridgeFailedAction = "RECIPIENT_CALL_BRIDGE_FAILED";
    public const string RequestedResultCode = "VOICE_CALL_REQUESTED";

    /// <summary>A request abandoned between its transactions: the provider may or may not have placed the call.</summary>
    public const string AbandonedResultCode = "VOICE_OUTCOME_UNKNOWN";

    /// <summary>The stop stopped qualifying while the request's own reservation was being resumed: nothing was placed.</summary>
    public const string RefusedResultCode = "VOICE_REQUEST_REFUSED";

    private static readonly Meter Meter = new("Paqueteria.Drivers.Voice", "1.0.0");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("drivers.voice.recipient_calls");
    private static readonly Counter<long> BridgeSteps = Meter.CreateCounter<long>("drivers.voice.bridge_steps");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<RecipientCallAvailabilityResult> GetAvailabilityAsync(
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        if (voiceStatus.Mode == VoiceBridgeMode.Disabled)
        {
            return RecipientCallAvailabilityResult.No(RecipientCallReasons.VoiceCallsDisabled);
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        return await RunAsync(actorId, organizationId, async (connection, transaction, driverId, token) =>
        {
            var state = await ReadCallStateAsync(connection, transaction, organizationId, driverId, orderId, token)
                .ConfigureAwait(false) ?? throw new RecipientCallNotFoundException();
            var refusal = Refusal(state);
            if (refusal is not null)
            {
                return RecipientCallAvailabilityResult.No(refusal);
            }

            return await RetryAfterAsync(connection, transaction, organizationId, driverId, orderId, now, token)
                .ConfigureAwait(false) is { } retryAfter
                ? RecipientCallAvailabilityResult.RateLimited(retryAfter)
                : RecipientCallAvailabilityResult.Yes;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecipientCallRequestResult> RequestAsync(
        RequestRecipientCallCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (voiceStatus.Mode == VoiceBridgeMode.Disabled)
        {
            throw new RecipientCallUnavailableException();
        }

        if (command.OrderId == Guid.Empty || !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey))
        {
            throw new RecipientCallConflictException(RecipientCallConflictCodes.InvalidRequest);
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        // Chosen once, outside the retrying transaction: when transaction 1 commits but its answer is lost, the retry
        // finds this id on the reservation and goes on placing the call instead of replaying REQUESTED forever.
        var callRequestId = (newCallRequestId ?? Guid.NewGuid)();
        var plan = await RunAsync(
            command.ActorId,
            command.OrganizationId,
            (connection, transaction, driverId, token) =>
                PlanAsync(connection, transaction, command, driverId, callRequestId, now, token),
            cancellationToken).ConfigureAwait(false);
        if (plan.Replay is { } replay)
        {
            Record("replayed");
            return replay;
        }

        // Once the REQUESTED row is committed the request runs to its end even if the client goes away: aborting the
        // provider call half way would leave a call that may ring without a recorded outcome. Every step below is
        // bounded on its own (provider timeout, Key Vault client, command timeouts).
        var completion = CancellationToken.None;
        var (status, resultCode, providerCallId, failure) = plan.Refusal is { } refusal
            ? (RecipientCallStatuses.Failed, RefusedResultCode, null, refusal)
            : await PlaceAsync(plan, command, completion).ConfigureAwait(false);
        var completedAt = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        var finalStatus = status;
        try
        {
            finalStatus = await RunCompletionAsync(command, (connection, transaction, token) =>
                CompleteAsync(connection, transaction, command, plan, status, resultCode, providerCallId, completedAt, token),
                completion).ConfigureAwait(false);
        }
        catch (RecipientCallUnavailableException)
        {
            // The call may already ring: the answer below stays truthful. The row keeps REQUESTED (and the status
            // callback can still attach the call id); a later replay of the key ends it once it is stale.
            logger.LogWarning("Recipient call outcome not recorded with status {Status} and code {Code}.", status, resultCode);
        }

        Record(finalStatus.ToLowerInvariant());
        logger.LogInformation("Recipient call completed with status {Status} and code {Code}.", finalStatus, resultCode);
        if (failure is not null)
        {
            throw failure;
        }

        return new RecipientCallRequestResult(plan.CallRequestId, finalStatus);
    }

    public async Task<bool> RecordStatusAsync(VoiceCallStatusReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.OrganizationId == Guid.Empty || report.CallRequestId == Guid.Empty ||
            !IsCallSid(report.ProviderCallId) ||
            !VoiceCallStatuses.All.Contains(report.CallStatus) ||
            report.DurationSeconds is < 0 or > 86_400)
        {
            return false;
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        try
        {
            return await RunSystemAsync(report.OrganizationId, async (connection, transaction, token) =>
            {
                Guid orderId;
                // The callback proves the call exists, so an in-flight (REQUESTED) or ambiguous (UNCONFIRMED) request
                // becomes PLACED. A FAILED request was answered as definitively failed and is never promoted; a
                // request with a final provider status keeps it.
                await using (var update = Command(
                    connection,
                    transaction,
                    """
                    UPDATE drivers.recipient_call_requests
                    SET provider_call_sid=COALESCE(provider_call_sid,@sid),
                        provider_call_status=@call_status,
                        call_duration_seconds=@duration,
                        status_reported_at=@now,
                        status='PLACED',
                        completed_at=COALESCE(completed_at,@now)
                    WHERE id=@id AND org_id=@organization
                      AND status = ANY(@promotable)
                      AND (provider_call_sid IS NULL OR provider_call_sid=@sid)
                      AND (provider_call_status IS NULL OR NOT (provider_call_status = ANY(@final_statuses)))
                    RETURNING order_id
                    """))
                {
                    update.Parameters.Add(P("sid", NpgsqlDbType.Text, report.ProviderCallId));
                    update.Parameters.Add(P("call_status", NpgsqlDbType.Text, report.CallStatus));
                    update.Parameters.Add(P("duration", NpgsqlDbType.Integer, report.DurationSeconds));
                    update.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
                    update.Parameters.Add(P("id", NpgsqlDbType.Uuid, report.CallRequestId));
                    update.Parameters.Add(P("organization", NpgsqlDbType.Uuid, report.OrganizationId));
                    update.Parameters.Add(TextArray("promotable", RecipientCallStatuses.PromotableByCallback));
                    update.Parameters.Add(TextArray("final_statuses", VoiceCallStatuses.Final));
                    if (await update.ExecuteScalarAsync(token).ConfigureAwait(false) is not Guid updated)
                    {
                        return false;
                    }

                    orderId = updated;
                }

                await WriteAuditAsync(
                    auditWriter,
                    auditRedactor,
                    connection,
                    transaction,
                    report.OrganizationId,
                    null,
                    StatusReportedAction,
                    EntityType,
                    report.CallRequestId,
                    report.RequestId,
                    new
                    {
                        call_request_id = report.CallRequestId,
                        order_id = orderId,
                        call_sid = report.ProviderCallId,
                        call_status = report.CallStatus,
                        duration_seconds = report.DurationSeconds,
                    },
                    now,
                    token).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            logger.LogWarning("Recipient call status not recorded with error {ErrorType}.", exception.GetType().Name);
            throw new RecipientCallUnavailableException(exception);
        }
    }

    public async Task<RecipientCallBridgeDecision> ConfirmDriverAsync(
        VoiceKeypress keypress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keypress);
        if (keypress.OrganizationId == Guid.Empty || keypress.CallRequestId == Guid.Empty || !IsCallSid(keypress.CallSid))
        {
            return RecipientCallBridgeDecision.Hangup;
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        try
        {
            // 1. Which call request and whose phone, without a lock and without holding anything while decrypting.
            var target = await RunSystemAsync(keypress.OrganizationId, (connection, transaction, token) =>
                ReadBridgeTargetAsync(connection, transaction, keypress, now, token), cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                // Unknown, another tenant's, failed, too old, another call or already decided: nobody is dialed.
                return RecipientCallBridgeDecision.Hangup;
            }

            if (!keypress.Confirmed)
            {
                await SetStageAsync(keypress, null, RecipientCallBridgeStages.DriverNotConfirmed, NotConfirmedAction, now, cancellationToken)
                    .ConfigureAwait(false);
                return RecipientCallBridgeDecision.Hangup;
            }

            VoicePhoneNumber? recipient = null;
            if (target.Phone is { } phone)
            {
                try
                {
                    recipient = await phoneResolver.ResolveRecipientAsync(phone, cancellationToken).ConfigureAwait(false);
                }
                catch (RecipientCallPhoneException exception)
                {
                    logger.LogWarning("Recipient phone unusable for the bridge with problem {Problem}.", exception.Problem);
                }
            }

            if (recipient is null)
            {
                await SetStageAsync(keypress, null, RecipientCallBridgeStages.BridgeFailed, BridgeFailedAction, now, cancellationToken)
                    .ConfigureAwait(false);
                return RecipientCallBridgeDecision.Hangup;
            }

            // 2. The single transition that releases the number: only the first, timely 1 of this call.
            return await SetStageAsync(
                    keypress, null, RecipientCallBridgeStages.DriverConfirmed, DriverConfirmedAction, now, cancellationToken)
                .ConfigureAwait(false)
                ? new RecipientCallBridgeDecision(recipient)
                : RecipientCallBridgeDecision.Hangup;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } ||
                                          IsStoreFailure(exception))
        {
            // Fail closed: nobody is dialed.
            logger.LogWarning("Driver key press not processed with error {ErrorType}.", exception.GetType().Name);
            return RecipientCallBridgeDecision.Hangup;
        }
    }

    public async Task<bool> ConfirmRecipientAsync(VoiceKeypress keypress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keypress);
        if (keypress.OrganizationId == Guid.Empty || keypress.CallRequestId == Guid.Empty || !IsCallSid(keypress.CallSid))
        {
            return false;
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        try
        {
            var moved = await SetStageAsync(
                    keypress,
                    RecipientCallBridgeStages.DriverConfirmed,
                    keypress.Confirmed ? RecipientCallBridgeStages.RecipientConfirmed : RecipientCallBridgeStages.RecipientNotConfirmed,
                    keypress.Confirmed ? BridgedAction : NotConfirmedAction,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            return moved && keypress.Confirmed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } ||
                                          IsStoreFailure(exception))
        {
            logger.LogWarning("Recipient key press not processed with error {ErrorType}.", exception.GetType().Name);
            return false;
        }
    }

    private async Task<RecipientCallPlan> PlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RequestRecipientCallCommand command,
        Guid driverId,
        Guid callRequestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var requestHash = RequestHash(command, driverId);
        await AdvisoryLockAsync(connection, transaction, $"{command.OrganizationId:D}:{IdempotencyScope}:{command.IdempotencyKey}", cancellationToken)
            .ConfigureAwait(false);
        var stored = await ReadIdempotencyAsync(connection, transaction, command, cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(stored.RequestHash, requestHash))
            {
                throw new RecipientCallConflictException(RecipientCallConflictCodes.IdempotencyConflict);
            }

            if (stored.ResponseStatus is null && stored.ResourceId == callRequestId)
            {
                // This request's own reservation: transaction 1 committed, its answer was lost and the execution
                // strategy ran it again. Go on with the call it reserved; nothing is written twice.
                return await ResumeOwnReservationAsync(
                    connection, transaction, command, driverId, callRequestId, requestHash, cancellationToken).ConfigureAwait(false);
            }

            return RecipientCallPlan.ForReplay(
                await ReplayAsync(connection, transaction, command, stored, requestHash, now, cancellationToken).ConfigureAwait(false));
        }

        // One driver at a time: the rate limit counts and the new row are decided together.
        await AdvisoryLockAsync(connection, transaction, $"voice-001:{command.OrganizationId:D}:{driverId:D}", cancellationToken)
            .ConfigureAwait(false);
        var state = await ReadCallStateAsync(
                connection, transaction, command.OrganizationId, driverId, command.OrderId, cancellationToken)
            .ConfigureAwait(false) ?? throw new RecipientCallNotFoundException();
        if (Refusal(state) is { } refusal)
        {
            throw new RecipientCallConflictException(refusal);
        }

        if (await RetryAfterAsync(
                connection, transaction, command.OrganizationId, driverId, command.OrderId, now, cancellationToken)
            .ConfigureAwait(false) is { } retryAfter)
        {
            throw new RecipientCallRateLimitedException(retryAfter);
        }

        await using (var reserve = Command(
            connection,
            transaction,
            """
            INSERT INTO platform.idempotency_keys(
              owner_org_id,scope,idempotency_key,request_hash,response_status,response_body,resource_id,created_at,expires_at)
            VALUES (@organization,@scope,@key,@hash,NULL,NULL,@resource,@now,@expires)
            """))
        {
            reserve.Parameters.Add(P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
            reserve.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
            reserve.Parameters.Add(P("key", NpgsqlDbType.Text, command.IdempotencyKey));
            reserve.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
            reserve.Parameters.Add(P("resource", NpgsqlDbType.Uuid, callRequestId));
            reserve.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
            reserve.Parameters.Add(P("expires", NpgsqlDbType.TimestampTz, now.AddMinutes(Limits.IdempotencyLifetimeMinutes)));
            await reserve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = Command(
            connection,
            transaction,
            """
            INSERT INTO drivers.recipient_call_requests(
              id,org_id,driver_id,requested_by,order_id,assignment_id,provider,status,result_code,requested_at)
            VALUES (@id,@organization,@driver,@actor,@order,@assignment,@provider,'REQUESTED',@code,@now)
            """))
        {
            insert.Parameters.Add(P("id", NpgsqlDbType.Uuid, callRequestId));
            insert.Parameters.Add(P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
            insert.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
            insert.Parameters.Add(P("actor", NpgsqlDbType.Uuid, command.ActorId));
            insert.Parameters.Add(P("order", NpgsqlDbType.Uuid, command.OrderId));
            insert.Parameters.Add(P("assignment", NpgsqlDbType.Uuid, state.AssignmentId));
            insert.Parameters.Add(P("provider", NpgsqlDbType.Text, voiceStatus.ProviderName));
            insert.Parameters.Add(P("code", NpgsqlDbType.Text, RequestedResultCode));
            insert.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteAuditAsync(
            auditWriter,
            auditRedactor,
            connection,
            transaction,
            command.OrganizationId,
            command.ActorId,
            RequestedAction,
            EntityType,
            callRequestId,
            command.RequestId,
            new
            {
                call_request_id = callRequestId,
                order_id = command.OrderId,
                assignment_id = state.AssignmentId,
                provider = voiceStatus.ProviderName,
            },
            now,
            cancellationToken).ConfigureAwait(false);
        return new RecipientCallPlan(callRequestId, requestHash, state.Phones(command.OrganizationId, driverId), null, null);
    }

    /// <summary>
    /// The reservation and the REQUESTED row this request committed are already there: read the phones again and go on
    /// placing the call. If the stop stopped qualifying in between, nothing is placed and the row ends FAILED.
    /// </summary>
    private static async Task<RecipientCallPlan> ResumeOwnReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RequestRecipientCallCommand command,
        Guid driverId,
        Guid callRequestId,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var state = await ReadCallStateAsync(
                connection, transaction, command.OrganizationId, driverId, command.OrderId, cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            return new RecipientCallPlan(callRequestId, requestHash, null, null, new RecipientCallNotFoundException());
        }

        return Refusal(state) is { } refusal
            ? new RecipientCallPlan(callRequestId, requestHash, null, null, new RecipientCallConflictException(refusal))
            : new RecipientCallPlan(callRequestId, requestHash, state.Phones(command.OrganizationId, driverId), null, null);
    }

    /// <summary>
    /// The stored answer of a key. A reservation still in flight answers REQUESTED, unless its request is stale
    /// (abandoned between its transactions): then it ends UNCONFIRMED (the call may have rung; nothing is retried). A
    /// request a status callback already completed finishes its key with that status.
    /// </summary>
    private async Task<RecipientCallRequestResult> ReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RequestRecipientCallCommand command,
        StoredIdempotency stored,
        byte[] requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (stored.ResponseStatus is null && stored.ResourceId is { } inFlight)
        {
            var (status, requestedAt) = await ReadRequestAsync(connection, transaction, command.OrganizationId, inFlight, cancellationToken)
                .ConfigureAwait(false) ?? throw new RecipientCallUnavailableException();
            if (status == RecipientCallStatuses.Requested)
            {
                if (requestedAt > now - TimeSpan.FromSeconds(Limits.StaleRequestSeconds))
                {
                    return new RecipientCallRequestResult(inFlight, RecipientCallStatuses.Requested);
                }

                await using (var end = Command(
                    connection,
                    transaction,
                    """
                    UPDATE drivers.recipient_call_requests
                    SET status='UNCONFIRMED',result_code=@code,completed_at=@now
                    WHERE id=@id AND org_id=@organization AND status='REQUESTED'
                    """))
                {
                    end.Parameters.Add(P("code", NpgsqlDbType.Text, AbandonedResultCode));
                    end.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
                    end.Parameters.Add(P("id", NpgsqlDbType.Uuid, inFlight));
                    end.Parameters.Add(P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
                    await end.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await SettleIdempotencyAsync(
                    connection, transaction, command, requestHash, inFlight, RecipientCallStatuses.Unconfirmed, now, cancellationToken)
                    .ConfigureAwait(false);
                await WriteOutcomeAuditAsync(
                    connection, transaction, command, inFlight, RecipientCallStatuses.Unconfirmed, AbandonedResultCode, null, now, cancellationToken)
                    .ConfigureAwait(false);
                return new RecipientCallRequestResult(inFlight, RecipientCallStatuses.Unconfirmed);
            }

            if (status is RecipientCallStatuses.Placed or RecipientCallStatuses.Unconfirmed)
            {
                await SettleIdempotencyAsync(connection, transaction, command, requestHash, inFlight, status, now, cancellationToken)
                    .ConfigureAwait(false);
                return new RecipientCallRequestResult(inFlight, status);
            }

            throw new RecipientCallUnavailableException();
        }

        if (stored.ResponseStatus == 202 && stored.ResponseBody is { } body)
        {
            var response = JsonSerializer.Deserialize<StoredResponse>(body, JsonOptions);
            if (response is { CallRequestId: var id, Status: RecipientCallStatuses.Placed or RecipientCallStatuses.Unconfirmed } &&
                id != Guid.Empty)
            {
                return new RecipientCallRequestResult(id, response.Status);
            }
        }

        throw new RecipientCallUnavailableException();
    }

    private async Task<(string Status, string ResultCode, string? ProviderCallId, Exception? Failure)> PlaceAsync(
        RecipientCallPlan plan,
        RequestRecipientCallCommand command,
        CancellationToken cancellationToken)
    {
        VoicePhoneNumber driver;
        VoicePhoneNumber recipient;
        try
        {
            (driver, recipient) = await phoneResolver.ResolveAsync(plan.Phones!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RecipientCallPhoneException exception)
        {
            return exception.Problem switch
            {
                RecipientCallPhoneProblem.DriverPhoneUnreadable => Failed(
                    VoiceBridgeResultCodes.PhoneUnavailable,
                    new RecipientCallConflictException(RecipientCallConflictCodes.DriverPhoneRequired)),
                RecipientCallPhoneProblem.DriverPhoneNotDialable => Failed(
                    VoiceBridgeResultCodes.PhoneInvalid,
                    new RecipientCallConflictException(RecipientCallConflictCodes.DriverPhoneRejected)),
                RecipientCallPhoneProblem.RecipientPhoneUnusable => Failed(
                    VoiceBridgeResultCodes.PhoneInvalid,
                    new RecipientCallConflictException(RecipientCallConflictCodes.RecipientPhoneUnavailable)),
                _ => Failed(VoiceBridgeResultCodes.PhoneUnavailable, new RecipientCallUnavailableException()),
            };
        }

        var result = await provider.PlaceCallAsync(
                new VoiceBridgeRequest(plan.CallRequestId, command.OrganizationId, driver, recipient),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Outcome switch
        {
            VoiceBridgeOutcome.Placed => (RecipientCallStatuses.Placed, result.Code, result.ProviderCallId, null),
            // A timeout, a broken answer or a 5xx: the call may exist. Never retried; the key is kept.
            VoiceBridgeOutcome.Ambiguous => (RecipientCallStatuses.Unconfirmed, result.Code, null, null),
            VoiceBridgeOutcome.PermanentFailure when result.Code == VoiceBridgeResultCodes.DriverPhoneRejected => Failed(
                result.Code,
                new RecipientCallConflictException(RecipientCallConflictCodes.DriverPhoneRejected)),
            _ => Failed(VoiceBridgeResultCodes.All.Contains(result.Code) ? result.Code : VoiceBridgeResultCodes.Unavailable,
                new RecipientCallUnavailableException()),
        };

        static (string, string, string?, Exception?) Failed(string code, Exception failure) =>
            (RecipientCallStatuses.Failed, code, null, failure);
    }

    /// <summary>
    /// Records the outcome once. A retry after this transaction committed finds the row already completed: it writes
    /// no second audit and settles the key from the stored status (a status callback may also have completed the row
    /// first). Returns the request's final status.
    /// </summary>
    private async Task<string> CompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RequestRecipientCallCommand command,
        RecipientCallPlan plan,
        string status,
        string resultCode,
        string? providerCallId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool transitioned;
        await using (var update = Command(
            connection,
            transaction,
            """
            UPDATE drivers.recipient_call_requests
            SET status=@status,
                result_code=@code,
                provider_call_sid=COALESCE(provider_call_sid,@sid),
                completed_at=@now
            WHERE id=@id AND org_id=@organization AND status='REQUESTED'
            RETURNING id
            """))
        {
            update.Parameters.Add(P("status", NpgsqlDbType.Text, status));
            update.Parameters.Add(P("code", NpgsqlDbType.Text, resultCode));
            update.Parameters.Add(P("sid", NpgsqlDbType.Text, providerCallId));
            update.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
            update.Parameters.Add(P("id", NpgsqlDbType.Uuid, plan.CallRequestId));
            update.Parameters.Add(P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
            transitioned = await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid;
        }

        var finalStatus = transitioned
            ? status
            : (await ReadRequestAsync(connection, transaction, command.OrganizationId, plan.CallRequestId, cancellationToken)
                .ConfigureAwait(false))?.Status ?? throw new RecipientCallUnavailableException();
        await SettleIdempotencyAsync(
            connection, transaction, command, plan.RequestHash, plan.CallRequestId, finalStatus, now, cancellationToken)
            .ConfigureAwait(false);
        if (transitioned)
        {
            await WriteOutcomeAuditAsync(
                connection, transaction, command, plan.CallRequestId, status, resultCode, providerCallId, now, cancellationToken)
                .ConfigureAwait(false);
        }

        return finalStatus;
    }

    /// <summary>
    /// Completes this request's own reservation with its final status: PLACED or UNCONFIRMED keep the key with a 202
    /// answer; FAILED releases it (nothing was placed, so a retry evaluates everything again). A settled key is left
    /// as it is.
    /// </summary>
    private async Task SettleIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RequestRecipientCallCommand command,
        byte[] requestHash,
        Guid callRequestId,
        string finalStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (finalStatus == RecipientCallStatuses.Failed)
        {
            await using var release = Command(
                connection,
                transaction,
                """
                DELETE FROM platform.idempotency_keys
                WHERE owner_org_id=@organization AND scope=@scope AND idempotency_key=@key
                  AND request_hash=@hash AND response_status IS NULL AND resource_id=@resource
                """);
            AddIdempotencyIdentity(release, command, requestHash, callRequestId);
            await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var complete = Command(
            connection,
            transaction,
            """
            UPDATE platform.idempotency_keys
            SET response_status=202,response_body=@body,expires_at=@expires
            WHERE owner_org_id=@organization AND scope=@scope AND idempotency_key=@key
              AND request_hash=@hash AND response_status IS NULL AND resource_id=@resource
            """);
        AddIdempotencyIdentity(complete, command, requestHash, callRequestId);
        complete.Parameters.Add(P(
            "body",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(new StoredResponse(callRequestId, finalStatus), JsonOptions)));
        complete.Parameters.Add(P("expires", NpgsqlDbType.TimestampTz, now.AddMinutes(Limits.IdempotencyLifetimeMinutes)));
        await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task WriteOutcomeAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RequestRecipientCallCommand command,
        Guid callRequestId,
        string status,
        string resultCode,
        string? providerCallId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        WriteAuditAsync(
            auditWriter,
            auditRedactor,
            connection,
            transaction,
            command.OrganizationId,
            command.ActorId,
            status switch
            {
                RecipientCallStatuses.Placed => PlacedAction,
                RecipientCallStatuses.Unconfirmed => UnconfirmedAction,
                _ => FailedAction,
            },
            EntityType,
            callRequestId,
            command.RequestId,
            new
            {
                call_request_id = callRequestId,
                order_id = command.OrderId,
                status,
                result_code = resultCode,
                call_sid = providerCallId,
            },
            now,
            cancellationToken);

    /// <summary>
    /// Moves the key-press bridge of one call request from <paramref name="expected"/> to <paramref name="stage"/>, for
    /// a call that may still ring, inside the key-press window and bound to the platform's own call, and audits it.
    /// Returns whether this call moved it. A retry after this transaction committed recognizes its own move.
    /// </summary>
    private async Task<bool> SetStageAsync(
        VoiceKeypress keypress,
        string? expected,
        string stage,
        string action,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        var moved = await RunSystemAsync(keypress.OrganizationId, async (connection, transaction, token) =>
        {
            attempt++;
            string? current;
            Guid orderId;
            await using (var read = Command(
                connection,
                transaction,
                """
                SELECT bridge_stage,order_id
                FROM drivers.recipient_call_requests
                WHERE id=@id AND org_id=@organization
                  AND status = ANY(@may_ring)
                  AND requested_at > @not_before
                  AND (provider_call_sid IS NULL OR provider_call_sid=@sid)
                FOR UPDATE
                """))
            {
                AddKeypressFilter(read, keypress, now);
                await using var reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return false;
                }

                current = reader.IsDBNull(0) ? null : reader.GetString(0);
                orderId = reader.GetGuid(1);
            }

            if (!string.Equals(current, expected, StringComparison.Ordinal))
            {
                // Already decided: a replayed or late key press changes nothing. Only this request's own retry, after
                // its committed move, is recognized as done (without a second audit).
                return attempt > 1 && string.Equals(current, stage, StringComparison.Ordinal);
            }

            await using (var update = Command(
                connection,
                transaction,
                """
                UPDATE drivers.recipient_call_requests
                SET bridge_stage=@stage,provider_call_sid=COALESCE(provider_call_sid,@sid)
                WHERE id=@id AND org_id=@organization
                """))
            {
                update.Parameters.Add(P("stage", NpgsqlDbType.Text, stage));
                update.Parameters.Add(P("sid", NpgsqlDbType.Text, keypress.CallSid));
                update.Parameters.Add(P("id", NpgsqlDbType.Uuid, keypress.CallRequestId));
                update.Parameters.Add(P("organization", NpgsqlDbType.Uuid, keypress.OrganizationId));
                await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await WriteAuditAsync(
                auditWriter,
                auditRedactor,
                connection,
                transaction,
                keypress.OrganizationId,
                null,
                action,
                EntityType,
                keypress.CallRequestId,
                keypress.RequestId,
                new
                {
                    call_request_id = keypress.CallRequestId,
                    order_id = orderId,
                    bridge_stage = stage,
                    call_sid = keypress.CallSid,
                },
                now,
                token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        if (moved)
        {
            BridgeSteps.Add(1, new KeyValuePair<string, object?>("stage", stage.ToLowerInvariant()));
            logger.LogInformation("Recipient call bridge moved to stage {Stage}.", stage);
        }

        return moved;
    }

    /// <summary>The call request a driver's key press may bridge, with the recipient's protected phone.</summary>
    private static async Task<BridgeTarget?> ReadBridgeTargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        VoiceKeypress keypress,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            connection,
            transaction,
            """
            SELECT d.id,d.owner_org_id,d.phone_ciphertext,d.pii_key_version
            FROM drivers.recipient_call_requests r
            JOIN orders.orders o ON o.id=r.order_id
            JOIN locations.locations d ON d.id=o.destination_location_id
            WHERE r.id=@id AND r.org_id=@organization
              AND r.status = ANY(@may_ring)
              AND r.requested_at > @not_before
              AND (r.provider_call_sid IS NULL OR r.provider_call_sid=@sid)
              AND r.bridge_stage IS NULL
            """);
        AddKeypressFilter(command, keypress, now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new BridgeTarget(reader.IsDBNull(2)
            ? null
            : new ProtectedRecipientPhone(
                reader.GetGuid(1),
                reader.GetGuid(0),
                reader.GetFieldValue<byte[]>(2),
                reader.GetString(3)));
    }

    private static void AddKeypressFilter(NpgsqlCommand command, VoiceKeypress keypress, DateTimeOffset now)
    {
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, keypress.CallRequestId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, keypress.OrganizationId));
        command.Parameters.Add(TextArray("may_ring", RecipientCallStatuses.MayRing));
        command.Parameters.Add(P("not_before", NpgsqlDbType.TimestampTz, now - RecipientCallPolicy.KeypressWindow));
        command.Parameters.Add(P("sid", NpgsqlDbType.Text, keypress.CallSid));
    }

    private static string? Refusal(RecipientCallState state) =>
        !RecipientCallPolicy.IsCallable(state.OrderStatus) ? RecipientCallReasons.OrderStateNotAllowed
        : state.RecipientCiphertext is null ? RecipientCallReasons.RecipientPhoneUnavailable
        : state.DriverCiphertext is null ? RecipientCallReasons.DriverPhoneRequired
        : null;

    private async Task<TimeSpan?> RetryAfterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid driverId,
        Guid orderId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var limits = Limits;
        var orderWindow = TimeSpan.FromMinutes(limits.OrderWindowMinutes);
        var orderRequests = new List<DateTimeOffset>();
        var driverRequests = new List<DateTimeOffset>();
        await using var command = Command(
            connection,
            transaction,
            """
            SELECT requested_at,order_id=@order
            FROM drivers.recipient_call_requests
            WHERE org_id=@organization AND driver_id=@driver AND status<>'FAILED' AND requested_at>@since
            ORDER BY requested_at DESC
            LIMIT 500
            """);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
        command.Parameters.Add(P("since", NpgsqlDbType.TimestampTz, now - TimeSpan.FromHours(1)));
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var requestedAt = reader.GetFieldValue<DateTimeOffset>(0);
                driverRequests.Add(requestedAt);
                if (reader.GetBoolean(1) && requestedAt > now - orderWindow)
                {
                    orderRequests.Add(requestedAt);
                }
            }
        }

        return new RecipientCallLimits(limits.MaximumPerOrder, orderWindow, limits.MaximumPerDriverPerHour)
            .RetryAfter(orderRequests, driverRequests, now);
    }

    private static async Task<RecipientCallState?> ReadCallStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid driverId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        // The DRV-001 visibility of a stop: the driver's own ACCEPTED or ACTIVE assignment of an order the selected
        // organization owns or operates, with the destination location visible under RLS.
        await using var command = Command(
            connection,
            transaction,
            """
            SELECT a.id,o.status,d.id,d.owner_org_id,d.phone_ciphertext,d.pii_key_version,
                   p.phone_ciphertext,p.phone_pii_key_version
            FROM dispatch.assignments a
            JOIN orders.orders o ON o.id=a.order_id
            JOIN locations.locations d ON d.id=o.destination_location_id
            JOIN drivers.driver_profiles p ON p.id=a.driver_id
            WHERE a.order_id=@order AND a.driver_id=@driver
              AND a.status = ANY(@assignment_statuses)
              AND (a.owner_org_id=@organization OR a.operator_org_id=@organization)
            """);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(TextArray("assignment_statuses", RecipientCallPolicy.CurrentAssignmentStatuses));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var state = new RecipientCallState(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<byte[]>(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? null : state;
    }

    private static async Task<(string Status, DateTimeOffset RequestedAt)?> ReadRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid callRequestId,
        CancellationToken cancellationToken)
    {
        await using var read = Command(
            connection,
            transaction,
            """
            SELECT status,requested_at FROM drivers.recipient_call_requests
            WHERE id=@id AND org_id=@organization
            FOR UPDATE
            """);
        read.Parameters.Add(P("id", NpgsqlDbType.Uuid, callRequestId));
        read.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1))
            : null;
    }

    private static async Task<StoredIdempotency?> ReadIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RequestRecipientCallCommand command,
        CancellationToken cancellationToken)
    {
        await using var read = Command(
            connection,
            transaction,
            """
            SELECT request_hash,response_status,response_body::text,resource_id
            FROM platform.idempotency_keys
            WHERE owner_org_id=@organization AND scope=@scope AND idempotency_key=@key
            """);
        read.Parameters.Add(P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
        read.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        read.Parameters.Add(P("key", NpgsqlDbType.Text, command.IdempotencyKey));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredIdempotency(
                reader.GetFieldValue<byte[]>(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3))
            : null;
    }

    /// <summary>A driver transaction: the actor's ACTIVE driver profile is resolved first (403 otherwise).</summary>
    private async Task<T> RunAsync<T>(
        Guid actorId,
        Guid organizationId,
        Func<NpgsqlConnection, NpgsqlTransaction, Guid, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new RecipientCallForbiddenException();
        }

        return await StoreAsync(() => transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(actorId, [organizationId]),
            async (dbContext, token) =>
            {
                var (connection, transaction) = Database(dbContext);
                var driverId = await ResolveActiveDriverAsync(connection, transaction, actorId, organizationId, token)
                    .ConfigureAwait(false) ?? throw new RecipientCallForbiddenException();
                return await operation(connection, transaction, driverId, token).ConfigureAwait(false);
            },
            cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Completing the request's own row as its actor, without resolving the driver again: a driver suspended between
    /// the two transactions still gets its outcome recorded.
    /// </summary>
    private Task<T> RunCompletionAsync<T>(
        RequestRecipientCallCommand command,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        StoreAsync(() => transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
            (dbContext, token) =>
            {
                var (connection, transaction) = Database(dbContext);
                return operation(connection, transaction, token);
            },
            cancellationToken), cancellationToken);

    /// <summary>
    /// A provider-driven transaction (callbacks and key presses). The provider is no user: a fresh random identity names
    /// only this transaction's <c>app.current_user_id</c> (as for ORD-AUTO-CLOSE) and the audit rows carry no actor.
    /// </summary>
    private Task<T> RunSystemAsync<T>(
        Guid organizationId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(Guid.NewGuid(), [organizationId]),
            (dbContext, token) =>
            {
                var (connection, transaction) = Database(dbContext);
                return operation(connection, transaction, token);
            },
            cancellationToken);

    private async Task<T> StoreAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            logger.LogWarning("Recipient call store unavailable with error {ErrorType}.", exception.GetType().Name);
            throw new RecipientCallUnavailableException(exception);
        }
    }

    private static async Task AdvisoryLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@key,0));");
        command.Parameters.Add(P("key", NpgsqlDbType.Text, key));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddIdempotencyIdentity(
        NpgsqlCommand command,
        RequestRecipientCallCommand request,
        byte[] requestHash,
        Guid callRequestId)
    {
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, request.OrganizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, request.IdempotencyKey));
        command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
        command.Parameters.Add(P("resource", NpgsqlDbType.Uuid, callRequestId));
    }

    private static NpgsqlParameter TextArray(string name, IEnumerable<string> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.Order(StringComparer.Ordinal).ToArray() };

    private static bool IsCallSid(string? value) =>
        value is { Length: >= 2 and <= 64 } && value.All(char.IsAsciiLetterOrDigit);

    /// <summary>The same key must name the same driver, actor and order; nothing else is part of the request.</summary>
    internal static byte[] RequestHash(RequestRecipientCallCommand command, Guid driverId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"VOICE-001|REQUEST_RECIPIENT_CALL|v1|{command.OrganizationId:D}|{command.ActorId:D}|{driverId:D}|{command.OrderId:D}"));

    private RecipientCallOptions Limits => options.Value.RecipientCalls;

    private static void Record(string outcome) =>
        Requests.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    private sealed record StoredIdempotency(byte[] RequestHash, int? ResponseStatus, string? ResponseBody, Guid? ResourceId);

    private sealed record StoredResponse(Guid CallRequestId, string Status);

    private sealed record BridgeTarget(ProtectedRecipientPhone? Phone)
    {
        public override string ToString() => "BridgeTarget { [redacted] }";
    }

    private sealed record RecipientCallState(
        Guid AssignmentId,
        string OrderStatus,
        Guid LocationId,
        Guid LocationOwnerOrganizationId,
        byte[]? RecipientCiphertext,
        string RecipientKeyVersion,
        byte[]? DriverCiphertext,
        string? DriverKeyVersion)
    {
        public RecipientCallProtectedPhones Phones(Guid organizationId, Guid driverId) => new(
            organizationId,
            driverId,
            DriverCiphertext!,
            DriverKeyVersion!,
            LocationOwnerOrganizationId,
            LocationId,
            RecipientCiphertext!,
            RecipientKeyVersion);

        public override string ToString() => "RecipientCallState { [redacted] }";
    }

    /// <param name="Phones">The protected phones to place the call with (null for a replay or a refusal).</param>
    /// <param name="Replay">The stored answer of the key: nothing is placed.</param>
    /// <param name="Refusal">The stop stopped qualifying while this request's own reservation was resumed: nothing is
    /// placed and the row ends FAILED with this answer.</param>
    private sealed record RecipientCallPlan(
        Guid CallRequestId,
        byte[] RequestHash,
        RecipientCallProtectedPhones? Phones,
        RecipientCallRequestResult? Replay,
        Exception? Refusal)
    {
        public static RecipientCallPlan ForReplay(RecipientCallRequestResult replay) =>
            new(replay.CallRequestId, [], null, replay, null);
    }
}
