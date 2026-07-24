using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using Dispatch.Application.Assignments;
using Dispatch.Domain;
using Dispatch.Infrastructure.Persistence;
using Drivers.Application.Eligibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
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

namespace Dispatch.Infrastructure.Assignments;

public sealed class PostgreSqlAssignmentToOrderCoordinator(
    TenantTransactionContext<DispatchDbContext> transactionContext,
    IOptions<DispatchOptions> options,
    IOptions<DispatchDriverEligibilityOptions> driverEligibilityOptions,
    IDispatchAssignmentAuthorizer authorizer,
    IDispatchAuthorizationReader authorizationReader,
    IDispatchDriverEligibilityReader eligibilityReader,
    IAssignmentReplayEvidenceReader replayEvidenceReader,
    OrderTransitionGuardRegistry guardRegistry,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor,
    IAssignmentFailureInjector failureInjector,
    IClock clock,
    ILogger<PostgreSqlAssignmentToOrderCoordinator> logger) : IAssignmentService
{
    public const string IdempotencyScope = "DSP-002:ASSIGN_OWN_DRIVER";
    public const string CoordinationFlow = "assignment_to_order_status_event";
    private const string OutboxTopic = "orders.status-changed";
    private const string InternalReason = "MANUAL_OWN_DRIVER_ASSIGNMENT";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private static readonly Meter Meter = new("Paqueteria.Dispatch");
    private static readonly Counter<long> CreatedCounter = Meter.CreateCounter<long>("dispatch.assignment.created");
    private static readonly Counter<long> ConflictCounter = Meter.CreateCounter<long>("dispatch.assignment.conflict");
    private static readonly Counter<long> IneligibleCounter = Meter.CreateCounter<long>("dispatch.assignment.ineligible");
    private static readonly Counter<long> ReplayCounter = Meter.CreateCounter<long>("dispatch.assignment.replay");

    public async Task<AssignmentResult> CreateOwnDriverAssignmentAsync(
        CreateOwnDriverAssignmentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!AssignmentInputPolicy.IsValid(command) ||
            !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey))
        {
            throw Conflict(AssignmentConflictCode.InvalidRequest);
        }

        var occurredAt = clock.UtcNow;
        var requestHash = AssignmentCanonicalizer.ComputeSha256(command);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
                async (dbContext, token) =>
                {
                    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                    var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!
                        .GetDbTransaction();
                    await AcquireIdempotencyLockAsync(connection, transaction, command, token);
                    var idempotency = await FindIdempotencyAsync(connection, transaction, command, token);
                    if (idempotency is not null)
                    {
                        if (!CryptographicOperations.FixedTimeEquals(
                                idempotency.RequestHash,
                                requestHash))
                        {
                            throw Conflict(AssignmentConflictCode.IdempotencyConflict);
                        }

                        var completed = ReadCompletedResponse(idempotency);
                        if (completed is not null)
                        {
                            var evidence = await replayEvidenceReader.ReadAsync(
                                connection,
                                transaction,
                                command.OrganizationId,
                                completed.Id,
                                command.OrderId,
                                command.DriverId,
                                command.CostCents!.Value,
                                token);
                            if (idempotency.ResourceId is not { } resourceId ||
                                !AssignmentReplayPolicy.IsConsistent(
                                    command,
                                    completed,
                                    resourceId,
                                    evidence))
                            {
                                throw Conflict(AssignmentConflictCode.InconsistentReplayEvidence);
                            }

                            await EnsureAuthorizedAsync(connection, transaction, command, token);
                            ReplayCounter.Add(1);
                            LogResult(command, completed.Id, "replay", stopwatch.Elapsed);
                            return completed;
                        }

                        throw Conflict(AssignmentConflictCode.IdempotencyConflict);
                    }

                    await EnsureAuthorizedAsync(connection, transaction, command, token);
                    await InsertIdempotencyReservationAsync(
                        connection,
                        transaction,
                        command,
                        requestHash,
                        occurredAt,
                        token);
                    await failureInjector.OnStageAsync(
                        AssignmentTransactionStage.IdempotencyReserved,
                        token);

                    var order = await ReadOrderForUpdateAsync(
                        connection,
                        transaction,
                        command.OrganizationId,
                        command.OrderId,
                        token) ?? throw new AssignmentForbiddenException();
                    await failureInjector.OnStageAsync(AssignmentTransactionStage.OrderLocked, token);

                    if (!OrderContractValues.TryParseOrderStatus(order.Status, out var source) ||
                        source is not (OrderStatus.ReadyForPickup or OrderStatus.Rescheduled) ||
                        !ManualOwnAssignmentPolicy.Evaluate(
                            command.OrderId,
                            command.DriverId,
                            AssignmentType.Own,
                            command.RouteId,
                            command.CostCents!.Value,
                            order.Status).Allowed)
                    {
                        throw Conflict(AssignmentConflictCode.InvalidOrderState);
                    }

                    if (await HasActiveAssignmentAsync(connection, transaction, order.Id, token))
                    {
                        throw Conflict(AssignmentConflictCode.ActiveAssignmentExists);
                    }

                    var packages = await ReadPackagesAsync(connection, transaction, order.Id, token);
                    await failureInjector.OnStageAsync(AssignmentTransactionStage.PackagesRead, token);
                    if (!PackageCapacityAggregator.TryAggregate(packages, out var capacity) ||
                        capacity is null)
                    {
                        throw Conflict(AssignmentConflictCode.CapacityInsufficient);
                    }

                    var eligibilityCommand = new EvaluateOwnDriverEligibilityCommand(
                        command.ActorId,
                        command.OrganizationId,
                        command.DriverId,
                        order.CityId,
                        order.ServiceAreaId,
                        capacity,
                        occurredAt);
                    var snapshot = await eligibilityReader.ReadAsync(
                        connection,
                        transaction,
                        eligibilityCommand,
                        token);
                    if (snapshot is null)
                    {
                        throw new AssignmentForbiddenException();
                    }

                    var eligibility = DriverEligibilityPolicy.Evaluate(
                        eligibilityCommand,
                        snapshot,
                        driverEligibilityOptions.Value.ToPolicy());
                    await failureInjector.OnStageAsync(
                        AssignmentTransactionStage.EligibilityEvaluated,
                        token);
                    if (!eligibility.IsEligible)
                    {
                        IneligibleCounter.Add(1);
                        throw EligibilityConflict(eligibility);
                    }

                    var assignmentId = Guid.NewGuid();
                    var eventId = Guid.NewGuid();
                    var outboxId = Guid.NewGuid();
                    Guid? operatorOrganizationId;
                    try
                    {
                        operatorOrganizationId = ManualOwnAssignmentPolicy.DeriveOperatorOrganization(
                            command.OrganizationId,
                            order.OwnerOrganizationId,
                            order.OperatorOrganizationId);
                    }
                    catch (InvalidOperationException)
                    {
                        throw new AssignmentForbiddenException();
                    }

                    var assignment = ManualOwnAssignmentPolicy.CreateAccepted(
                        assignmentId,
                        order.Id,
                        order.OwnerOrganizationId,
                        operatorOrganizationId,
                        command.DriverId,
                        command.CostCents.Value,
                        occurredAt);
                    await InsertAssignmentAsync(connection, transaction, assignment, token);
                    await failureInjector.OnStageAsync(
                        AssignmentTransactionStage.AssignmentInserted,
                        token);

                    var matrix = OrderTransitionMatrix.Evaluate(
                        source,
                        OrderStatus.Assigned,
                        occurredAt,
                        null,
                        null);
                    var guards = guardRegistry.Evaluate(new OrderTransitionGuardContext
                    {
                        Source = source,
                        Target = OrderStatus.Assigned,
                        Reason = InternalReason,
                        OccurredAt = occurredAt,
                        ClaimWindowEndsAt = null,
                        FinalizedAt = null,
                        CodExpectedCents = 0,
                        MonetaryIntegrityValid = true,
                        Metadata = NormalizedTransitionMetadata.Empty,
                        Assignment = new AssignmentGuardSnapshot(true, true, true, true),
                    });
                    if (!matrix.Allowed || !guards.Satisfied || order.Version == int.MaxValue)
                    {
                        throw Conflict(AssignmentConflictCode.InvalidOrderState);
                    }

                    var newVersion = checked(order.Version + 1);
                    if (await UpdateOrderAsync(
                            connection,
                            transaction,
                            order,
                            source,
                            newVersion,
                            occurredAt,
                            token) != 1)
                    {
                        throw Conflict(AssignmentConflictCode.ConcurrencyConflict);
                    }

                    await failureInjector.OnStageAsync(AssignmentTransactionStage.OrderUpdated, token);
                    await InsertOrderEventAsync(
                        connection,
                        transaction,
                        eventId,
                        order,
                        assignmentId,
                        source,
                        newVersion,
                        command.ActorId,
                        occurredAt,
                        token);
                    await failureInjector.OnStageAsync(AssignmentTransactionStage.EventInserted, token);
                    await InsertOutboxAsync(
                        connection,
                        transaction,
                        outboxId,
                        order,
                        source,
                        newVersion,
                        occurredAt,
                        token);
                    await failureInjector.OnStageAsync(AssignmentTransactionStage.OutboxInserted, token);
                    await WriteAssignmentAuditAsync(
                        connection,
                        transaction,
                        command,
                        assignment,
                        occurredAt,
                        token);
                    await failureInjector.OnStageAsync(
                        AssignmentTransactionStage.AssignmentAuditInserted,
                        token);
                    await WriteTransitionAuditAsync(
                        connection,
                        transaction,
                        command,
                        order,
                        assignmentId,
                        source,
                        newVersion,
                        occurredAt,
                        token);
                    await failureInjector.OnStageAsync(
                        AssignmentTransactionStage.TransitionAuditInserted,
                        token);

                    var created = new AssignmentResult(
                        assignment.Id,
                        assignment.OrderId,
                        assignment.DriverId,
                        assignment.Status.ToContractValue(),
                        new Dispatch.Application.Assignments.MoneyResult("MXN", assignment.CostCents));
                    await failureInjector.OnStageAsync(
                        AssignmentTransactionStage.BeforeIdempotencyCompletion,
                        token);
                    await CompleteIdempotencyAsync(
                        connection,
                        transaction,
                        command,
                        requestHash,
                        created,
                        occurredAt,
                        token);
                    await failureInjector.OnStageAsync(
                        AssignmentTransactionStage.IdempotencyCompleted,
                        token);
                    await failureInjector.OnStageAsync(
                        AssignmentTransactionStage.BeforeCommit,
                        token);
                    return created;
                },
                cancellationToken);

            CreatedCounter.Add(1);
            LogResult(command, result.Id, "created", stopwatch.Elapsed);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.UniqueViolation &&
                  exception.ConstraintName is "one_active_assignment_per_order" or "order_events_order_id_aggregate_version_key")
        {
            throw Conflict(AssignmentConflictCode.ConcurrencyConflict, exception);
        }
    }

    private async Task EnsureAuthorizedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateOwnDriverAssignmentCommand command,
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
            throw new AssignmentForbiddenException();
        }
    }

    private static AssignmentConflictException EligibilityConflict(DriverEligibilityResult eligibility)
    {
        var codes = eligibility.Rejections.Select(value => value.Code).ToHashSet(StringComparer.Ordinal);
        if (codes.Contains(DriverEligibilityRejectionCodes.DocumentExpired))
        {
            return Conflict(AssignmentConflictCode.DriverDocumentExpired);
        }

        if (codes.Overlaps(
            [
                DriverEligibilityRejectionCodes.PackageRequirementInvalid,
                DriverEligibilityRejectionCodes.PackageCountExceeded,
                DriverEligibilityRejectionCodes.TotalWeightExceeded,
                DriverEligibilityRejectionCodes.SinglePackageWeightExceeded,
                DriverEligibilityRejectionCodes.PackageLengthExceeded,
                DriverEligibilityRejectionCodes.PackageWidthExceeded,
                DriverEligibilityRejectionCodes.PackageHeightExceeded,
            ]))
        {
            return Conflict(AssignmentConflictCode.CapacityInsufficient);
        }

        return Conflict(AssignmentConflictCode.DriverIneligible);
    }

    private async Task AcquireIdempotencyLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateOwnDriverAssignmentCommand assignment,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key,0));");
        command.Parameters.Add(P(
            "lock_key",
            NpgsqlDbType.Text,
            $"{assignment.OrganizationId:D}:{IdempotencyScope}:{assignment.IdempotencyKey}"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IdempotencyRow?> FindIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateOwnDriverAssignmentCommand assignment,
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
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, assignment.OrganizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, assignment.IdempotencyKey));
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

    private static AssignmentResult? ReadCompletedResponse(IdempotencyRow row)
    {
        if (row.ResponseStatus is null && row.ResponseBody is null && row.ResourceId is null)
        {
            return null;
        }

        if (row.ResponseStatus != 201 || string.IsNullOrWhiteSpace(row.ResponseBody) || row.ResourceId is null)
        {
            throw Conflict(AssignmentConflictCode.InconsistentReplayEvidence);
        }

        try
        {
            return JsonSerializer.Deserialize<AssignmentResult>(row.ResponseBody, JsonOptions)
                ?? throw Conflict(AssignmentConflictCode.InconsistentReplayEvidence);
        }
        catch (JsonException exception)
        {
            throw Conflict(AssignmentConflictCode.InconsistentReplayEvidence, exception);
        }
    }

    private async Task InsertIdempotencyReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateOwnDriverAssignmentCommand assignment,
        byte[] requestHash,
        DateTimeOffset occurredAt,
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
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, assignment.OrganizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, assignment.IdempotencyKey));
        command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
        command.Parameters.Add(P("created", NpgsqlDbType.TimestampTz, occurredAt));
        command.Parameters.Add(P(
            "expires",
            NpgsqlDbType.TimestampTz,
            occurredAt.AddMinutes(options.Value.IdempotencyLifetimeMinutes)));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "Idempotency reservation failed.");
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
            SELECT id,owner_org_id,operator_org_id,city_id,service_area_id,status,version
            FROM orders.orders
            WHERE id=@id AND (owner_org_id=@organization_id OR operator_org_id=@organization_id)
            FOR UPDATE
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("organization_id", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetString(5),
                reader.GetInt32(6))
            : null;
    }

    private async Task<bool> HasActiveAssignmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT EXISTS (
              SELECT 1 FROM dispatch.assignments
              WHERE order_id=@order_id AND status IN ('ACCEPTED','ACTIVE')
            )
            """);
        command.Parameters.Add(P("order_id", NpgsqlDbType.Uuid, orderId));
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<IReadOnlyList<PackageCapacityItem>> ReadPackagesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT weight_grams,dimensions_mm::text
            FROM orders.package_items
            WHERE order_id=@order_id
            ORDER BY id
            """);
        command.Parameters.Add(P("order_id", NpgsqlDbType.Uuid, orderId));
        var packages = new List<PackageCapacityItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!TryReadDimensions(
                    reader.GetString(1),
                    out var length,
                    out var width,
                    out var height))
            {
                throw Conflict(AssignmentConflictCode.CapacityInsufficient);
            }

            packages.Add(new PackageCapacityItem(reader.GetInt32(0), length, width, height));
        }

        return packages;
    }

    internal static bool TryReadDimensions(
        string json,
        out int? length,
        out int? width,
        out int? height)
    {
        length = null;
        width = null;
        height = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryReadDimension(document.RootElement, "length_mm", out length) ||
                !TryReadDimension(document.RootElement, "width_mm", out width) ||
                !TryReadDimension(document.RootElement, "height_mm", out height))
            {
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadDimension(JsonElement element, string name, out int? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var parsed) ||
            parsed <= 0)
        {
            return false;
        }

        value = parsed;
        return true;
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
            VALUES (@id,@order,@owner,@operator,@driver,NULL,'OWN','ACCEPTED',@cost,@accepted,@created)
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, assignment.Id));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, assignment.OrderId));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, assignment.OwnerOrganizationId));
        command.Parameters.Add(P("operator", NpgsqlDbType.Uuid, assignment.OperatorOrganizationId));
        command.Parameters.Add(P("driver", NpgsqlDbType.Uuid, assignment.DriverId));
        command.Parameters.Add(P("cost", NpgsqlDbType.Bigint, assignment.CostCents));
        command.Parameters.Add(P("accepted", NpgsqlDbType.TimestampTz, assignment.AcceptedAt));
        command.Parameters.Add(P("created", NpgsqlDbType.TimestampTz, assignment.CreatedAt));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "Assignment insert failed.");
    }

    private async Task<int> UpdateOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrderRow order,
        OrderStatus source,
        int newVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE orders.orders
            SET status='ASSIGNED',version=@new_version,updated_at=@occurred
            WHERE id=@order_id AND status=@source_status AND version=@current_version
            """);
        command.Parameters.Add(P("new_version", NpgsqlDbType.Integer, newVersion));
        command.Parameters.Add(P("occurred", NpgsqlDbType.TimestampTz, occurredAt));
        command.Parameters.Add(P("order_id", NpgsqlDbType.Uuid, order.Id));
        command.Parameters.Add(P("source_status", NpgsqlDbType.Text, source.ToContractValue()));
        command.Parameters.Add(P("current_version", NpgsqlDbType.Integer, order.Version));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertOrderEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        OrderRow order,
        Guid assignmentId,
        OrderStatus source,
        int newVersion,
        Guid actorId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            previous_status = source.ToContractValue(),
            new_status = "ASSIGNED",
            assignment_id = assignmentId,
            reason_redacted = InternalReason,
        }, JsonOptions);
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,operator_org_id,aggregate_version,event_type,
              public_event_code,payload,actor_id,occurred_at)
            VALUES (@id,@order,@owner,@operator,@version,'ORDER_STATUS_CHANGED',
                    NULL,@payload,@actor,@occurred)
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, eventId));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, order.Id));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        command.Parameters.Add(P("operator", NpgsqlDbType.Uuid, order.OperatorOrganizationId));
        command.Parameters.Add(P("version", NpgsqlDbType.Integer, newVersion));
        command.Parameters.Add(P("payload", NpgsqlDbType.Jsonb, payload));
        command.Parameters.Add(P("actor", NpgsqlDbType.Uuid, actorId));
        command.Parameters.Add(P("occurred", NpgsqlDbType.TimestampTz, occurredAt));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "Order event insert failed.");
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid outboxId,
        OrderRow order,
        OrderStatus source,
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
            order_id = order.Id,
            previous_status = source.ToContractValue(),
            new_status = "ASSIGNED",
            occurred_at = occurredAt,
            public_event_code = (string?)null,
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
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "Outbox insert failed.");
    }

    private async Task WriteAssignmentAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateOwnDriverAssignmentCommand command,
        Assignment assignment,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            assignment_id = assignment.Id,
            order_id = assignment.OrderId,
            driver_id = assignment.DriverId,
            owner_organization_id = assignment.OwnerOrganizationId,
            operator_organization_id = assignment.OperatorOrganizationId,
            assignment_type = assignment.AssignmentType.ToContractValue(),
            status = assignment.Status.ToContractValue(),
            cost_cents = assignment.CostCents,
            policy_version = options.Value.AssignmentPolicyVersion,
            request_id = command.RequestId,
        }, JsonOptions);
        await auditWriter.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                Guid.NewGuid(),
                assignment.OwnerOrganizationId,
                command.ActorId,
                "ASSIGNMENT_CREATED",
                "Assignment",
                assignment.Id,
                command.RequestId,
                auditRedactor.Redact(payload),
                occurredAt),
            cancellationToken);
    }

    private async Task WriteTransitionAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateOwnDriverAssignmentCommand command,
        OrderRow order,
        Guid assignmentId,
        OrderStatus source,
        int newVersion,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            order_id = order.Id,
            previous_status = source.ToContractValue(),
            new_status = "ASSIGNED",
            previous_version = order.Version,
            new_version = newVersion,
            assignment_id = assignmentId,
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
                auditRedactor.Redact(payload),
                occurredAt),
            cancellationToken);
    }

    private async Task CompleteIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateOwnDriverAssignmentCommand assignment,
        byte[] requestHash,
        AssignmentResult result,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var response = JsonSerializer.Serialize(result, JsonOptions);
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE platform.idempotency_keys
            SET response_status=201,response_body=@response,resource_id=@resource,expires_at=@expires
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key AND request_hash=@hash
              AND response_status IS NULL AND response_body IS NULL AND resource_id IS NULL
            """);
        command.Parameters.Add(P("response", NpgsqlDbType.Jsonb, response));
        command.Parameters.Add(P("resource", NpgsqlDbType.Uuid, result.Id));
        command.Parameters.Add(P(
            "expires",
            NpgsqlDbType.TimestampTz,
            occurredAt.AddMinutes(options.Value.IdempotencyLifetimeMinutes)));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, assignment.OrganizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, assignment.IdempotencyKey));
        command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "Idempotency completion failed.");
    }

    private void LogResult(
        CreateOwnDriverAssignmentCommand command,
        Guid assignmentId,
        string result,
        TimeSpan duration) =>
        logger.LogInformation(
            "Dispatch assignment result {Result}; tenant {TenantId}; actor {ActorId}; order {OrderId}; assignment {AssignmentId}; assignment policy {AssignmentPolicyVersion}; eligibility policy {EligibilityPolicyVersion}; duration_ms {DurationMs}",
            result,
            command.OrganizationId,
            command.ActorId,
            command.OrderId,
            assignmentId,
            options.Value.AssignmentPolicyVersion,
            driverEligibilityOptions.Value.PolicyVersion,
            duration.TotalMilliseconds);

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

    private static AssignmentConflictException Conflict(
        AssignmentConflictCode code,
        Exception? exception = null)
    {
        ConflictCounter.Add(1);
        return new AssignmentConflictException(code, exception);
    }

    private static void RequireOne(int affected, string message)
    {
        if (affected != 1)
        {
            throw new AssignmentInfrastructureException(message);
        }
    }

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
        int Version);
}
