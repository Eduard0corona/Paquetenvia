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
/// the <c>RECIPIENT_CALL_REQUESTED</c> audit. A refused request writes nothing.</item>
/// <item>No transaction held: the phones are decrypted in memory (live: ADP-001 Key Vault envelope) and the provider
/// is called once (no retry: creating a call is not idempotent).</item>
/// <item>Transaction 2: the outcome (PLACED, UNCONFIRMED or FAILED, result code, provider call id), the idempotency
/// completion (a FAILED request releases its key) and the outcome audit.</item>
/// </list>
/// Reads Dispatch, Orders and Locations rows only under RLS and never writes them. No phone number is returned,
/// logged, audited or stored in clear.
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
    ILogger<PostgreSqlRecipientCallService> logger) : IRecipientCallService
{
    public const string IdempotencyScope = "VOICE-001:REQUEST_RECIPIENT_CALL";
    public const string EntityType = "RecipientCallRequest";
    public const string RequestedAction = "RECIPIENT_CALL_REQUESTED";
    public const string PlacedAction = "RECIPIENT_CALL_PLACED";
    public const string UnconfirmedAction = "RECIPIENT_CALL_UNCONFIRMED";
    public const string FailedAction = "RECIPIENT_CALL_FAILED";
    public const string StatusReportedAction = "RECIPIENT_CALL_STATUS_REPORTED";
    public const string RequestedResultCode = "VOICE_CALL_REQUESTED";

    private static readonly Meter Meter = new("Paqueteria.Drivers.Voice", "1.0.0");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("drivers.voice.recipient_calls");
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
                .ConfigureAwait(false) is null
                ? RecipientCallAvailabilityResult.Yes
                : RecipientCallAvailabilityResult.No(RecipientCallReasons.RateLimited);
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
        var plan = await RunAsync(
            command.ActorId,
            command.OrganizationId,
            (connection, transaction, driverId, token) => PlanAsync(connection, transaction, command, driverId, now, token),
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
        var (status, resultCode, providerCallId, failure) = await PlaceAsync(plan, command, completion)
            .ConfigureAwait(false);
        var completedAt = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        try
        {
            await RunAsync(command.ActorId, command.OrganizationId, (connection, transaction, driverId, token) =>
                CompleteAsync(connection, transaction, command, plan, status, resultCode, providerCallId, completedAt, token),
                completion).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is RecipientCallUnavailableException or RecipientCallForbiddenException)
        {
            // The call may already ring: the answer below stays truthful. The row keeps REQUESTED (and the status
            // callback can still attach the call id); a replay of the key answers REQUESTED.
            logger.LogWarning("Recipient call outcome not recorded with status {Status} and code {Code}.", status, resultCode);
        }

        Record(status.ToLowerInvariant());
        logger.LogInformation("Recipient call completed with status {Status} and code {Code}.", status, resultCode);
        return failure is null ? new RecipientCallRequestResult(plan.CallRequestId, status) : throw failure;
    }

    public async Task<bool> RecordStatusAsync(VoiceCallStatusReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.OrganizationId == Guid.Empty || report.CallRequestId == Guid.Empty ||
            report.ProviderCallId is not { Length: >= 2 and <= 64 } ||
            !report.ProviderCallId.All(char.IsAsciiLetterOrDigit) ||
            !VoiceCallStatuses.All.Contains(report.CallStatus) ||
            report.DurationSeconds is < 0 or > 86_400)
        {
            return false;
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        try
        {
            // The provider is no user: a fresh random identity names only this transaction's app.current_user_id
            // (as for ORD-AUTO-CLOSE), and the audit row carries no actor.
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(Guid.NewGuid(), [report.OrganizationId]),
                async (dbContext, token) =>
                {
                    var (connection, transaction) = Database(dbContext);
                    Guid orderId;
                    await using (var update = Command(
                        connection,
                        transaction,
                        """
                        UPDATE drivers.recipient_call_requests
                        SET provider_call_sid=COALESCE(provider_call_sid,@sid),
                            provider_call_status=@call_status,
                            call_duration_seconds=@duration,
                            status_reported_at=@now,
                            status=CASE WHEN status='PLACED' THEN status ELSE 'PLACED' END,
                            completed_at=COALESCE(completed_at,@now)
                        WHERE id=@id AND org_id=@organization
                          AND (provider_call_sid IS NULL OR provider_call_sid=@sid)
                          AND (provider_call_status IS NULL
                               OR provider_call_status NOT IN ('completed','busy','no-answer','canceled','failed'))
                        RETURNING order_id
                        """))
                    {
                        update.Parameters.Add(P("sid", NpgsqlDbType.Text, report.ProviderCallId));
                        update.Parameters.Add(P("call_status", NpgsqlDbType.Text, report.CallStatus));
                        update.Parameters.Add(P("duration", NpgsqlDbType.Integer, report.DurationSeconds));
                        update.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
                        update.Parameters.Add(P("id", NpgsqlDbType.Uuid, report.CallRequestId));
                        update.Parameters.Add(P("organization", NpgsqlDbType.Uuid, report.OrganizationId));
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
                        null,
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
                },
                cancellationToken).ConfigureAwait(false);
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

    private async Task<RecipientCallPlan> PlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RequestRecipientCallCommand command,
        Guid driverId,
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

            return RecipientCallPlan.ForReplay(Replay(stored));
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

        var callRequestId = Guid.NewGuid();
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
        return new RecipientCallPlan(callRequestId, requestHash, state.Phones(command.OrganizationId, driverId), null);
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

    private async Task<bool> CompleteAsync(
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
        await using (var update = Command(
            connection,
            transaction,
            """
            UPDATE drivers.recipient_call_requests
            SET status=CASE WHEN status='REQUESTED' THEN @status ELSE status END,
                result_code=@code,
                provider_call_sid=COALESCE(provider_call_sid,@sid),
                completed_at=COALESCE(completed_at,@now)
            WHERE id=@id AND org_id=@organization
            """))
        {
            update.Parameters.Add(P("status", NpgsqlDbType.Text, status));
            update.Parameters.Add(P("code", NpgsqlDbType.Text, resultCode));
            update.Parameters.Add(P("sid", NpgsqlDbType.Text, providerCallId));
            update.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
            update.Parameters.Add(P("id", NpgsqlDbType.Uuid, plan.CallRequestId));
            update.Parameters.Add(P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new RecipientCallUnavailableException();
            }
        }

        if (status == RecipientCallStatuses.Failed)
        {
            // A request that placed nothing releases its key: retrying it evaluates everything again.
            await using var release = Command(
                connection,
                transaction,
                """
                DELETE FROM platform.idempotency_keys
                WHERE owner_org_id=@organization AND scope=@scope AND idempotency_key=@key
                  AND request_hash=@hash AND response_status IS NULL AND resource_id=@resource
                """);
            AddIdempotencyIdentity(release, command, plan);
            await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var complete = Command(
                connection,
                transaction,
                """
                UPDATE platform.idempotency_keys
                SET response_status=202,response_body=@body,expires_at=@expires
                WHERE owner_org_id=@organization AND scope=@scope AND idempotency_key=@key
                  AND request_hash=@hash AND response_status IS NULL AND resource_id=@resource
                """);
            AddIdempotencyIdentity(complete, command, plan);
            complete.Parameters.Add(P(
                "body",
                NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(new StoredResponse(plan.CallRequestId, status), JsonOptions)));
            complete.Parameters.Add(P("expires", NpgsqlDbType.TimestampTz, now.AddMinutes(Limits.IdempotencyLifetimeMinutes)));
            await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteAuditAsync(
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
            plan.CallRequestId,
            command.RequestId,
            new
            {
                call_request_id = plan.CallRequestId,
                order_id = command.OrderId,
                status,
                result_code = resultCode,
                call_sid = providerCallId,
            },
            now,
            cancellationToken).ConfigureAwait(false);
        return true;
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
              AND a.status IN ('ACCEPTED','ACTIVE')
              AND (a.owner_org_id=@organization OR a.operator_org_id=@organization)
            """);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
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

    private static RecipientCallRequestResult Replay(StoredIdempotency stored)
    {
        if (stored.ResponseStatus is null && stored.ResourceId is { } inFlight)
        {
            return new RecipientCallRequestResult(inFlight, RecipientCallStatuses.Requested);
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

        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(actorId, [organizationId]),
                async (dbContext, token) =>
                {
                    var (connection, transaction) = Database(dbContext);
                    var driverId = await ResolveActiveDriverAsync(connection, transaction, actorId, organizationId, token)
                        .ConfigureAwait(false) ?? throw new RecipientCallForbiddenException();
                    return await operation(connection, transaction, driverId, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
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

    private static void AddIdempotencyIdentity(NpgsqlCommand command, RequestRecipientCallCommand request, RecipientCallPlan plan)
    {
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, request.OrganizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, request.IdempotencyKey));
        command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, plan.RequestHash));
        command.Parameters.Add(P("resource", NpgsqlDbType.Uuid, plan.CallRequestId));
    }

    /// <summary>The same key must name the same driver, actor and order; nothing else is part of the request.</summary>
    internal static byte[] RequestHash(RequestRecipientCallCommand command, Guid driverId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"VOICE-001|REQUEST_RECIPIENT_CALL|v1|{command.OrganizationId:D}|{command.ActorId:D}|{driverId:D}|{command.OrderId:D}"));

    private RecipientCallOptions Limits => options.Value.RecipientCalls;

    private static void Record(string outcome) =>
        Requests.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    private sealed record StoredIdempotency(byte[] RequestHash, int? ResponseStatus, string? ResponseBody, Guid? ResourceId);

    private sealed record StoredResponse(Guid CallRequestId, string Status);

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

    private sealed record RecipientCallPlan(
        Guid CallRequestId,
        byte[] RequestHash,
        RecipientCallProtectedPhones? Phones,
        RecipientCallRequestResult? Replay)
    {
        public static RecipientCallPlan ForReplay(RecipientCallRequestResult replay) =>
            new(replay.CallRequestId, [], null, replay);
    }
}
