using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Orders;
using Orders.Domain;
using Orders.Infrastructure.Persistence;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Orders.Infrastructure.Orders;

public enum OrderTransitionStage
{
    ReservationCreated,
    OrderLocked,
    OrderUpdated,
    EventInserted,
    OutboxInserted,
    TimelineOutboxInserted,
    AuditInserted,
    BeforeIdempotencyCompletion,
}

public interface IOrderTransitionFailureInjector
{
    Task OnStageAsync(OrderTransitionStage stage, CancellationToken cancellationToken);
}

public sealed class NoOpOrderTransitionFailureInjector : IOrderTransitionFailureInjector
{
    public Task OnStageAsync(OrderTransitionStage stage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public sealed class DisabledOrderTransitionService : IOrderTransitionService
{
    public Task<OrderResult> TransitionAsync(
        TransitionOrderCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new OrderTransitionConflictException(OrderTransitionConflictCode.DependencyUnavailable);
    }
}

public sealed class PostgreSqlOrderTransitionService(
    TenantTransactionContext<OrdersDbContext> transactionContext,
    IOrderTransitionAuthorizationReader authorizationReader,
    IOrderTransitionReplayAuthorizationReader replayAuthorizationReader,
    IOrderQuoteAcceptanceGuardReader quoteAcceptanceReader,
    IOrderAssignmentGuardReader assignmentReader,
    IOrderProofGuardReader proofReader,
    IOrderCustodyGuardReader custodyReader,
    IOrderIncidentGuardReader incidentReader,
    IOrderCodGuardReader codReader,
    IOrderTransitionAuthorizer authorizer,
    OrderTransitionGuardRegistry guardRegistry,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor,
    IOrderTransitionFailureInjector failureInjector,
    IOptions<OrdersOptions> options,
    IClock clock) : IOrderTransitionService
{
    internal const string IdempotencyScope = "ORD-002:TRANSITION_ORDER";
    internal const string OutboxTopic = "orders.status-changed";
    internal const string TimelineOutboxTopic = "orders.timeline-event-added";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<OrderResult> TransitionAsync(
        TransitionOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!OrderTransitionInputPolicy.IsValidCommandShape(
                command,
                options.Value.TransitionMetadataMaximumBytes) ||
            !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey) ||
            !OrderContractValues.TryParseOrderStatus(command.TargetStatus, out var target) ||
            !OrderTransitionInputPolicy.TryNormalizeMetadata(
                command.MetadataJson,
                target == OrderStatus.Confirmed ? OrderStatus.Draft : default,
                target,
                options.Value.TransitionMetadataMaximumBytes,
                out var metadata))
        {
            throw new OrderTransitionConflictException(OrderTransitionConflictCode.InvalidRequest);
        }

        var occurredAt = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        var requestHash = OrderTransitionCanonicalizer.ComputeSha256(command, target, metadata);
        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
                (dbContext, token) => TransitionWithinTransactionAsync(
                    dbContext,
                    command,
                    target,
                    metadata,
                    requestHash,
                    occurredAt,
                    token),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrderTransitionConflictException)
        {
            throw;
        }
        catch (OrderTransitionForbiddenException)
        {
            throw;
        }
        catch (OrderTransitionInfrastructureException)
        {
            throw;
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure)
        {
            throw new OrderTransitionConflictException(OrderTransitionConflictCode.ConcurrencyConflict);
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or DbUpdateException)
        {
            throw new OrderTransitionInfrastructureException(
                "The transition dependency failed safely.",
                exception);
        }
    }

    private async Task<OrderResult> TransitionWithinTransactionAsync(
        OrdersDbContext dbContext,
        TransitionOrderCommand command,
        OrderStatus target,
        NormalizedTransitionMetadata metadata,
        byte[] requestHash,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();

        await AcquireIdempotencyLockAsync(connection, transaction, command, cancellationToken);
        var idempotency = await FindIdempotencyAsync(connection, transaction, command, cancellationToken);
        if (idempotency is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(idempotency.RequestHash, requestHash))
            {
                throw new OrderTransitionConflictException(OrderTransitionConflictCode.IdempotencyConflict);
            }

            var replay = ReadCompletedResponse(idempotency);
            if (replay is not null)
            {
                if (command.ExpectedVersion == int.MaxValue)
                {
                    throw new OrderTransitionConflictException(
                        OrderTransitionConflictCode.IdempotencyConflict);
                }

                var replayAuthorization = await replayAuthorizationReader.ReadAsync(
                    connection,
                    transaction,
                    command.ActorId,
                    command.OrganizationId,
                    command.OrderId,
                    command.ExpectedVersion + 1,
                    cancellationToken);
                var replayEvaluation = OrderTransitionReplayPolicy.Evaluate(
                    command,
                    target,
                    replay,
                    replayAuthorization);
                if (!replayEvaluation.IsConsistent)
                {
                    throw new OrderTransitionConflictException(
                        OrderTransitionConflictCode.IdempotencyConflict);
                }

                if (!authorizer.IsAuthorized(new OrderTransitionAuthorizationContext(
                        replayAuthorization.ActiveRole,
                        replayEvaluation.Source,
                        replayEvaluation.Target,
                        command.MfaSatisfied,
                        replayAuthorization.HasMatchingDriverAssignment)))
                {
                    throw new OrderTransitionForbiddenException();
                }

                return replay;
            }
        }
        else
        {
            await InsertIdempotencyReservationAsync(
                connection,
                transaction,
                command,
                requestHash,
                occurredAt,
                occurredAt.AddMinutes(options.Value.IdempotencyLifetimeMinutes),
                cancellationToken);
            await failureInjector.OnStageAsync(OrderTransitionStage.ReservationCreated, cancellationToken);
        }

        var order = await ReadOrderForUpdateAsync(
            connection,
            transaction,
            command.OrganizationId,
            command.OrderId,
            cancellationToken);
        if (order is null ||
            !OrderContractValues.TryParseOrderStatus(order.Status, out var source))
        {
            throw new OrderTransitionConflictException(OrderTransitionConflictCode.OrderUnavailable);
        }

        await failureInjector.OnStageAsync(OrderTransitionStage.OrderLocked, cancellationToken);
        occurredAt = await SequenceAfterCommittedIncidentsAsync(
            connection,
            transaction,
            command.OrganizationId,
            command.OrderId,
            occurredAt,
            cancellationToken);
        var version = OrderTransitionMatrix.EvaluateVersion(order.Version, command.ExpectedVersion);
        if (!version.Allowed)
        {
            throw new OrderTransitionConflictException(OrderTransitionConflictCode.VersionConflict);
        }

        var state = OrderTransitionMatrix.Evaluate(
            source,
            target,
            occurredAt,
            order.ClaimWindowEndsAt,
            order.FinalizedAt);
        if (!state.Allowed)
        {
            throw new OrderTransitionConflictException(
                state.Code == OrderTransitionRuleCode.TerminalState
                    ? OrderTransitionConflictCode.TerminalState
                    : OrderTransitionConflictCode.InvalidState);
        }

        var authorization = await authorizationReader.ReadAsync(
            connection,
            transaction,
            command.ActorId,
            command.OrganizationId,
            command.OrderId,
            cancellationToken);
        if (!authorizer.IsAuthorized(new OrderTransitionAuthorizationContext(
                authorization.ActiveRole,
                source,
                target,
                command.MfaSatisfied,
                authorization.HasMatchingDriverAssignment)))
        {
            throw new OrderTransitionForbiddenException();
        }

        var quoteAcceptance = new QuoteAcceptanceGuardSnapshot(false, false);
        if (source == OrderStatus.Draft && target == OrderStatus.Confirmed)
        {
            quoteAcceptance = await quoteAcceptanceReader.ReadAsync(
                connection, transaction, command.OrganizationId, command.OrderId, cancellationToken);
        }

        var assignment = new AssignmentGuardSnapshot(false, false, false, false);
        if (target == OrderStatus.Assigned ||
            (target == OrderStatus.Delivering && source == OrderStatus.FailedAttempt))
        {
            assignment = await assignmentReader.ReadAsync(
                connection,
                transaction,
                command.OrganizationId,
                command.OrderId,
                order.CityId,
                occurredAt,
                cancellationToken);
        }

        var proofs = target is OrderStatus.PickedUp or OrderStatus.Delivered
            ? await proofReader.ReadAsync(
                connection, transaction, command.OrganizationId, command.OrderId, cancellationToken)
            : new ProofGuardSnapshot(false, false);

        var needsCustody = target == OrderStatus.Returning ||
            (target == OrderStatus.Cancelled && source == OrderStatus.AtPickup) ||
            (target == OrderStatus.Delivering && source == OrderStatus.FailedAttempt);
        var custody = needsCustody
            ? await custodyReader.ReadAsync(
                connection, transaction, command.OrganizationId, command.OrderId, cancellationToken)
            : new CustodyGuardSnapshot(false);

        var needsIncidents = target is OrderStatus.FailedAttempt or OrderStatus.Closed ||
            source == OrderStatus.FailedAttempt;
        var incidents = needsIncidents
            ? await incidentReader.ReadAsync(
                connection,
                transaction,
                command.OrganizationId,
                command.OrderId,
                metadata.IncidentId,
                cancellationToken)
            : new IncidentGuardSnapshot(false, false, false, false);

        var cod = target is OrderStatus.Delivered or OrderStatus.Closed
            ? await codReader.ReadAsync(
                connection, transaction, command.OrganizationId, command.OrderId, cancellationToken)
            : new CodGuardSnapshot(false, null, null);

        var guardContext = new OrderTransitionGuardContext
        {
            Source = source,
            Target = target,
            Reason = command.Reason!,
            OccurredAt = occurredAt,
            ClaimWindowEndsAt = order.ClaimWindowEndsAt,
            FinalizedAt = order.FinalizedAt,
            CodExpectedCents = order.CodExpectedCents,
            MonetaryIntegrityValid = HasMonetaryIntegrity(order),
            Metadata = metadata,
            QuoteAcceptance = quoteAcceptance,
            Assignment = assignment,
            Proofs = proofs,
            Custody = custody,
            Incidents = incidents,
            Cod = cod,
        };
        var guard = guardRegistry.Evaluate(guardContext);
        if (!guard.Satisfied)
        {
            throw new OrderTransitionConflictException(
                OrderTransitionConflictCode.GuardNotSatisfied,
                guard.Code);
        }

        var newVersion = checked(order.Version + 1);
        var claimWindowEndsAt = order.ClaimWindowEndsAt;
        if (target == OrderStatus.Delivered && claimWindowEndsAt is null)
        {
            claimWindowEndsAt = occurredAt.AddHours(options.Value.ClaimWindowHours);
        }

        var finalizedAt = target == OrderStatus.ClaimResolved
            ? occurredAt
            : order.FinalizedAt;
        var affected = await UpdateOrderAsync(
            connection,
            transaction,
            command.OrganizationId,
            order,
            target,
            newVersion,
            occurredAt,
            claimWindowEndsAt,
            finalizedAt,
            cancellationToken);
        if (affected != 1)
        {
            throw new OrderTransitionConflictException(OrderTransitionConflictCode.ConcurrencyConflict);
        }

        await failureInjector.OnStageAsync(OrderTransitionStage.OrderUpdated, cancellationToken);
        var reasonRedacted = RedactReason(command.Reason!);
        var publicEventCode = OrderPublicEventCodePolicy.Map(target);
        var eventId = Guid.NewGuid();
        await InsertEventAsync(
            connection,
            transaction,
            eventId,
            order,
            command.ActorId,
            source,
            target,
            newVersion,
            publicEventCode,
            reasonRedacted,
            metadata,
            incidents,
            occurredAt,
            cancellationToken);
        await failureInjector.OnStageAsync(OrderTransitionStage.EventInserted, cancellationToken);

        var driverAudience = await ReadDriverAudienceCandidateAsync(
            connection,
            transaction,
            order.OwnerOrganizationId,
            order.Id,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            Guid.NewGuid(),
            eventId,
            order,
            source,
            target,
            newVersion,
            publicEventCode,
            driverAudience,
            occurredAt,
            cancellationToken);
        await failureInjector.OnStageAsync(OrderTransitionStage.OutboxInserted, cancellationToken);
        await InsertTimelineOutboxAsync(
            connection,
            transaction,
            Guid.NewGuid(),
            eventId,
            order,
            target,
            newVersion,
            occurredAt,
            cancellationToken);
        await failureInjector.OnStageAsync(
            OrderTransitionStage.TimelineOutboxInserted,
            cancellationToken);
        await InsertDispatchReactionOutboxAsync(
            connection,
            transaction,
            eventId,
            order,
            source,
            target,
            newVersion,
            occurredAt,
            cancellationToken);

        var auditPayload = JsonSerializer.SerializeToElement(new
        {
            order_id = order.Id,
            previous_status = source.ToContractValue(),
            new_status = target.ToContractValue(),
            previous_version = order.Version,
            new_version = newVersion,
            reason_redacted = reasonRedacted,
            request_id = command.RequestId,
        }, JsonOptions);
        await auditWriter.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                Guid.NewGuid(),
                order.OwnerOrganizationId,
                command.ActorId,
                "ORDER_STATUS_CHANGED",
                "Order",
                order.Id,
                command.RequestId,
                auditRedactor.Redact(auditPayload),
                occurredAt),
            cancellationToken);
        await failureInjector.OnStageAsync(OrderTransitionStage.AuditInserted, cancellationToken);

        var result = ToResult(
            order,
            target.ToContractValue(),
            newVersion,
            claimWindowEndsAt,
            finalizedAt);
        var responseJson = JsonSerializer.Serialize(result, JsonOptions);
        await failureInjector.OnStageAsync(
            OrderTransitionStage.BeforeIdempotencyCompletion,
            cancellationToken);
        await CompleteIdempotencyAsync(
            connection,
            transaction,
            command,
            requestHash,
            result.Id,
            responseJson,
            occurredAt.AddMinutes(options.Value.IdempotencyLifetimeMinutes),
            cancellationToken);
        return result;
    }

    private string RedactReason(string reason)
    {
        var payload = JsonSerializer.SerializeToElement(new { reason }, JsonOptions);
        var redacted = auditRedactor.Redact(payload);
        using var document = JsonDocument.Parse(redacted.Json);
        return document.RootElement.GetProperty("reason").GetString() ?? AuditPayloadRedactor.Replacement;
    }

    private async Task AcquireIdempotencyLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransitionOrderCommand transition,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key,0));");
        command.Parameters.Add(P(
            "lock_key",
            NpgsqlDbType.Text,
            $"{transition.OrganizationId:D}:{IdempotencyScope}:{transition.IdempotencyKey}"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IdempotencyRow?> FindIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransitionOrderCommand transition,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT request_hash,response_status,response_body::text,resource_id
            FROM platform.idempotency_keys
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key
            """);
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, transition.OrganizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, transition.IdempotencyKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new(
            reader.GetFieldValue<byte[]>(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3));
    }

    private static OrderResult? ReadCompletedResponse(IdempotencyRow row)
    {
        if (row.ResponseStatus is null && row.ResponseBody is null && row.ResourceId is null)
        {
            return null;
        }

        if (row.ResponseStatus != 200 || string.IsNullOrWhiteSpace(row.ResponseBody) || row.ResourceId is null)
        {
            throw new OrderTransitionConflictException(OrderTransitionConflictCode.IdempotencyConflict);
        }

        OrderResult? result;
        try
        {
            result = JsonSerializer.Deserialize<OrderResult>(row.ResponseBody, JsonOptions);
        }
        catch (JsonException)
        {
            throw new OrderTransitionConflictException(OrderTransitionConflictCode.IdempotencyConflict);
        }

        if (result is null)
        {
            throw new OrderTransitionConflictException(OrderTransitionConflictCode.IdempotencyConflict);
        }

        return result.Id == row.ResourceId
            ? result
            : throw new OrderTransitionConflictException(OrderTransitionConflictCode.IdempotencyConflict);
    }

    private async Task InsertIdempotencyReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransitionOrderCommand transition,
        byte[] requestHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO platform.idempotency_keys(
              owner_org_id,scope,idempotency_key,request_hash,response_status,response_body,resource_id,created_at,expires_at)
            VALUES (@owner,@scope,@key,@hash,NULL,NULL,NULL,@created,@expires)
            """);
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, transition.OrganizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, transition.IdempotencyKey));
        command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
        command.Parameters.Add(P("created", NpgsqlDbType.TimestampTz, createdAt));
        command.Parameters.Add(P("expires", NpgsqlDbType.TimestampTz, expiresAt));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "The transition reservation was not inserted.");
    }

    /// <summary>
    /// The transition's time is read from the application clock before the order lock, and
    /// INC-001 opens incidents under a share lock on the same row. Whichever commits first, the
    /// later one must carry the later time, or an incident opened during the previous attempt
    /// could look newer than the status change that starts the next one. This runs after the
    /// order lock in its own statement (a fresh READ COMMITTED snapshot, so it sees every incident
    /// committed while the lock was awaited) and moves the transition just past the newest
    /// incident of the order when the clock alone would have placed it earlier. INC-001 applies
    /// the mirror rule against the order history. No AI-06 column is needed.
    /// </summary>
    private async Task<DateTimeOffset> SequenceAfterCommittedIncidentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid orderId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT max(i.created_at)
            FROM incidents.incidents i
            WHERE i.order_id=@order AND (i.owner_org_id=@org OR i.operator_org_id=@org)
            """);
        command.Parameters.Add(P("org", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        var latest = await command.ExecuteScalarAsync(cancellationToken);
        if (latest is not DateTime latestIncident)
        {
            return occurredAt;
        }

        var after = new DateTimeOffset(DateTime.SpecifyKind(latestIncident, DateTimeKind.Utc)).AddTicks(10);
        return after > occurredAt ? after : occurredAt;
    }

    private async Task<OrderRow?> ReadOrderForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id,public_id,quote_id,owner_org_id,operator_org_id,city_id,status,version,
                   claim_window_ends_at,finalized_at,cod_expected_cents,
                   subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,
                   financial_override IS NOT NULL,currency,origin_location_id,destination_location_id,
                   service_type,service_area_id,pricing_tier
            FROM orders.orders
            WHERE id=@id AND owner_org_id=@org
            FOR UPDATE
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("org", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.GetGuid(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetInt64(14),
            reader.GetInt64(15),
            reader.GetBoolean(16),
            reader.GetString(17),
            reader.GetGuid(18),
            reader.GetGuid(19),
            reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetGuid(21),
            reader.GetString(22));
    }

    private async Task<int> UpdateOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        OrderRow order,
        OrderStatus target,
        int newVersion,
        DateTimeOffset occurredAt,
        DateTimeOffset? claimWindowEndsAt,
        DateTimeOffset? finalizedAt,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE orders.orders
            SET status=@target,version=@new_version,updated_at=@occurred,
                claim_window_ends_at=@claim_window,finalized_at=@finalized
            WHERE id=@id
              AND owner_org_id=@org
              AND status=@source
              AND version=@expected_version
            """);
        command.Parameters.Add(P("target", NpgsqlDbType.Text, target.ToContractValue()));
        command.Parameters.Add(P("new_version", NpgsqlDbType.Integer, newVersion));
        command.Parameters.Add(P("occurred", NpgsqlDbType.TimestampTz, occurredAt));
        command.Parameters.Add(P("claim_window", NpgsqlDbType.TimestampTz, claimWindowEndsAt));
        command.Parameters.Add(P("finalized", NpgsqlDbType.TimestampTz, finalizedAt));
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, order.Id));
        command.Parameters.Add(P("org", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P("source", NpgsqlDbType.Text, order.Status));
        command.Parameters.Add(P("expected_version", NpgsqlDbType.Integer, order.Version));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        OrderRow order,
        Guid actorId,
        OrderStatus source,
        OrderStatus target,
        int newVersion,
        string? publicEventCode,
        string reasonRedacted,
        NormalizedTransitionMetadata metadata,
        IncidentGuardSnapshot incidents,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        object payloadValue = target == OrderStatus.FailedAttempt
            ? new
            {
                previous_status = source.ToContractValue(),
                new_status = target.ToContractValue(),
                reason_redacted = reasonRedacted,
                incident_id = metadata.IncidentId,
                attempt_stage = source.ToContractValue(),
                custody_acquired = incidents.RequestedIncidentCustodyAcquired,
            }
            : source == OrderStatus.Draft && target == OrderStatus.Confirmed
                ? new
                {
                    previous_status = source.ToContractValue(),
                    new_status = target.ToContractValue(),
                    reason_redacted = reasonRedacted,
                    restricted_goods_acknowledged = metadata.RestrictedGoodsAcknowledged,
                }
                : new
                {
                    previous_status = source.ToContractValue(),
                    new_status = target.ToContractValue(),
                    reason_redacted = reasonRedacted,
                };
        var payload = JsonSerializer.Serialize(payloadValue, JsonOptions);
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,operator_org_id,aggregate_version,event_type,
              public_event_code,payload,actor_id,occurred_at)
            VALUES (@id,@order,@owner,@operator,@version,'ORDER_STATUS_CHANGED',
                    @public_code,@payload,@actor,@occurred)
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, eventId));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, order.Id));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        command.Parameters.Add(P("operator", NpgsqlDbType.Uuid, order.OperatorOrganizationId));
        command.Parameters.Add(P("version", NpgsqlDbType.Integer, newVersion));
        command.Parameters.Add(P("public_code", NpgsqlDbType.Text, publicEventCode));
        command.Parameters.Add(P("payload", NpgsqlDbType.Jsonb, payload));
        command.Parameters.Add(P("actor", NpgsqlDbType.Uuid, actorId));
        command.Parameters.Add(P("occurred", NpgsqlDbType.TimestampTz, occurredAt));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "The transition event was not inserted.");
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid outboxId,
        Guid orderEventId,
        OrderRow order,
        OrderStatus source,
        OrderStatus target,
        int newVersion,
        string? publicEventCode,
        DriverAudienceCandidate? driverAudience,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var tenantContext = JsonSerializer.Serialize(new
        {
            organization_ids = new[] { order.OwnerOrganizationId },
        }, JsonOptions);
        var payload = JsonSerializer.Serialize(new
        {
            schema_version = "order-status-changed-v1",
            order_event_id = orderEventId,
            order_id = order.Id,
            public_order_id = order.PublicId,
            previous_status = source.ToContractValue(),
            new_status = target.ToContractValue(),
            occurred_at = occurredAt,
            public_event_code = publicEventCode,
            authorized_driver_id = driverAudience?.DriverId,
            assignment_id = driverAudience?.AssignmentId,
        }, JsonOptions);
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,
              priority,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,
              last_error,created_at,processed_at)
            VALUES (@id,@owner,@tenant,@topic,'Order',@order,@version,@payload,
                    50,'PENDING',0,@available,NULL,NULL,NULL,NULL,NULL,@created,NULL)
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, outboxId));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        command.Parameters.Add(P("tenant", NpgsqlDbType.Jsonb, tenantContext));
        command.Parameters.Add(P("topic", NpgsqlDbType.Text, OutboxTopic));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, order.Id));
        command.Parameters.Add(P("version", NpgsqlDbType.Integer, newVersion));
        command.Parameters.Add(P("payload", NpgsqlDbType.Jsonb, payload));
        command.Parameters.Add(P("available", NpgsqlDbType.TimestampTz, occurredAt));
        command.Parameters.Add(P("created", NpgsqlDbType.TimestampTz, occurredAt));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "The transition outbox event was not inserted.");
    }

    private async Task InsertTimelineOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid outboxId,
        Guid orderEventId,
        OrderRow order,
        OrderStatus target,
        int newVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var tenantContext = JsonSerializer.Serialize(new
        {
            organization_ids = new[] { order.OwnerOrganizationId },
        }, JsonOptions);
        var payload = JsonSerializer.Serialize(new
        {
            schema_version = "order-timeline-event-added-v1",
            order_id = order.Id,
            timeline_event_id = orderEventId,
            category = "ORDER_STATUS",
            summary = $"Order status changed to {target.ToContractValue()}.",
            occurred_at = occurredAt,
        }, JsonOptions);
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,
              priority,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,
              last_error,created_at,processed_at)
            VALUES (@id,@owner,@tenant,@topic,'Order',@order,@version,@payload,
                    50,'PENDING',0,@available,NULL,NULL,NULL,NULL,NULL,@created,NULL)
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, outboxId));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        command.Parameters.Add(P("tenant", NpgsqlDbType.Jsonb, tenantContext));
        command.Parameters.Add(P("topic", NpgsqlDbType.Text, TimelineOutboxTopic));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, order.Id));
        command.Parameters.Add(P("version", NpgsqlDbType.Integer, newVersion));
        command.Parameters.Add(P("payload", NpgsqlDbType.Jsonb, payload));
        command.Parameters.Add(P("available", NpgsqlDbType.TimestampTz, occurredAt));
        command.Parameters.Add(P("created", NpgsqlDbType.TimestampTz, occurredAt));
        RequireOne(
            await command.ExecuteNonQueryAsync(cancellationToken),
            "The transition timeline outbox event was not inserted.");
    }

    /// <summary>
    /// D8 / D8-OUTBOX-LANE-DISPATCH: a transition that closes an assignment asks Dispatch to react by
    /// writing one more outbox row in this same transaction. It names the assignment active under the
    /// order lock, so a late redelivery can never close a newer assignment. <c>orders.status-changed</c>
    /// is untouched and keeps its REALTIME route.
    /// </summary>
    private async Task InsertDispatchReactionOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderEventId,
        OrderRow order,
        OrderStatus source,
        OrderStatus target,
        int newVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (!DispatchReactionRequestPolicy.Requires(source, target))
        {
            return;
        }

        Guid? assignmentId;
        await using (var active = CreateCommand(
            connection,
            transaction,
            """
            SELECT a.id
            FROM dispatch.assignments a
            WHERE a.order_id=@order
              AND a.owner_org_id=@owner
              AND a.status IN ('ACCEPTED','ACTIVE')
            """))
        {
            active.Parameters.Add(P("order", NpgsqlDbType.Uuid, order.Id));
            active.Parameters.Add(P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
            assignmentId = await active.ExecuteScalarAsync(cancellationToken) as Guid?;
        }

        if (assignmentId is null)
        {
            return;
        }

        var tenantContext = JsonSerializer.Serialize(new
        {
            organization_ids = new[] { order.OwnerOrganizationId },
        }, JsonOptions);
        var payload = JsonSerializer.Serialize(new
        {
            schema_version = DispatchReactionRequestPolicy.SchemaVersion,
            order_event_id = orderEventId,
            order_id = order.Id,
            previous_status = source.ToContractValue(),
            new_status = target.ToContractValue(),
            occurred_at = occurredAt,
            assignment_id = assignmentId.Value,
        }, JsonOptions);
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,
              priority,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,
              last_error,created_at,processed_at)
            VALUES (@id,@owner,@tenant,@topic,'Order',@order,@version,@payload,
                    50,'PENDING',0,@available,NULL,NULL,NULL,NULL,NULL,@created,NULL)
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, Guid.NewGuid()));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        command.Parameters.Add(P("tenant", NpgsqlDbType.Jsonb, tenantContext));
        command.Parameters.Add(P("topic", NpgsqlDbType.Text, DispatchReactionRequestPolicy.Topic));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, order.Id));
        command.Parameters.Add(P("version", NpgsqlDbType.Integer, newVersion));
        command.Parameters.Add(P("payload", NpgsqlDbType.Jsonb, payload));
        command.Parameters.Add(P("available", NpgsqlDbType.TimestampTz, occurredAt));
        command.Parameters.Add(P("created", NpgsqlDbType.TimestampTz, occurredAt));
        RequireOne(
            await command.ExecuteNonQueryAsync(cancellationToken),
            "The Dispatch reaction outbox event was not inserted.");
    }

    private async Task<DriverAudienceCandidate?> ReadDriverAudienceCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ownerOrganizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT a.id,a.driver_id
            FROM dispatch.assignments a
            JOIN drivers.driver_profiles p
              ON p.id=a.driver_id
             AND p.org_id=a.owner_org_id
             AND p.driver_type=a.assignment_type
             AND p.status='ACTIVE'
            JOIN identity.users u ON u.id=p.user_id AND u.status='ACTIVE'
            JOIN organizations.organization_memberships m
              ON m.user_id=p.user_id
             AND m.organization_id=p.org_id
             AND m.role='DRIVER'
             AND m.status='ACTIVE'
            WHERE a.order_id=@order
              AND a.owner_org_id=@owner
              AND a.assignment_type IN ('OWN','EXTERNAL')
              AND a.status IN ('ACCEPTED','ACTIVE')
            """);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, ownerOrganizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var result = new DriverAudienceCandidate(reader.GetGuid(0), reader.GetGuid(1));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new OrderTransitionInfrastructureException(
                "More than one current driver audience candidate exists.");
        }

        return result;
    }

    private async Task CompleteIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TransitionOrderCommand transition,
        byte[] requestHash,
        Guid orderId,
        string responseJson,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE platform.idempotency_keys
            SET response_status=200,response_body=@response,resource_id=@resource,expires_at=@expires
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key AND request_hash=@hash
              AND response_status IS NULL AND response_body IS NULL AND resource_id IS NULL
            """);
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, transition.OrganizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, transition.IdempotencyKey));
        command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
        command.Parameters.Add(P("response", NpgsqlDbType.Jsonb, responseJson));
        command.Parameters.Add(P("resource", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("expires", NpgsqlDbType.TimestampTz, expiresAt));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "The transition idempotency record was not completed.");
    }

    private static bool HasMonetaryIntegrity(OrderRow order)
    {
        try
        {
            return order.SubtotalCents >= 0 &&
                order.DiscountCents >= 0 &&
                order.TaxCents >= 0 &&
                order.TotalCents >= 0 &&
                order.MinimumTotalCentsSnapshot >= 0 &&
                order.TotalCents == checked(order.SubtotalCents - order.DiscountCents + order.TaxCents) &&
                (order.TotalCents >= order.MinimumTotalCentsSnapshot || order.HasFinancialOverride);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static OrderResult ToResult(
        OrderRow order,
        string status,
        int version,
        DateTimeOffset? claimWindowEndsAt,
        DateTimeOffset? finalizedAt) =>
        new(
            order.Id,
            order.PublicId,
            order.OwnerOrganizationId,
            order.OperatorOrganizationId,
            status,
            new MoneyResult(order.Currency, checked(order.SubtotalCents - order.DiscountCents)),
            version,
            order.OriginLocationId,
            order.DestinationLocationId,
            order.ServiceType,
            order.QuoteId,
            order.CityId,
            order.ServiceAreaId,
            order.PricingTier,
            new MoneyResult(order.Currency, order.TotalCents),
            claimWindowEndsAt,
            finalizedAt);

    private NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = options.Value.CommandTimeoutSeconds,
        };

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type)
    {
        Value = value ?? DBNull.Value,
    };

    private static void RequireOne(int affected, string message)
    {
        if (affected != 1)
        {
            throw new OrderTransitionInfrastructureException(message);
        }
    }

    private sealed record IdempotencyRow(
        byte[] RequestHash,
        int? ResponseStatus,
        string? ResponseBody,
        Guid? ResourceId);

    private sealed record DriverAudienceCandidate(Guid AssignmentId, Guid DriverId);

    private sealed record OrderRow(
        Guid Id,
        string PublicId,
        Guid QuoteId,
        Guid OwnerOrganizationId,
        Guid? OperatorOrganizationId,
        Guid CityId,
        string Status,
        int Version,
        DateTimeOffset? ClaimWindowEndsAt,
        DateTimeOffset? FinalizedAt,
        long CodExpectedCents,
        long SubtotalCents,
        long DiscountCents,
        long TaxCents,
        long TotalCents,
        long MinimumTotalCentsSnapshot,
        bool HasFinancialOverride,
        string Currency,
        Guid OriginLocationId,
        Guid DestinationLocationId,
        string ServiceType,
        Guid? ServiceAreaId,
        string PricingTier);
}
