using System.Security.Cryptography;
using System.Text.Json;
using Dispatch.Application.Assignments;
using Dispatch.Application.ExternalOffers;
using Dispatch.Domain;
using Dispatch.Infrastructure.Assignments;
using Dispatch.Infrastructure.Persistence;
using Drivers.Application.Eligibility;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Orders;
using Orders.Domain;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;
using DispatchMoneyResult = Dispatch.Application.Assignments.MoneyResult;

namespace Dispatch.Infrastructure.ExternalOffers;

public sealed class PostgreSqlExternalOfferService(
    TenantTransactionContext<DispatchDbContext> transactionContext,
    IOptions<DispatchOptions> options,
    IOptions<DispatchDriverEligibilityOptions> eligibilityOptions,
    IDispatchAssignmentAuthorizer authorizer,
    IDispatchAuthorizationReader authorizationReader,
    IDispatchDriverEligibilityReader eligibilityReader,
    OrderTransitionGuardRegistry guardRegistry,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor,
    IClock clock) : IExternalOfferService
{
    public const string CreateIdempotencyScope = "EXT-001:CREATE_EXTERNAL_OFFER";
    public const string AcceptIdempotencyScope = "EXT-001:ACCEPT_EXTERNAL_OFFER";
    public const string OutboxTopic = "dispatch.external-offer-changed";
    private const string StatusOutboxTopic = "orders.status-changed";
    private const string TimelineOutboxTopic = "orders.timeline-event-added";
    private const string AssignmentOutboxTopic = "dispatch.assignment-changed";
    private const int PageSize = 50;
    private const int MaximumScanSize = 200;
    private const int MaximumAudienceSize = 500;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<ExternalOfferResult> CreateAsync(
        CreateExternalOfferCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!ExternalOfferInputPolicy.IsValid(command) ||
            !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey))
        {
            throw Conflict(ExternalOfferConflictCode.InvalidRequest);
        }

        command = command with { ExpiresAt = UtcMicrosecondPrecision.Normalize(command.ExpiresAt) };
        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        if (!ExternalOfferPolicy.CanCreate("READY_FOR_PICKUP", command.ExpiresAt, now))
        {
            throw Conflict(ExternalOfferConflictCode.InvalidRequest);
        }

        var requestHash = ExternalOfferCanonicalizer.ComputeSha256(command);
        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
                async (dbContext, token) =>
                {
                    var (connection, transaction) = GetDatabase(dbContext);
                    await EnsureDispatcherAuthorizedAsync(connection, transaction, command, token);
                    await AcquireIdempotencyLockAsync(
                        connection, transaction, command.OrganizationId,
                        CreateIdempotencyScope, command.IdempotencyKey, token);
                    var idempotency = await ReadIdempotencyAsync(
                        connection, transaction, command.OrganizationId,
                        CreateIdempotencyScope, command.IdempotencyKey, token);
                    if (idempotency is not null)
                    {
                        EnsureMatchingHash(idempotency.RequestHash, requestHash);
                        return await ReadCreateReplayAsync(
                            connection, transaction, command, idempotency, token);
                    }

                    await InsertIdempotencyAsync(
                        connection, transaction, command.OrganizationId,
                        CreateIdempotencyScope, command.IdempotencyKey, requestHash, now, token);
                    var order = await ReadOrderAsync(
                        connection, transaction, command.OrganizationId, command.OrderId, true, token);
                    if (order is null)
                    {
                        throw Conflict(ExternalOfferConflictCode.OrderUnavailable);
                    }
                    if (!ExternalOfferPolicy.CanCreate(order.Status, command.ExpiresAt, now) ||
                        await HasActiveAssignmentAsync(connection, transaction, order.Id, token))
                    {
                        throw Conflict(ExternalOfferConflictCode.InvalidOrderState);
                    }

                    await ExpireOpenOffersAsync(connection, transaction, order, now, token);
                    var offer = new ExternalOfferResult(
                        Guid.NewGuid(),
                        order.Id,
                        ExternalOfferStatus.Open.ToContractValue(),
                        new DispatchMoneyResult("MXN", command.CommissionCents),
                        command.ExpiresAt,
                        null,
                        null,
                        1);
                    await InsertOfferAsync(connection, transaction, command, order, offer, now, token);
                    var capacity = await ReadCapacityAsync(connection, transaction, order.Id, token);
                    if (capacity is null)
                    {
                        throw Conflict(ExternalOfferConflictCode.InvalidOrderState);
                    }
                    var audience = await ResolveEligibleDriverIdsAsync(
                        connection, transaction, command.ActorId, command.OrganizationId,
                        order, command.Constraints, capacity, now, token);
                    await InsertExternalOfferOutboxAsync(
                        connection, transaction, order.OwnerOrganizationId, offer, audience, now, token);
                    await WriteAuditAsync(
                        connection, transaction, command.ActorId, order.OwnerOrganizationId,
                        "EXTERNAL_OFFER_CREATED", "ExternalOffer", offer.Id, command.RequestId,
                        new
                        {
                            offer_id = offer.Id,
                            order_id = offer.OrderId,
                            commission_cents = command.CommissionCents,
                            expires_at = command.ExpiresAt,
                            eligible_constraints = command.Constraints,
                        },
                        now, token);
                    await CompleteIdempotencyAsync(
                        connection, transaction, command.OrganizationId,
                        CreateIdempotencyScope, command.IdempotencyKey, requestHash,
                        StatusCodes.Status201Created, offer.Id, offer, now, token);
                    return offer;
                },
                cancellationToken);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UniqueViolation &&
            exception.ConstraintName == "one_open_offer_per_order")
        {
            throw Conflict(ExternalOfferConflictCode.OpenOfferExists, exception);
        }
    }

    public Task<ExternalOfferPageResult> ListEligibleAsync(
        Guid actorId,
        Guid organizationId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new ExternalOfferForbiddenException();
        }

        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        var hasCursor = cursor is not null;
        if (hasCursor && !ExternalOfferCursorCodec.TryDecode(cursor, out cursorCreatedAt, out cursorId))
        {
            return Task.FromResult(new ExternalOfferPageResult([], null));
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        return transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(actorId, [organizationId]),
            async (dbContext, token) =>
            {
                var (connection, transaction) = GetDatabase(dbContext);
                var driverId = await ResolveExternalDriverIdAsync(
                    connection, transaction, actorId, organizationId, token);
                if (driverId is null)
                {
                    throw new ExternalOfferForbiddenException();
                }

                var candidates = await ReadCandidatesAsync(
                    connection, transaction, organizationId, hasCursor,
                    cursorCreatedAt, cursorId, now, token);
                var rowsToScan = candidates.Take(MaximumScanSize).ToArray();
                var snapshots = new Dictionary<(Guid CityId, Guid? ServiceAreaId), DriverEligibilitySnapshot?>();
                var results = new List<ExternalOfferResult>(PageSize);
                ExternalOfferCandidate? lastScanned = null;
                foreach (var candidate in rowsToScan)
                {
                    lastScanned = candidate;
                    var key = (candidate.Order.CityId, candidate.Order.ServiceAreaId);
                    if (!snapshots.TryGetValue(key, out var snapshot))
                    {
                        snapshot = await eligibilityReader.ReadAsync(
                            connection,
                            transaction,
                            new EvaluateExternalDriverEligibilityCommand(
                                actorId,
                                organizationId,
                                driverId.Value,
                                key.CityId,
                                key.ServiceAreaId,
                                candidate.Capacity,
                                now),
                            token);
                        snapshots[key] = snapshot;
                    }

                    if (snapshot is null ||
                        !candidate.Constraints.Allows(
                            snapshot.VehicleType,
                            candidate.Order.ServiceAreaId,
                            candidate.Order.CodExpectedCents))
                    {
                        continue;
                    }

                    var eligibility = DriverEligibilityPolicy.EvaluateExternal(
                        new EvaluateExternalDriverEligibilityCommand(
                            actorId,
                            organizationId,
                            driverId.Value,
                            candidate.Order.CityId,
                            candidate.Order.ServiceAreaId,
                            candidate.Capacity,
                            now),
                        snapshot with { ServiceAreaEligible = snapshot.ServiceAreaEligible },
                        eligibilityOptions.Value.ToPolicy());
                    if (!eligibility.IsEligible)
                    {
                        continue;
                    }

                    results.Add(candidate.Offer);
                    if (results.Count == PageSize)
                    {
                        break;
                    }
                }

                var moreCandidates = candidates.Count > MaximumScanSize ||
                    (results.Count == PageSize && lastScanned is not null &&
                     lastScanned != candidates[^1]);
                var nextCursor = moreCandidates && lastScanned is not null
                    ? ExternalOfferCursorCodec.Encode(lastScanned.CreatedAt, lastScanned.Offer.Id)
                    : null;
                return new ExternalOfferPageResult(results, nextCursor);
            },
            cancellationToken);
    }

    public async Task<AssignmentResult> AcceptAsync(
        AcceptExternalOfferCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!ExternalOfferInputPolicy.IsValid(command) ||
            !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey))
        {
            throw Conflict(ExternalOfferConflictCode.InvalidRequest);
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        var result = await transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
            async (dbContext, token) =>
            {
                var (connection, transaction) = GetDatabase(dbContext);
                var driverId = await ResolveExternalDriverIdAsync(
                    connection, transaction, command.ActorId, command.OrganizationId, token);
                if (driverId is null)
                {
                    throw new ExternalOfferForbiddenException();
                }

                var requestHash = ExternalOfferCanonicalizer.ComputeSha256(command, driverId.Value);
                await AcquireIdempotencyLockAsync(
                    connection, transaction, command.OrganizationId,
                    AcceptIdempotencyScope, command.IdempotencyKey, token);
                var idempotency = await ReadIdempotencyAsync(
                    connection, transaction, command.OrganizationId,
                    AcceptIdempotencyScope, command.IdempotencyKey, token);
                if (idempotency is not null)
                {
                    EnsureMatchingHash(idempotency.RequestHash, requestHash);
                    return await ReadAcceptReplayAsync(
                        connection, transaction, command, driverId.Value, idempotency, token);
                }

                await InsertIdempotencyAsync(
                    connection, transaction, command.OrganizationId,
                    AcceptIdempotencyScope, command.IdempotencyKey, requestHash, now, token);
                var state = await ReadOfferForAcceptAsync(
                    connection, transaction, command.OrganizationId, command.OfferId, token);
                if (state is null)
                {
                    throw Conflict(ExternalOfferConflictCode.OfferUnavailable);
                }
                if (!ExternalOfferPolicy.CanAccept(state.Offer.Status, state.Offer.ExpiresAt, now))
                {
                    throw Conflict(state.Offer.ExpiresAt <= now
                        ? ExternalOfferConflictCode.OfferExpired
                        : ExternalOfferConflictCode.OfferUnavailable);
                }
                if (state.Order.Status is not ("READY_FOR_PICKUP" or "RESCHEDULED") ||
                    await HasActiveAssignmentAsync(connection, transaction, state.Order.Id, token))
                {
                    throw Conflict(ExternalOfferConflictCode.ActiveAssignmentExists);
                }
                var snapshot = await eligibilityReader.ReadAsync(
                    connection,
                    transaction,
                    new EvaluateExternalDriverEligibilityCommand(
                        command.ActorId,
                        command.OrganizationId,
                        driverId.Value,
                        state.Order.CityId,
                        state.Order.ServiceAreaId,
                        state.Capacity,
                        now),
                    token);
                if (snapshot is null ||
                    !state.Constraints.Allows(
                        snapshot.VehicleType,
                        state.Order.ServiceAreaId,
                        state.Order.CodExpectedCents) ||
                    !DriverEligibilityPolicy.EvaluateExternal(
                        new EvaluateExternalDriverEligibilityCommand(
                            command.ActorId,
                            command.OrganizationId,
                            driverId.Value,
                            state.Order.CityId,
                            state.Order.ServiceAreaId,
                            state.Capacity,
                            now),
                        snapshot,
                        eligibilityOptions.Value.ToPolicy()).IsEligible)
                {
                    throw Conflict(ExternalOfferConflictCode.DriverIneligible);
                }

                return await AcceptWithinTransactionAsync(
                    connection, transaction, command, state, driverId.Value,
                    requestHash, now, token);
            },
            cancellationToken);
        return result;
    }

    private async Task<AssignmentResult> AcceptWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AcceptExternalOfferCommand command,
        ExternalOfferAcceptanceState state,
        Guid driverId,
        byte[] requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!OrderContractValues.TryParseOrderStatus(state.Order.Status, out var source) ||
            source is not (OrderStatus.ReadyForPickup or OrderStatus.Rescheduled))
        {
            throw Conflict(ExternalOfferConflictCode.InvalidOrderState);
        }

        var matrix = OrderTransitionMatrix.Evaluate(source, OrderStatus.Assigned, now, null, null);
        var guards = guardRegistry.Evaluate(new OrderTransitionGuardContext
        {
            Source = source,
            Target = OrderStatus.Assigned,
            Reason = "EXTERNAL_OFFER_ACCEPTED",
            OccurredAt = now,
            ClaimWindowEndsAt = null,
            FinalizedAt = null,
            CodExpectedCents = state.Order.CodExpectedCents,
            MonetaryIntegrityValid = true,
            Metadata = NormalizedTransitionMetadata.Empty,
            Assignment = new AssignmentGuardSnapshot(true, true, true, true),
        });
        if (!matrix.Allowed || !guards.Satisfied || state.Order.Version == int.MaxValue ||
            state.Offer.Version == int.MaxValue)
        {
            throw Conflict(ExternalOfferConflictCode.InvalidOrderState);
        }

        var assignmentId = Guid.NewGuid();
        var assignment = new Assignment(
            assignmentId,
            state.Order.Id,
            state.Order.OwnerOrganizationId,
            state.Order.OperatorOrganizationId,
            driverId,
            null,
            AssignmentType.External,
            AssignmentStatus.Accepted,
            state.Offer.Commission.AmountCents,
            now,
            now);
        var orderVersion = checked(state.Order.Version + 1);
        var offerVersion = checked(state.Offer.Version + 1);
        await InsertAssignmentAsync(connection, transaction, assignment, cancellationToken);
        await UpdateAcceptedOfferAsync(
            connection, transaction, state.Offer.Id, driverId, state.Offer.Version,
            offerVersion, now, cancellationToken);
        await UpdateOrderAsync(
            connection, transaction, state.Order, source, orderVersion, now, cancellationToken);
        var orderEventId = Guid.NewGuid();
        await InsertOrderEventAsync(
            connection, transaction, state.Order, assignmentId, source,
            orderVersion, command.ActorId, orderEventId, now, cancellationToken);
        await InsertOrderOutboxesAsync(
            connection, transaction, state.Order, assignment, source,
            orderVersion, orderEventId, now, cancellationToken);
        var acceptedOffer = state.Offer with
        {
            Status = ExternalOfferStatus.Accepted.ToContractValue(),
            AcceptedByDriverId = driverId,
            AcceptedAt = now,
            Version = offerVersion,
        };
        await InsertExternalOfferOutboxAsync(
            connection, transaction, state.Order.OwnerOrganizationId,
            acceptedOffer, [driverId], now, cancellationToken);
        await WriteAuditAsync(
            connection, transaction, command.ActorId, state.Order.OwnerOrganizationId,
            "EXTERNAL_OFFER_ACCEPTED", "ExternalOffer", state.Offer.Id, command.RequestId,
            new
            {
                offer_id = state.Offer.Id,
                order_id = state.Order.Id,
                driver_id = driverId,
                assignment_id = assignmentId,
                commission_cents = state.Offer.Commission.AmountCents,
                offer_version = offerVersion,
                order_version = orderVersion,
            },
            now, cancellationToken);
        await WriteAuditAsync(
            connection, transaction, command.ActorId, state.Order.OwnerOrganizationId,
            "ASSIGNMENT_CREATED", "Assignment", assignmentId, command.RequestId,
            new
            {
                assignment_id = assignmentId,
                order_id = state.Order.Id,
                driver_id = driverId,
                assignment_type = "EXTERNAL",
                status = "ACCEPTED",
                cost_cents = state.Offer.Commission.AmountCents,
            },
            now, cancellationToken);
        var result = new AssignmentResult(
            assignment.Id,
            assignment.OrderId,
            assignment.DriverId,
            assignment.Status.ToContractValue(),
            new DispatchMoneyResult("MXN", assignment.CostCents));
        await CompleteIdempotencyAsync(
            connection, transaction, command.OrganizationId,
            AcceptIdempotencyScope, command.IdempotencyKey, requestHash,
            StatusCodes.Status200OK, assignmentId, result, now, cancellationToken);
        return result;
    }

    private async Task EnsureDispatcherAuthorizedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateExternalOfferCommand command,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationReader.ReadAsync(
            connection,
            transaction,
            command.ActorId,
            command.OrganizationId,
            cancellationToken);
        if (!authorizer.IsAuthorized(new DispatchAssignmentAuthorizationContext(
                authorization.ActiveRole,
                authorization.UserActive,
                authorization.MembershipActive,
                command.MfaSatisfied)))
        {
            throw new ExternalOfferForbiddenException();
        }
    }

    private static async Task<Guid?> ResolveExternalDriverIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT p.id
            FROM drivers.driver_profiles p
            JOIN identity.users u ON u.id=p.user_id AND u.status='ACTIVE'
            JOIN organizations.organization_memberships m
              ON m.user_id=p.user_id AND m.organization_id=p.org_id
             AND m.role='DRIVER' AND m.status='ACTIVE'
            WHERE p.user_id=@actor AND p.org_id=@organization
              AND p.driver_type='EXTERNAL'
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(P("actor", NpgsqlDbType.Uuid, actorId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    private static async Task AcquireIdempotencyLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string scope,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0));",
            connection,
            transaction);
        command.Parameters.Add(P("key", NpgsqlDbType.Text, $"{organizationId:D}:{scope}:{key}"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IdempotencyRow?> ReadIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string scope,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT request_hash,response_status,response_body::text,resource_id
            FROM platform.idempotency_keys
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key
            """,
            connection,
            transaction);
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, scope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, key));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetFieldValue<byte[]>(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3))
            : null;
    }

    private static void EnsureMatchingHash(byte[] stored, byte[] actual)
    {
        if (!CryptographicOperations.FixedTimeEquals(stored, actual))
        {
            throw Conflict(ExternalOfferConflictCode.IdempotencyConflict);
        }
    }

    private async Task InsertIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string scope,
        string key,
        byte[] hash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO platform.idempotency_keys(
              owner_org_id,scope,idempotency_key,request_hash,response_status,
              response_body,resource_id,created_at,expires_at)
            VALUES (@owner,@scope,@key,@hash,NULL,NULL,NULL,@now,@expires)
            """);
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, scope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, key));
        command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, hash));
        command.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        command.Parameters.Add(P("expires", NpgsqlDbType.TimestampTz,
            now.AddMinutes(options.Value.IdempotencyLifetimeMinutes)));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "Idempotency reservation failed.");
    }

    private async Task CompleteIdempotencyAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string scope,
        string key,
        byte[] hash,
        int status,
        Guid resourceId,
        T response,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE platform.idempotency_keys
            SET response_status=@status,response_body=@body,resource_id=@resource,expires_at=@expires
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key
              AND request_hash=@hash AND response_status IS NULL
              AND response_body IS NULL AND resource_id IS NULL
            """);
        command.Parameters.Add(P("status", NpgsqlDbType.Integer, status));
        command.Parameters.Add(P("body", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(response, JsonOptions)));
        command.Parameters.Add(P("resource", NpgsqlDbType.Uuid, resourceId));
        command.Parameters.Add(P("expires", NpgsqlDbType.TimestampTz,
            now.AddMinutes(options.Value.IdempotencyLifetimeMinutes)));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, scope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, key));
        command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, hash));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "Idempotency completion failed.");
    }

    private async Task<ExternalOfferResult> ReadCreateReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateExternalOfferCommand command,
        IdempotencyRow idempotency,
        CancellationToken cancellationToken)
    {
        if (idempotency.ResponseStatus != StatusCodes.Status201Created ||
            idempotency.ResourceId is not { } resourceId ||
            string.IsNullOrWhiteSpace(idempotency.ResponseBody))
        {
            throw Conflict(ExternalOfferConflictCode.InconsistentReplayEvidence);
        }
        ExternalOfferResult result;
        try
        {
            result = JsonSerializer.Deserialize<ExternalOfferResult>(idempotency.ResponseBody, JsonOptions)
                ?? throw Conflict(ExternalOfferConflictCode.InconsistentReplayEvidence);
        }
        catch (JsonException exception)
        {
            throw Conflict(ExternalOfferConflictCode.InconsistentReplayEvidence, exception);
        }

        await using var evidence = new NpgsqlCommand(
            """
            SELECT count(*)::integer
            FROM dispatch.external_offers
            WHERE id=@id AND order_id=@order AND commission_cents=@commission
              AND expires_at=@expires
            """,
            connection,
            transaction);
        evidence.Parameters.Add(P("id", NpgsqlDbType.Uuid, resourceId));
        evidence.Parameters.Add(P("order", NpgsqlDbType.Uuid, command.OrderId));
        evidence.Parameters.Add(P("commission", NpgsqlDbType.Bigint, command.CommissionCents));
        evidence.Parameters.Add(P("expires", NpgsqlDbType.TimestampTz, command.ExpiresAt));
        var count = (int)(await evidence.ExecuteScalarAsync(cancellationToken))!;
        if (count != 1 || result.Id != resourceId || result.OrderId != command.OrderId ||
            result.Commission.AmountCents != command.CommissionCents || result.ExpiresAt != command.ExpiresAt)
        {
            throw Conflict(ExternalOfferConflictCode.InconsistentReplayEvidence);
        }
        return result;
    }

    private async Task<AssignmentResult> ReadAcceptReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AcceptExternalOfferCommand command,
        Guid driverId,
        IdempotencyRow idempotency,
        CancellationToken cancellationToken)
    {
        if (idempotency.ResponseStatus != StatusCodes.Status200OK ||
            idempotency.ResourceId is not { } resourceId ||
            string.IsNullOrWhiteSpace(idempotency.ResponseBody))
        {
            throw Conflict(ExternalOfferConflictCode.InconsistentReplayEvidence);
        }
        AssignmentResult result;
        try
        {
            result = JsonSerializer.Deserialize<AssignmentResult>(idempotency.ResponseBody, JsonOptions)
                ?? throw Conflict(ExternalOfferConflictCode.InconsistentReplayEvidence);
        }
        catch (JsonException exception)
        {
            throw Conflict(ExternalOfferConflictCode.InconsistentReplayEvidence, exception);
        }

        await using var evidence = new NpgsqlCommand(
            """
            SELECT count(*)::integer
            FROM dispatch.external_offers o
            JOIN dispatch.assignments a
              ON a.id=@assignment AND a.order_id=o.order_id
             AND a.driver_id=o.accepted_by_driver_id
            WHERE o.id=@offer AND o.status='ACCEPTED'
              AND o.accepted_by_driver_id=@driver
              AND a.assignment_type='EXTERNAL' AND a.status IN ('ACCEPTED','ACTIVE')
              AND a.cost_cents=o.commission_cents
            """,
            connection,
            transaction);
        evidence.Parameters.Add(P("assignment", NpgsqlDbType.Uuid, resourceId));
        evidence.Parameters.Add(P("offer", NpgsqlDbType.Uuid, command.OfferId));
        evidence.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
        var count = (int)(await evidence.ExecuteScalarAsync(cancellationToken))!;
        if (count != 1 || result.Id != resourceId || result.DriverId != driverId)
        {
            throw Conflict(ExternalOfferConflictCode.InconsistentReplayEvidence);
        }
        return result;
    }

    private static async Task<OrderRow?> ReadOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid orderId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql =
            """
            SELECT id,owner_org_id,operator_org_id,city_id,service_area_id,status,
                   version,public_id,cod_expected_cents
            FROM orders.orders
            WHERE id=@order AND (owner_org_id=@organization OR operator_org_id=@organization)
            """ + (forUpdate ? " FOR UPDATE" : string.Empty);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadOrder(reader) : null;
    }

    private async Task ExpireOpenOffersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrderRow order,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE dispatch.external_offers
            SET status='EXPIRED',version=version+1
            WHERE order_id=@order AND status='OPEN' AND expires_at<=@now
            RETURNING id,order_id,commission_cents,expires_at,version
            """);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, order.Id));
        command.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        var expired = new List<ExternalOfferResult>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                expired.Add(new(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    ExternalOfferStatus.Expired.ToContractValue(),
                    new DispatchMoneyResult("MXN", reader.GetInt64(2)),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    null,
                    null,
                    reader.GetInt32(4)));
            }
        }
        foreach (var offer in expired)
        {
            await InsertExternalOfferOutboxAsync(
                connection, transaction, order.OwnerOrganizationId, offer, [], now, cancellationToken);
        }
    }

    private async Task InsertOfferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateExternalOfferCommand command,
        OrderRow order,
        ExternalOfferResult offer,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var insert = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO dispatch.external_offers(
              id,order_id,owner_org_id,operator_org_id,commission_cents,
              eligible_constraints,status,accepted_by_driver_id,accepted_at,
              version,expires_at,created_at)
            VALUES (@id,@order,@owner,@operator,@commission,@constraints,
                    'OPEN',NULL,NULL,1,@expires,@created)
            """);
        insert.Parameters.Add(P("id", NpgsqlDbType.Uuid, offer.Id));
        insert.Parameters.Add(P("order", NpgsqlDbType.Uuid, order.Id));
        insert.Parameters.Add(P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        insert.Parameters.Add(P("operator", NpgsqlDbType.Uuid, order.OperatorOrganizationId));
        insert.Parameters.Add(P("commission", NpgsqlDbType.Bigint, command.CommissionCents));
        insert.Parameters.Add(P("constraints", NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(command.Constraints, JsonOptions)));
        insert.Parameters.Add(P("expires", NpgsqlDbType.TimestampTz, command.ExpiresAt));
        insert.Parameters.Add(P("created", NpgsqlDbType.TimestampTz, now));
        RequireOne(await insert.ExecuteNonQueryAsync(cancellationToken), "External offer insert failed.");
    }

    private async Task<IReadOnlyList<ExternalOfferCandidate>> ReadCandidatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        bool hasCursor,
        DateTimeOffset cursorCreatedAt,
        Guid cursorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT eo.id,eo.order_id,eo.commission_cents,eo.expires_at,eo.version,
                   eo.eligible_constraints::text,eo.created_at,
                   o.owner_org_id,o.operator_org_id,o.city_id,o.service_area_id,
                   o.status,o.version,o.public_id,o.cod_expected_cents,
                   COALESCE(package_rows.value,'[]'::jsonb)::text
            FROM dispatch.external_offers eo
            JOIN orders.orders o ON o.id=eo.order_id
            LEFT JOIN LATERAL (
              SELECT jsonb_agg(jsonb_build_object(
                       'weight_grams',item.weight_grams,
                       'dimensions_mm',item.dimensions_mm) ORDER BY item.id) AS value
              FROM orders.package_items item WHERE item.order_id=o.id
            ) package_rows ON true
            WHERE (eo.owner_org_id=@organization OR eo.operator_org_id=@organization)
              AND eo.status='OPEN' AND eo.expires_at>@now
              AND o.status IN ('READY_FOR_PICKUP','RESCHEDULED')
              AND NOT EXISTS (
                SELECT 1 FROM dispatch.assignments a
                WHERE a.order_id=o.id AND a.status IN ('ACCEPTED','ACTIVE'))
              AND (@has_cursor=false OR eo.created_at<@cursor_created_at
                   OR (eo.created_at=@cursor_created_at AND eo.id<@cursor_id))
            ORDER BY eo.created_at DESC,eo.id DESC
            LIMIT @take
            """);
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        command.Parameters.Add(P("has_cursor", NpgsqlDbType.Boolean, hasCursor));
        command.Parameters.Add(P("cursor_created_at", NpgsqlDbType.TimestampTz, cursorCreatedAt));
        command.Parameters.Add(P("cursor_id", NpgsqlDbType.Uuid, cursorId));
        command.Parameters.Add(P("take", NpgsqlDbType.Integer, MaximumScanSize + 1));
        var results = new List<ExternalOfferCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var constraints = ParseConstraints(reader.GetString(5));
            var capacity = ParseCapacity(reader.GetString(15));
            if (constraints is null || capacity is null)
            {
                continue;
            }
            var order = new OrderRow(
                reader.GetGuid(1),
                reader.GetGuid(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.GetGuid(9),
                reader.IsDBNull(10) ? null : reader.GetGuid(10),
                reader.GetString(11),
                reader.GetInt32(12),
                reader.GetString(13),
                reader.GetInt64(14));
            results.Add(new(
                new ExternalOfferResult(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    ExternalOfferStatus.Open.ToContractValue(),
                    new DispatchMoneyResult("MXN", reader.GetInt64(2)),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    null,
                    null,
                    reader.GetInt32(4)),
                constraints,
                order,
                capacity,
                reader.GetFieldValue<DateTimeOffset>(6)));
        }
        return results;
    }

    private async Task<ExternalOfferAcceptanceState?> ReadOfferForAcceptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid offerId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT eo.id,eo.order_id,eo.status,eo.commission_cents,eo.expires_at,
                   eo.accepted_by_driver_id,eo.accepted_at,eo.version,
                   eo.eligible_constraints::text,
                   o.owner_org_id,o.operator_org_id,o.city_id,o.service_area_id,
                   o.status,o.version,o.public_id,o.cod_expected_cents,
                   COALESCE(package_rows.value,'[]'::jsonb)::text
            FROM dispatch.external_offers eo
            JOIN orders.orders o ON o.id=eo.order_id
            LEFT JOIN LATERAL (
              SELECT jsonb_agg(jsonb_build_object(
                       'weight_grams',item.weight_grams,
                       'dimensions_mm',item.dimensions_mm) ORDER BY item.id) AS value
              FROM orders.package_items item WHERE item.order_id=o.id
            ) package_rows ON true
            WHERE eo.id=@offer
              AND (eo.owner_org_id=@organization OR eo.operator_org_id=@organization)
            FOR UPDATE OF eo,o
            """);
        command.Parameters.Add(P("offer", NpgsqlDbType.Uuid, offerId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var constraints = ParseConstraints(reader.GetString(8));
        var capacity = ParseCapacity(reader.GetString(17));
        if (constraints is null || capacity is null)
        {
            throw Conflict(ExternalOfferConflictCode.DriverIneligible);
        }
        return new(
            new ExternalOfferResult(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                new DispatchMoneyResult("MXN", reader.GetInt64(3)),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetInt32(7)),
            constraints,
            new OrderRow(
                reader.GetGuid(1),
                reader.GetGuid(9),
                reader.IsDBNull(10) ? null : reader.GetGuid(10),
                reader.GetGuid(11),
                reader.IsDBNull(12) ? null : reader.GetGuid(12),
                reader.GetString(13),
                reader.GetInt32(14),
                reader.GetString(15),
                reader.GetInt64(16)),
            capacity);
    }

    private static ExternalOfferConstraints? ParseConstraints(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize<ExternalOfferConstraints>(json, JsonOptions);
            return value is not null && value.IsValid() ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DriverCapacityRequirement? ParseCapacity(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var packages = new List<PackageCapacityItem>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("weight_grams", out var weight) ||
                    !weight.TryGetInt64(out var parsedGrams) ||
                    !item.TryGetProperty("dimensions_mm", out var dimensions) ||
                    !PostgreSqlAssignmentToOrderCoordinator.TryReadDimensions(
                        dimensions.GetRawText(), out var length, out var width, out var height))
                {
                    return null;
                }
                packages.Add(new PackageCapacityItem(parsedGrams, length, width, height));
            }
            return PackageCapacityAggregator.TryAggregate(packages, out var capacity) ? capacity : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<DriverCapacityRequirement?> ReadCapacityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT COALESCE(jsonb_agg(jsonb_build_object(
                     'weight_grams',p.weight_grams,
                     'dimensions_mm',p.dimensions_mm) ORDER BY p.id),'[]'::jsonb)::text
            FROM orders.package_items p WHERE p.order_id=@order
            """,
            connection,
            transaction);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : ParseCapacity(json);
    }

    private async Task<IReadOnlyList<Guid>> ResolveEligibleDriverIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        OrderRow order,
        ExternalOfferConstraints constraints,
        DriverCapacityRequirement capacity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidates = await eligibilityReader.ReadExternalCandidatesAsync(
            connection,
            transaction,
            organizationId,
            order.CityId,
            order.ServiceAreaId,
            MaximumAudienceSize + 1,
            cancellationToken);
        if (candidates.Count > MaximumAudienceSize)
        {
            throw new ExternalOfferInfrastructureException("External offer audience exceeds the bounded fanout.");
        }

        var eligible = new List<Guid>();
        foreach (var snapshot in candidates)
        {
            var eligibilityCommand = new EvaluateExternalDriverEligibilityCommand(
                actorId,
                organizationId,
                snapshot.DriverId,
                order.CityId,
                order.ServiceAreaId,
                capacity,
                now);
            if (constraints.Allows(snapshot.VehicleType, order.ServiceAreaId, order.CodExpectedCents) &&
                DriverEligibilityPolicy.EvaluateExternal(
                    eligibilityCommand, snapshot, eligibilityOptions.Value.ToPolicy()).IsEligible)
            {
                eligible.Add(snapshot.DriverId);
            }
        }
        return eligible;
    }

    private static async Task<bool> HasActiveAssignmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
              SELECT 1 FROM dispatch.assignments
              WHERE order_id=@order AND status IN ('ACCEPTED','ACTIVE'))
            """,
            connection,
            transaction);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task InsertAssignmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Assignment assignment,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,operator_org_id,driver_id,route_id,
              assignment_type,status,cost_cents,accepted_at,created_at)
            VALUES (@id,@order,@owner,@operator,@driver,NULL,
                    'EXTERNAL','ACCEPTED',@cost,@accepted,@created)
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, assignment.Id));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, assignment.OrderId));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, assignment.OwnerOrganizationId));
        command.Parameters.Add(P("operator", NpgsqlDbType.Uuid, assignment.OperatorOrganizationId));
        command.Parameters.Add(P("driver", NpgsqlDbType.Uuid, assignment.DriverId));
        command.Parameters.Add(P("cost", NpgsqlDbType.Bigint, assignment.CostCents));
        command.Parameters.Add(P("accepted", NpgsqlDbType.TimestampTz, assignment.AcceptedAt));
        command.Parameters.Add(P("created", NpgsqlDbType.TimestampTz, assignment.CreatedAt));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "External assignment insert failed.");
    }

    private async Task UpdateAcceptedOfferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid offerId,
        Guid driverId,
        int currentVersion,
        int newVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE dispatch.external_offers
            SET status='ACCEPTED',accepted_by_driver_id=@driver,
                accepted_at=@now,version=@new_version
            WHERE id=@offer AND status='OPEN' AND version=@current_version
              AND expires_at>@now
            """);
        command.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
        command.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        command.Parameters.Add(P("new_version", NpgsqlDbType.Integer, newVersion));
        command.Parameters.Add(P("offer", NpgsqlDbType.Uuid, offerId));
        command.Parameters.Add(P("current_version", NpgsqlDbType.Integer, currentVersion));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(ExternalOfferConflictCode.ConcurrencyConflict);
        }
    }

    private async Task UpdateOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrderRow order,
        OrderStatus source,
        int newVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE orders.orders
            SET status='ASSIGNED',version=@new_version,updated_at=@now
            WHERE id=@order AND status=@source AND version=@current_version
            """);
        command.Parameters.Add(P("new_version", NpgsqlDbType.Integer, newVersion));
        command.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, order.Id));
        command.Parameters.Add(P("source", NpgsqlDbType.Text, source.ToContractValue()));
        command.Parameters.Add(P("current_version", NpgsqlDbType.Integer, order.Version));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(ExternalOfferConflictCode.ConcurrencyConflict);
        }
    }

    private async Task InsertOrderEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrderRow order,
        Guid assignmentId,
        OrderStatus source,
        int newVersion,
        Guid actorId,
        Guid eventId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            previous_status = source.ToContractValue(),
            new_status = "ASSIGNED",
            assignment_id = assignmentId,
            reason_redacted = "EXTERNAL_OFFER_ACCEPTED",
        }, JsonOptions);
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,operator_org_id,aggregate_version,event_type,
              public_event_code,payload,actor_id,occurred_at)
            VALUES (@id,@order,@owner,@operator,@version,'ORDER_STATUS_CHANGED',
                    NULL,@payload,@actor,@now)
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, eventId));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, order.Id));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        command.Parameters.Add(P("operator", NpgsqlDbType.Uuid, order.OperatorOrganizationId));
        command.Parameters.Add(P("version", NpgsqlDbType.Integer, newVersion));
        command.Parameters.Add(P("payload", NpgsqlDbType.Jsonb, payload));
        command.Parameters.Add(P("actor", NpgsqlDbType.Uuid, actorId));
        command.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "External order event insert failed.");
    }

    private async Task InsertOrderOutboxesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrderRow order,
        Assignment assignment,
        OrderStatus source,
        int orderVersion,
        Guid eventId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var tenant = JsonSerializer.Serialize(new
        {
            organization_ids = new[] { order.OwnerOrganizationId },
        }, JsonOptions);
        var statusPayload = JsonSerializer.Serialize(new
        {
            schema_version = "order-status-changed-v1",
            order_event_id = eventId,
            order_id = order.Id,
            public_order_id = order.PublicId,
            previous_status = source.ToContractValue(),
            new_status = "ASSIGNED",
            occurred_at = now,
            public_event_code = (string?)null,
            authorized_driver_id = assignment.DriverId,
            assignment_id = assignment.Id,
        }, JsonOptions);
        var timelinePayload = JsonSerializer.Serialize(new
        {
            schema_version = "order-timeline-event-added-v1",
            order_id = order.Id,
            timeline_event_id = eventId,
            category = "ORDER_STATUS",
            summary = "Order status changed to ASSIGNED.",
            occurred_at = now,
        }, JsonOptions);
        var assignmentPayload = JsonSerializer.Serialize(new
        {
            schema_version = "assignment-changed-v1",
            order_id = order.Id,
            assignment_id = assignment.Id,
            driver_id = assignment.DriverId,
            assignment_status = "ACCEPTED",
            occurred_at = now,
        }, JsonOptions);
        await InsertOutboxAsync(connection, transaction, Guid.NewGuid(), order.OwnerOrganizationId,
            tenant, StatusOutboxTopic, "Order", order.Id, orderVersion, statusPayload, now, cancellationToken);
        await InsertOutboxAsync(connection, transaction, Guid.NewGuid(), order.OwnerOrganizationId,
            tenant, TimelineOutboxTopic, "Order", order.Id, orderVersion, timelinePayload, now, cancellationToken);
        await InsertOutboxAsync(connection, transaction, Guid.NewGuid(), order.OwnerOrganizationId,
            tenant, AssignmentOutboxTopic, "Order", order.Id, orderVersion, assignmentPayload, now, cancellationToken);
    }

    private async Task InsertExternalOfferOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ownerOrganizationId,
        ExternalOfferResult offer,
        IReadOnlyList<Guid> audienceDriverIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var tenant = JsonSerializer.Serialize(new
        {
            organization_ids = new[] { ownerOrganizationId },
        }, JsonOptions);
        var payload = JsonSerializer.Serialize(new
        {
            schema_version = "external-offer-changed-v1",
            offer_id = offer.Id,
            status = offer.Status,
            commission_cents = offer.Commission.AmountCents,
            expires_at = offer.ExpiresAt,
            audience_driver_ids = audienceDriverIds,
        }, JsonOptions);
        await InsertOutboxAsync(
            connection, transaction, Guid.NewGuid(), ownerOrganizationId,
            tenant, OutboxTopic, "ExternalOffer", offer.Id, offer.Version,
            payload, now, cancellationToken);
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        Guid ownerOrganizationId,
        string tenantContext,
        string topic,
        string aggregateType,
        Guid aggregateId,
        int aggregateVersion,
        string payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,
              aggregate_version,payload,priority,status,attempts,available_at,
              locked_at,locked_by,lease_token,lease_expires_at,last_error,created_at,processed_at)
            VALUES (@id,@owner,@tenant,@topic,@aggregate_type,@aggregate_id,
                    @version,@payload,50,'PENDING',0,@now,
                    NULL,NULL,NULL,NULL,NULL,@now,NULL)
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, id));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, ownerOrganizationId));
        command.Parameters.Add(P("tenant", NpgsqlDbType.Jsonb, tenantContext));
        command.Parameters.Add(P("topic", NpgsqlDbType.Text, topic));
        command.Parameters.Add(P("aggregate_type", NpgsqlDbType.Text, aggregateType));
        command.Parameters.Add(P("aggregate_id", NpgsqlDbType.Uuid, aggregateId));
        command.Parameters.Add(P("version", NpgsqlDbType.Integer, aggregateVersion));
        command.Parameters.Add(P("payload", NpgsqlDbType.Jsonb, payload));
        command.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "External offer outbox insert failed.");
    }

    private async Task WriteAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        string action,
        string resourceType,
        Guid resourceId,
        string? requestId,
        object payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var element = JsonSerializer.SerializeToElement(payload, JsonOptions);
        await auditWriter.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                Guid.NewGuid(),
                organizationId,
                actorId,
                action,
                resourceType,
                resourceId,
                requestId,
                auditRedactor.Redact(element),
                now),
            cancellationToken);
    }

    private NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = options.Value.CommandTimeoutSeconds,
        };

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    private static OrderRow ReadOrder(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.IsDBNull(2) ? null : reader.GetGuid(2),
        reader.GetGuid(3),
        reader.IsDBNull(4) ? null : reader.GetGuid(4),
        reader.GetString(5),
        reader.GetInt32(6),
        reader.GetString(7),
        reader.GetInt64(8));

    private static ExternalOfferConflictException Conflict(
        ExternalOfferConflictCode code,
        Exception? exception = null) => new(code, exception);

    private static void RequireOne(int affected, string message)
    {
        if (affected != 1)
        {
            throw new ExternalOfferInfrastructureException(message);
        }
    }

    private static (NpgsqlConnection Connection, NpgsqlTransaction Transaction) GetDatabase(
        DispatchDbContext dbContext) =>
        ((NpgsqlConnection)dbContext.Database.GetDbConnection(),
         (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction());

    private sealed record IdempotencyRow(
        byte[] RequestHash,
        int? ResponseStatus,
        string? ResponseBody,
        Guid? ResourceId);

    private sealed record OrderRow(
        Guid Id,
        Guid OwnerOrganizationId,
        Guid? OperatorOrganizationId,
        Guid CityId,
        Guid? ServiceAreaId,
        string Status,
        int Version,
        string PublicId,
        long CodExpectedCents);

    private sealed record ExternalOfferCandidate(
        ExternalOfferResult Offer,
        ExternalOfferConstraints Constraints,
        OrderRow Order,
        DriverCapacityRequirement Capacity,
        DateTimeOffset CreatedAt);

    private sealed record ExternalOfferAcceptanceState(
        ExternalOfferResult Offer,
        ExternalOfferConstraints Constraints,
        OrderRow Order,
        DriverCapacityRequirement Capacity);
}
