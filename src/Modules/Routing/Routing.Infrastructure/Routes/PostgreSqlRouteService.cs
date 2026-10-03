using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Drivers.Application.Eligibility;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;
using Routing.Application.Routes;
using Routing.Domain;
using Routing.Infrastructure.Persistence;

namespace Routing.Infrastructure.Routes;

public sealed partial class PostgreSqlRouteService(
    TenantTransactionContext<RoutingDbContext> transactionContext,
    IOptions<RoutingOptions> options,
    IOptions<RoutingDriverEligibilityOptions> driverEligibilityOptions,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor,
    IRoutingFailureInjector failureInjector,
    IClock clock) : IRouteService
{
    public const string CreateIdempotencyScope = "RTE-001:CREATE_ROUTE";
    public const string AddStopIdempotencyScope = "RTE-001:ADD_ROUTE_STOP";
    public const string RemoveStopIdempotencyScope = "RTE-001:REMOVE_ROUTE_STOP";
    public const string ReorderStopsIdempotencyScope = "RTE-001:REORDER_ROUTE_STOPS";
    public const string OutboxTopic = "routes.route-changed";
    public const int PageSize = 50;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<RouteResult> CreateAsync(
        CreateRouteCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!RoutingInputPolicy.IsValid(command) || !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey))
        {
            throw Conflict(RoutingConflictCode.InvalidRequest);
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        var requestHash = RoutingCanonicalizer.Create(command);
        return await ExecuteAsync(
            command.ActorId,
            command.OrganizationId,
            async (connection, transaction, token) =>
            {
                await EnsureAuthorizedAsync(
                    connection,
                    transaction,
                    command.ActorId,
                    command.OrganizationId,
                    command.MfaSatisfied,
                    true,
                    token);
                var replay = await BeginIdempotencyAsync<RouteResult>(
                    connection,
                    transaction,
                    command.OrganizationId,
                    CreateIdempotencyScope,
                    command.IdempotencyKey,
                    requestHash,
                    StatusCodes.Status201Created,
                    now,
                    token);
                if (replay is not null) return replay;

                var eligible = await EvaluateDriverAsync(
                    connection,
                    transaction,
                    command.ActorId,
                    command.OrganizationId,
                    command.DriverId,
                    command.CityId,
                    command.ServiceAreaId,
                    new DriverCapacityRequirement(1, 1, 1, 1, 1, 1),
                    now,
                    token);
                if (!eligible.IsEligible)
                {
                    if (eligible.Rejections.Any(rejection =>
                            rejection.Code == DriverEligibilityRejectionCodes.DriverUnavailable))
                    {
                        throw new RoutingNotFoundException();
                    }
                    throw Conflict(RoutingConflictCode.DriverIneligible);
                }

                var routeId = Guid.NewGuid();
                await using (var insert = CreateCommand(
                    connection,
                    transaction,
                    """
                    INSERT INTO routes.routes(
                      id,operator_org_id,city_id,service_area_id,driver_id,status,
                      version,scheduled_for,created_at,updated_at)
                    VALUES (@id,@organization,@city,@service_area,@driver,'DRAFT',1,@scheduled,@now,@now)
                    """))
                {
                    insert.Parameters.Add(P("id", NpgsqlDbType.Uuid, routeId));
                    insert.Parameters.Add(P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
                    insert.Parameters.Add(P("city", NpgsqlDbType.Uuid, command.CityId));
                    insert.Parameters.Add(P("service_area", NpgsqlDbType.Uuid, command.ServiceAreaId));
                    insert.Parameters.Add(P("driver", NpgsqlDbType.Uuid, command.DriverId));
                    insert.Parameters.Add(P("scheduled", NpgsqlDbType.Date, command.ScheduledFor));
                    insert.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
                    RequireOne(await insert.ExecuteNonQueryAsync(token), "Route insert failed.");
                }
                await failureInjector.OnStageAsync(RoutingTransactionStage.RouteInserted, token);

                var result = new RouteResult(
                    routeId,
                    RouteStatus.Draft.ToContractValue(),
                    1,
                    command.DriverId,
                    command.CityId,
                    command.ServiceAreaId,
                    command.ScheduledFor,
                    0,
                    0);
                await WriteAuditAsync(
                    connection,
                    transaction,
                    command.ActorId,
                    command.OrganizationId,
                    "routing.route.created",
                    routeId,
                    command.RequestId,
                    new
                    {
                        route_id = routeId,
                        driver_id = command.DriverId,
                        city_id = command.CityId,
                        service_area_id = command.ServiceAreaId,
                        scheduled_for = command.ScheduledFor,
                        route_version = 1,
                    },
                    now,
                    token);
                await InsertOutboxAsync(
                    connection,
                    transaction,
                    command.OrganizationId,
                    routeId,
                    1,
                    [],
                    now,
                    token);
                await CompleteIdempotencyAsync(
                    connection,
                    transaction,
                    command.OrganizationId,
                    CreateIdempotencyScope,
                    command.IdempotencyKey,
                    requestHash,
                    StatusCodes.Status201Created,
                    routeId,
                    result,
                    now,
                    token);
                await failureInjector.OnStageAsync(RoutingTransactionStage.BeforeCommit, token);
                return result;
            },
            cancellationToken);
    }

    public async Task<RoutePageResult> ListAsync(
        ListRoutesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        RouteCursor? cursor = null;
        if (!RoutingInputPolicy.IsValid(query) ||
            (query.Cursor is not null && !RouteCursorCodec.TryDecode(query.Cursor, out cursor)))
        {
            throw new RoutingForbiddenException();
        }

        return await ExecuteAsync(
            query.ActorId,
            query.OrganizationId,
            async (connection, transaction, token) =>
            {
                await EnsureAuthorizedAsync(
                    connection,
                    transaction,
                    query.ActorId,
                    query.OrganizationId,
                    false,
                    false,
                    token);
                const string sql =
                    """
                    SELECT r.id,r.status,r.version,r.driver_id,r.city_id,r.service_area_id,
                           r.scheduled_for,
                           COALESCE((
                             SELECT sum(a.cost_cents)
                             FROM dispatch.assignments a
                             WHERE a.route_id=r.id AND a.status IN ('ACCEPTED','ACTIVE')
                           ),0)::bigint AS assignment_cost_cents_total,
                           (SELECT count(*)::integer FROM routes.route_stops s WHERE s.route_id=r.id),
                           r.created_at
                    FROM routes.routes r
                    WHERE r.operator_org_id=@organization
                      AND (@status IS NULL OR r.status=@status)
                      AND (@driver IS NULL OR r.driver_id=@driver)
                      AND (@scheduled IS NULL OR r.scheduled_for=@scheduled)
                      AND (@cursor_created IS NULL OR (r.created_at,r.id) < (@cursor_created,@cursor_id))
                    ORDER BY r.created_at DESC,r.id DESC
                    LIMIT @limit
                    """;
                var values = new List<(RouteResult Route, DateTimeOffset CreatedAt)>();
                await using var command = CreateCommand(connection, transaction, sql);
                command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, query.OrganizationId));
                command.Parameters.Add(P("status", NpgsqlDbType.Text, query.Status));
                command.Parameters.Add(P("driver", NpgsqlDbType.Uuid, query.DriverId));
                command.Parameters.Add(P("scheduled", NpgsqlDbType.Date, query.ScheduledFor));
                command.Parameters.Add(P("cursor_created", NpgsqlDbType.TimestampTz, cursor?.CreatedAt));
                command.Parameters.Add(P("cursor_id", NpgsqlDbType.Uuid, cursor?.Id));
                command.Parameters.Add(P("limit", NpgsqlDbType.Integer, PageSize + 1));
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    values.Add((ReadRoute(reader), reader.GetFieldValue<DateTimeOffset>(9)));
                }

                var hasMore = values.Count > PageSize;
                var page = values.Take(PageSize).ToArray();
                var nextCursor = hasMore && page.Length != 0
                    ? RouteCursorCodec.Encode(new(page[^1].CreatedAt, page[^1].Route.Id))
                    : null;
                return new RoutePageResult(page.Select(value => value.Route).ToArray(), nextCursor);
            },
            cancellationToken);
    }

    public async Task<RouteDetailResult> GetAsync(
        GetRouteQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!RoutingInputPolicy.IsValid(query)) throw new RoutingNotFoundException();
        return await ExecuteAsync(
            query.ActorId,
            query.OrganizationId,
            async (connection, transaction, token) =>
            {
                await EnsureAuthorizedAsync(
                    connection,
                    transaction,
                    query.ActorId,
                    query.OrganizationId,
                    false,
                    false,
                    token);
                return await ReadDetailRequiredAsync(
                    connection,
                    transaction,
                    query.OrganizationId,
                    query.RouteId,
                    token);
            },
            cancellationToken);
    }

    public async Task<RouteDetailResult> AddStopAsync(
        AddRouteStopCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!RoutingInputPolicy.IsValid(command) || !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey))
        {
            throw Conflict(RoutingConflictCode.InvalidRequest);
        }
        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        var requestHash = RoutingCanonicalizer.Add(command);
        return await ExecuteAsync(
            command.ActorId,
            command.OrganizationId,
            async (connection, transaction, token) =>
            {
                await EnsureAuthorizedAsync(connection, transaction, command.ActorId,
                    command.OrganizationId, command.MfaSatisfied, true, token);
                var replay = await BeginIdempotencyAsync<RouteDetailResult>(
                    connection, transaction, command.OrganizationId, AddStopIdempotencyScope,
                    command.IdempotencyKey, requestHash, StatusCodes.Status201Created, now, token);
                if (replay is not null) return replay;

                await AcquireOrderLockAsync(connection, transaction, command.OrderId, token);
                var route = await ReadRouteForUpdateAsync(
                    connection, transaction, command.OrganizationId, command.RouteId, token)
                    ?? throw new RoutingNotFoundException();
                EnsureMutable(route, command.ExpectedVersion);
                var order = await ReadOrderForUpdateAsync(
                    connection, transaction, command.OrganizationId, command.OrderId, token)
                    ?? throw new RoutingNotFoundException();
                var assignment = await ReadAssignmentForUpdateAsync(
                    connection, transaction, command.OrganizationId, command.OrderId, token)
                    ?? throw Conflict(RoutingConflictCode.AssignmentUnavailable);
                if (assignment.RouteId == route.Id ||
                    await RouteStopExistsAsync(connection, transaction, route.Id, order.Id, token))
                {
                    throw Conflict(RoutingConflictCode.DuplicateOrder);
                }
                if (assignment.RouteId is not null ||
                    await ActiveRouteStopExistsAsync(connection, transaction, route.Id, order.Id, token))
                {
                    throw Conflict(RoutingConflictCode.OrderAlreadyRouted);
                }
                if (!ManualRoutePolicy.CanLinkAssignment(
                        assignment.AssignmentType,
                        assignment.Status,
                        assignment.DriverId,
                        route.DriverId,
                        assignment.RouteId))
                {
                    throw Conflict(assignment.DriverId == route.DriverId
                        ? RoutingConflictCode.AssignmentUnavailable
                        : RoutingConflictCode.AssignmentDriverMismatch);
                }
                if (order.CityId != route.CityId ||
                    (route.ServiceAreaId is not null && order.ServiceAreaId != route.ServiceAreaId))
                {
                    throw Conflict(RoutingConflictCode.DriverIneligible);
                }
                var capacity = await ReadCapacityAsync(connection, transaction, order.Id, token)
                    ?? throw Conflict(RoutingConflictCode.DriverIneligible);
                var eligible = await EvaluateDriverAsync(
                    connection, transaction, command.ActorId, command.OrganizationId,
                    route.DriverId, order.CityId, order.ServiceAreaId, capacity, now, token);
                if (!eligible.IsEligible) throw Conflict(RoutingConflictCode.DriverIneligible);

                var stopId = Guid.NewGuid();
                var sequence = await NextSequenceAsync(connection, transaction, route.Id, token);
                await InsertStopAsync(connection, transaction, command.OrganizationId,
                    route.Id, stopId, order.Id, sequence, token);
                await failureInjector.OnStageAsync(RoutingTransactionStage.StopInserted, token);
                await LinkAssignmentAsync(connection, transaction, assignment.Id, route.Id, token);
                await failureInjector.OnStageAsync(RoutingTransactionStage.AssignmentLinked, token);
                var newVersion = await IncrementVersionAsync(
                    connection, transaction, route.Id, command.ExpectedVersion, now, token);
                await WriteAuditAsync(
                    connection, transaction, command.ActorId, command.OrganizationId,
                    "routing.route-stop.added", route.Id, command.RequestId,
                    new
                    {
                        route_id = route.Id,
                        stop_id = stopId,
                        order_id = order.Id,
                        assignment_id = assignment.Id,
                        sequence,
                        route_version = newVersion,
                    },
                    now,
                    token);
                await InsertOutboxAsync(connection, transaction, command.OrganizationId,
                    route.Id, newVersion, [stopId], now, token);
                var result = await ReadDetailRequiredAsync(
                    connection, transaction, command.OrganizationId, route.Id, token);
                await CompleteIdempotencyAsync(
                    connection, transaction, command.OrganizationId, AddStopIdempotencyScope,
                    command.IdempotencyKey, requestHash, StatusCodes.Status201Created,
                    route.Id, result, now, token);
                await failureInjector.OnStageAsync(RoutingTransactionStage.BeforeCommit, token);
                return result;
            },
            cancellationToken);
    }

    public async Task<RouteDetailResult> RemoveStopAsync(
        RemoveRouteStopCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!RoutingInputPolicy.IsValid(command) || !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey))
        {
            throw Conflict(RoutingConflictCode.InvalidRequest);
        }
        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        var requestHash = RoutingCanonicalizer.Remove(command);
        return await ExecuteAsync(
            command.ActorId,
            command.OrganizationId,
            async (connection, transaction, token) =>
            {
                await EnsureAuthorizedAsync(connection, transaction, command.ActorId,
                    command.OrganizationId, command.MfaSatisfied, true, token);
                var replay = await BeginIdempotencyAsync<RouteDetailResult>(
                    connection, transaction, command.OrganizationId, RemoveStopIdempotencyScope,
                    command.IdempotencyKey, requestHash, StatusCodes.Status200OK, now, token);
                if (replay is not null) return replay;

                var initialStop = await ReadStopAsync(
                    connection, transaction, command.OrganizationId, command.RouteId, command.StopId,
                    false, token) ?? throw new RoutingNotFoundException();
                await AcquireOrderLockAsync(connection, transaction, initialStop.OrderId, token);
                var route = await ReadRouteForUpdateAsync(
                    connection, transaction, command.OrganizationId, command.RouteId, token)
                    ?? throw new RoutingNotFoundException();
                EnsureMutable(route, command.ExpectedVersion);
                var stop = await ReadStopAsync(
                    connection, transaction, command.OrganizationId, command.RouteId, command.StopId,
                    true, token) ?? throw Conflict(RoutingConflictCode.ConcurrencyConflict);
                var shiftedIds = await ReadShiftedStopIdsAsync(
                    connection, transaction, route.Id, stop.Sequence, token);
                await DeleteStopAsync(connection, transaction, stop.Id, token);
                await failureInjector.OnStageAsync(RoutingTransactionStage.StopRemoved, token);
                await UnlinkAssignmentAsync(connection, transaction, route.Id, stop.OrderId, token);
                await failureInjector.OnStageAsync(RoutingTransactionStage.AssignmentUnlinked, token);
                await CompactSequencesAsync(connection, transaction, route.Id, token);
                var newVersion = await IncrementVersionAsync(
                    connection, transaction, route.Id, command.ExpectedVersion, now, token);
                var changed = new[] { stop.Id }.Concat(shiftedIds).Distinct().ToArray();
                await WriteAuditAsync(
                    connection, transaction, command.ActorId, command.OrganizationId,
                    "routing.route-stop.removed", route.Id, command.RequestId,
                    new
                    {
                        route_id = route.Id,
                        stop_id = stop.Id,
                        order_id = stop.OrderId,
                        route_version = newVersion,
                    },
                    now,
                    token);
                await InsertOutboxAsync(connection, transaction, command.OrganizationId,
                    route.Id, newVersion, changed, now, token);
                var result = await ReadDetailRequiredAsync(
                    connection, transaction, command.OrganizationId, route.Id, token);
                await CompleteIdempotencyAsync(
                    connection, transaction, command.OrganizationId, RemoveStopIdempotencyScope,
                    command.IdempotencyKey, requestHash, StatusCodes.Status200OK,
                    route.Id, result, now, token);
                await failureInjector.OnStageAsync(RoutingTransactionStage.BeforeCommit, token);
                return result;
            },
            cancellationToken);
    }

    public async Task<RouteDetailResult> ReorderStopsAsync(
        ReorderRouteStopsCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!RoutingInputPolicy.IsValid(command) || !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey))
        {
            throw Conflict(RoutingConflictCode.InvalidRequest);
        }
        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        var requestHash = RoutingCanonicalizer.Reorder(command);
        return await ExecuteAsync(
            command.ActorId,
            command.OrganizationId,
            async (connection, transaction, token) =>
            {
                await EnsureAuthorizedAsync(connection, transaction, command.ActorId,
                    command.OrganizationId, command.MfaSatisfied, true, token);
                var replay = await BeginIdempotencyAsync<RouteDetailResult>(
                    connection, transaction, command.OrganizationId, ReorderStopsIdempotencyScope,
                    command.IdempotencyKey, requestHash, StatusCodes.Status200OK, now, token);
                if (replay is not null) return replay;

                var route = await ReadRouteForUpdateAsync(
                    connection, transaction, command.OrganizationId, command.RouteId, token)
                    ?? throw new RoutingNotFoundException();
                EnsureMutable(route, command.ExpectedVersion);
                var currentIds = await ReadAllStopIdsAsync(connection, transaction, route.Id, true, token);
                if (currentIds.Count != command.StopIds.Count ||
                    !currentIds.Order().SequenceEqual(command.StopIds.Order()))
                {
                    throw Conflict(RoutingConflictCode.InvalidStopSet);
                }
                await ReorderSequencesAsync(connection, transaction, route.Id, command.StopIds, token);
                await failureInjector.OnStageAsync(RoutingTransactionStage.StopsReordered, token);
                var newVersion = await IncrementVersionAsync(
                    connection, transaction, route.Id, command.ExpectedVersion, now, token);
                await WriteAuditAsync(
                    connection, transaction, command.ActorId, command.OrganizationId,
                    "routing.route-stops.reordered", route.Id, command.RequestId,
                    new
                    {
                        route_id = route.Id,
                        stop_ids = command.StopIds,
                        route_version = newVersion,
                    },
                    now,
                    token);
                await InsertOutboxAsync(connection, transaction, command.OrganizationId,
                    route.Id, newVersion, command.StopIds, now, token);
                var result = await ReadDetailRequiredAsync(
                    connection, transaction, command.OrganizationId, route.Id, token);
                await CompleteIdempotencyAsync(
                    connection, transaction, command.OrganizationId, ReorderStopsIdempotencyScope,
                    command.IdempotencyKey, requestHash, StatusCodes.Status200OK,
                    route.Id, result, now, token);
                await failureInjector.OnStageAsync(RoutingTransactionStage.BeforeCommit, token);
                return result;
            },
            cancellationToken);
    }

    private async Task<T> ExecuteAsync<T>(
        Guid actorId,
        Guid organizationId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(actorId, [organizationId]),
                async (dbContext, token) =>
                {
                    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                    var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!
                        .GetDbTransaction();
                    return await operation(connection, transaction, token);
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is RoutingConflictException or RoutingForbiddenException or RoutingNotFoundException)
        {
            throw;
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UniqueViolation or
                PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
        {
            throw Conflict(RoutingConflictCode.ConcurrencyConflict, exception);
        }
        catch (NpgsqlException exception)
        {
            throw new RoutingUnavailableException("The routing data store is unavailable.", exception);
        }
    }

    private async Task EnsureAuthorizedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        bool mfaSatisfied,
        bool mutation,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT u.status,m.role,m.status
            FROM identity.users u
            LEFT JOIN organizations.organization_memberships m
              ON m.user_id=u.id AND m.organization_id=@organization
            WHERE u.id=@actor
            ORDER BY CASE m.role WHEN 'PLATFORM_ADMIN' THEN 0 WHEN 'DISPATCHER' THEN 1 ELSE 2 END
            LIMIT 1
            """;
        RoutingAuthorizationContext context;
        await using (var command = CreateCommand(connection, transaction, sql))
        {
            command.Parameters.Add(P("actor", NpgsqlDbType.Uuid, actorId));
            command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            context = await reader.ReadAsync(cancellationToken)
                ? new(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(0) == "ACTIVE",
                    !reader.IsDBNull(2) && reader.GetString(2) == "ACTIVE",
                    mfaSatisfied)
                : new(null, false, false, mfaSatisfied);
        }
        if (mutation ? !RoutingAuthorizationPolicy.CanMutate(context) : !RoutingAuthorizationPolicy.CanRead(context))
        {
            throw new RoutingForbiddenException();
        }
    }

    private async Task<DriverEligibilityResult> EvaluateDriverAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        Guid driverId,
        Guid cityId,
        Guid? serviceAreaId,
        DriverCapacityRequirement capacity,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        var snapshot = await ReadDriverSnapshotAsync(
            connection,
            transaction,
            organizationId,
            driverId,
            cityId,
            serviceAreaId,
            cancellationToken);
        return DriverEligibilityPolicy.Evaluate(
            new EvaluateOwnDriverEligibilityCommand(
                actorId,
                organizationId,
                driverId,
                cityId,
                serviceAreaId,
                capacity,
                evaluatedAt),
            snapshot,
            driverEligibilityOptions.Value.ToPolicy());
    }

    private async Task<DriverEligibilitySnapshot?> ReadDriverSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid driverId,
        Guid cityId,
        Guid? serviceAreaId,
        CancellationToken cancellationToken)
    {
        const string profileSql =
            """
            SELECT p.id,p.org_id,p.user_id,p.home_city_id,p.driver_type,p.vehicle_type,p.status,
                   u.status,
                   EXISTS (
                     SELECT 1 FROM organizations.organization_memberships m
                     WHERE m.user_id=p.user_id AND m.organization_id=p.org_id
                       AND m.role='DRIVER' AND m.status='ACTIVE'),
                   CASE WHEN @service_area IS NULL THEN NULL ELSE EXISTS (
                     SELECT 1
                     FROM drivers.driver_service_areas dsa
                     JOIN locations.service_areas sa ON sa.id=dsa.service_area_id
                     WHERE dsa.driver_id=p.id AND dsa.service_area_id=@service_area
                       AND dsa.org_id=p.org_id AND dsa.status='ACTIVE'
                       AND sa.owner_org_id=p.org_id AND sa.city_id=@city AND sa.status='ACTIVE') END,
                   o.driver_eligibility_policy_version
            FROM drivers.driver_profiles p
            JOIN organizations.organizations o ON o.id=p.org_id
            LEFT JOIN identity.users u ON u.id=p.user_id
            WHERE p.id=@driver AND p.org_id=@organization
            """;
        DriverProfileRow? profile = null;
        await using (var command = CreateCommand(connection, transaction, profileSql))
        {
            command.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
            command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
            command.Parameters.Add(P("service_area", NpgsqlDbType.Uuid, serviceAreaId));
            command.Parameters.Add(P("city", NpgsqlDbType.Uuid, cityId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                profile = new(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetBoolean(8),
                    reader.IsDBNull(9) ? null : reader.GetBoolean(9), reader.GetString(10));
            }
        }
        if (profile is null) return null;

        const string documentSql =
            """
            SELECT DISTINCT ON (document_type)
                   document_type,status,object_key,sha256,expires_at
            FROM drivers.driver_documents
            WHERE driver_id=@driver AND org_id=@organization
            ORDER BY document_type,created_at DESC,id DESC
            """;
        var documents = new Dictionary<string, DriverDocumentSnapshot>(StringComparer.Ordinal);
        await using (var command = CreateCommand(connection, transaction, documentSql))
        {
            command.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
            command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var type = reader.GetString(0);
                documents[type] = new(
                    type,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetFieldValue<byte[]>(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
            }
        }
        return new(
            profile.DriverId,
            profile.OrganizationId,
            profile.UserId,
            profile.HomeCityId,
            profile.DriverType,
            profile.VehicleType,
            profile.ProfileStatus,
            profile.UserStatus,
            profile.MembershipActive,
            profile.ServiceAreaEligible,
            documents,
            profile.PolicyVersion);
    }

    private static void EnsureMutable(RouteRow route, int expectedVersion)
    {
        if (route.Status != "DRAFT") throw Conflict(RoutingConflictCode.RouteStateConflict);
        if (route.Version != expectedVersion) throw Conflict(RoutingConflictCode.VersionConflict);
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

    private static RoutingConflictException Conflict(
        RoutingConflictCode code,
        Exception? exception = null) => new(code, exception);

    private static void RequireOne(int affected, string message)
    {
        if (affected != 1) throw new RoutingUnavailableException(message);
    }

    private sealed record DriverProfileRow(
        Guid DriverId,
        Guid OrganizationId,
        Guid UserId,
        Guid HomeCityId,
        string DriverType,
        string VehicleType,
        string ProfileStatus,
        string? UserStatus,
        bool MembershipActive,
        bool? ServiceAreaEligible,
        string PolicyVersion);

    private sealed record RouteRow(
        Guid Id,
        Guid OrganizationId,
        Guid DriverId,
        Guid CityId,
        Guid? ServiceAreaId,
        string Status,
        int Version,
        DateOnly? ScheduledFor);

    private sealed record OrderRow(Guid Id, Guid CityId, Guid? ServiceAreaId);
    private sealed record AssignmentRow(
        Guid Id,
        Guid DriverId,
        Guid? RouteId,
        string AssignmentType,
        string Status);
    private sealed record StopRow(Guid Id, Guid OrderId, int Sequence);
    private sealed record IdempotencyRow(byte[] Hash, int? Status, string? Body, Guid? ResourceId);
}
