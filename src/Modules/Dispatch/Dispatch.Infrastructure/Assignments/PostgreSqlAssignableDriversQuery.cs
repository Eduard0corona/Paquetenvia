using System.Diagnostics.Metrics;
using Dispatch.Application.Assignments;
using Dispatch.Infrastructure.Persistence;
using Drivers.Application.Eligibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Dispatch.Infrastructure.Assignments;

/// <summary>
/// UI-PHASE2-DRIVER-PICKER-2026-10-05 (listAssignableDrivers). A read only: one explicit tenant transaction
/// (set_config after BEGIN, SET LOCAL ROLE paqueteria_app, FORCE RLS), the assignDriver capability read first,
/// then the order visible to the active organization as owner or operator (no row lock), then one page of that
/// organization's OWN drivers evaluated with the same DriverEligibilityPolicy, order city, service area and package
/// requirement assignDriver applies. Nothing is written and no cross-module flow is added.
/// </summary>
public sealed class PostgreSqlAssignableDriversQuery(
    TenantTransactionContext<DispatchDbContext> transactionContext,
    IOptions<DispatchDriverEligibilityOptions> driverEligibilityOptions,
    IDispatchAssignmentAuthorizer authorizer,
    IDispatchAuthorizationReader authorizationReader,
    IClock clock,
    ILogger<PostgreSqlAssignableDriversQuery> logger) : IAssignableDriversQuery
{
    private static readonly Meter Meter = new("Paqueteria.Dispatch");
    private static readonly Histogram<long> ListedCount =
        Meter.CreateHistogram<long>("dispatch.assignable_drivers.count");

    /// <summary>The requirement that makes the policy report PACKAGE_REQUIREMENT_INVALID, as assignDriver refuses.</summary>
    private static readonly DriverCapacityRequirement InvalidRequirement = new(0, 0, 0, null, null, null);

    public async Task<AssignableDriverPage> ListAsync(
        ListAssignableDriversQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.ActorId == Guid.Empty || query.OrganizationId == Guid.Empty)
        {
            throw new AssignmentForbiddenException();
        }

        if (query.OrderId == Guid.Empty)
        {
            throw new AssignmentNotFoundException();
        }

        var evaluatedAt = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        var policy = driverEligibilityOptions.Value.ToPolicy();
        var page = await transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(query.ActorId, [query.OrganizationId]),
            async (dbContext, token) =>
            {
                var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();

                var authorization = await authorizationReader.ReadAsync(
                    connection, transaction, query.ActorId, query.OrganizationId, token);
                if (!authorizer.IsAuthorized(new DispatchAssignmentAuthorizationContext(
                        authorization.ActiveRole,
                        authorization.UserActive,
                        authorization.MembershipActive,
                        query.MfaSatisfied)))
                {
                    throw new AssignmentForbiddenException();
                }

                var order = await ReadOrderAsync(connection, transaction, query, token)
                    ?? throw new AssignmentNotFoundException();
                if (!AssignableDriverPolicy.AssignableOrderStatuses.Contains(order.Status))
                {
                    throw new AssignmentConflictException(AssignmentConflictCode.InvalidOrderState);
                }

                if (order.HasActiveAssignment)
                {
                    throw new AssignmentConflictException(AssignmentConflictCode.ActiveAssignmentExists);
                }

                var capacity = ReadCapacity(order.Packages);
                var candidates = await ReadCandidatesAsync(connection, transaction, query, order, token);
                var hasMore = candidates.Count > AssignableDriverPolicy.PageSize;
                var listed = candidates.Take(AssignableDriverPolicy.PageSize)
                    .Select(candidate => AssignableDriverPolicy.ToResult(
                        candidate.Snapshot,
                        DriverEligibilityPolicy.Evaluate(
                            new EvaluateOwnDriverEligibilityCommand(
                                query.ActorId,
                                query.OrganizationId,
                                candidate.Snapshot.DriverId,
                                order.CityId,
                                order.ServiceAreaId,
                                capacity,
                                evaluatedAt),
                            candidate.Snapshot,
                            policy),
                        candidate.ActiveAssignmentCount))
                    .ToArray();
                return new AssignableDriverPage(
                    listed,
                    hasMore
                        ? AssignableDriverCursorCodec.Encode(new AssignableDriverCursor(listed[^1].DriverId))
                        : null);
            },
            cancellationToken);

        ListedCount.Record(page.Items.Count);
        logger.LogInformation(
            "Dispatch assignable drivers listed; tenant {TenantId}; actor {ActorId}; count {Count}; eligible {Eligible}",
            query.OrganizationId,
            query.ActorId,
            page.Items.Count,
            page.Items.Count(item => item.Eligible));
        return page;
    }

    private static DriverCapacityRequirement ReadCapacity(IReadOnlyList<AssignmentVisibilityPackage> rows)
    {
        var packages = new List<PackageCapacityItem>(rows.Count);
        foreach (var row in rows)
        {
            if (!PostgreSqlAssignmentToOrderCoordinator.TryReadDimensions(
                    row.DimensionsJson, out var length, out var width, out var height))
            {
                return InvalidRequirement;
            }

            packages.Add(new PackageCapacityItem(row.WeightGrams, length, width, height));
        }

        return PackageCapacityAggregator.TryAggregate(packages, out var requirement) && requirement is not null
            ? requirement
            : InvalidRequirement;
    }

    private static async Task<OrderRow?> ReadOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ListAssignableDriversQuery query,
        CancellationToken cancellationToken)
    {
        // The same visibility as assignDriver (owner or operator of the order), without FOR UPDATE: a read never
        // blocks an assignment in flight.
        const string sql =
            """
            SELECT o.city_id,o.service_area_id,o.status,
                   EXISTS (
                     SELECT 1 FROM dispatch.assignments a
                     WHERE a.order_id=o.id AND a.status IN ('ACCEPTED','ACTIVE'))
            FROM orders.orders o
            WHERE o.id=@order_id
              AND (o.owner_org_id=@organization_id OR o.operator_org_id=@organization_id);

            SELECT package.weight_grams,package.dimensions_mm::text
            FROM orders.package_items package
            JOIN orders.orders visible_order ON visible_order.id=package.order_id
            WHERE package.order_id=@order_id
              AND (visible_order.owner_org_id=@organization_id
                   OR visible_order.operator_org_id=@organization_id)
            ORDER BY package.id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(P("order_id", NpgsqlDbType.Uuid, query.OrderId));
        command.Parameters.Add(P("organization_id", NpgsqlDbType.Uuid, query.OrganizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var cityId = reader.GetGuid(0);
        Guid? serviceAreaId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        var status = reader.GetString(2);
        var hasActiveAssignment = reader.GetBoolean(3);
        if (!await reader.NextResultAsync(cancellationToken))
        {
            throw new AssignmentInfrastructureException("Assignable drivers package result is missing.");
        }

        var packages = new List<AssignmentVisibilityPackage>();
        while (await reader.ReadAsync(cancellationToken))
        {
            packages.Add(new(reader.GetInt32(0), reader.GetString(1)));
        }

        return new OrderRow(cityId, serviceAreaId, status, hasActiveAssignment, packages);
    }

    private static async Task<IReadOnlyList<Candidate>> ReadCandidatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ListAssignableDriversQuery query,
        OrderRow order,
        CancellationToken cancellationToken)
    {
        // Every OWN profile of the active organization that is not retired (INACTIVE), with the same profile,
        // user, DRIVER membership, service-area and latest-document projection the per-driver eligibility read
        // performs, plus its ACCEPTED or ACTIVE assignments (assignments_driver_idx). Only the status is read from
        // identity.users (no email ciphertext); document keys and hashes feed the policy and are never returned.
        const string sql =
            """
            SELECT p.id,p.org_id,p.user_id,p.home_city_id,p.driver_type,p.vehicle_type,p.status,
                   u.status,
                   EXISTS (
                     SELECT 1
                     FROM organizations.organization_memberships m
                     WHERE m.user_id=p.user_id AND m.organization_id=p.org_id
                       AND m.role='DRIVER' AND m.status='ACTIVE'
                   ),
                   CASE WHEN @service_area_id IS NULL THEN NULL ELSE EXISTS (
                     SELECT 1
                     FROM drivers.driver_service_areas dsa
                     JOIN locations.service_areas sa ON sa.id=dsa.service_area_id
                     WHERE dsa.driver_id=p.id AND dsa.service_area_id=@service_area_id
                       AND dsa.org_id=p.org_id AND dsa.status='ACTIVE'
                       AND sa.owner_org_id=p.org_id AND sa.city_id=@city_id AND sa.status='ACTIVE'
                   ) END,
                   COALESCE(docs.types,ARRAY[]::text[]),
                   COALESCE(docs.statuses,ARRAY[]::text[]),
                   COALESCE(docs.object_keys,ARRAY[]::text[]),
                   COALESCE(docs.hashes,ARRAY[]::bytea[]),
                   COALESCE(docs.expirations,ARRAY[]::timestamptz[]),
                   o.driver_eligibility_policy_version,
                   (SELECT count(*)::integer
                    FROM dispatch.assignments a
                    WHERE a.driver_id=p.id AND a.status IN ('ACCEPTED','ACTIVE'))
            FROM drivers.driver_profiles p
            JOIN organizations.organizations o ON o.id=p.org_id
            LEFT JOIN identity.users u ON u.id=p.user_id
            LEFT JOIN LATERAL (
              SELECT array_agg(d.document_type ORDER BY d.document_type) AS types,
                     array_agg(d.status ORDER BY d.document_type) AS statuses,
                     array_agg(d.object_key ORDER BY d.document_type) AS object_keys,
                     array_agg(d.sha256 ORDER BY d.document_type) AS hashes,
                     array_agg(d.expires_at ORDER BY d.document_type) AS expirations
              FROM (
                SELECT DISTINCT ON (dd.document_type)
                       dd.document_type,dd.status,dd.object_key,dd.sha256,dd.expires_at
                FROM drivers.driver_documents dd
                WHERE dd.driver_id=p.id AND dd.org_id=p.org_id
                ORDER BY dd.document_type,dd.created_at DESC,dd.id DESC
              ) d
            ) docs ON true
            WHERE p.org_id=@organization_id AND p.driver_type='OWN' AND p.status<>'INACTIVE'
              AND (@after IS NULL OR p.id>@after)
            ORDER BY p.id
            LIMIT @limit
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(P("organization_id", NpgsqlDbType.Uuid, query.OrganizationId));
        command.Parameters.Add(P("service_area_id", NpgsqlDbType.Uuid, order.ServiceAreaId));
        command.Parameters.Add(P("city_id", NpgsqlDbType.Uuid, order.CityId));
        command.Parameters.Add(P("after", NpgsqlDbType.Uuid, query.Cursor?.AfterDriverId));
        command.Parameters.Add(P("limit", NpgsqlDbType.Integer, AssignableDriverPolicy.PageSize + 1));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var candidates = new List<Candidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var types = reader.GetFieldValue<string[]>(10);
            var statuses = reader.GetFieldValue<string[]>(11);
            var objectKeys = reader.GetFieldValue<string[]>(12);
            var hashes = reader.GetFieldValue<byte[][]>(13);
            var expirations = reader.GetFieldValue<DateTime?[]>(14);
            if (statuses.Length != types.Length || objectKeys.Length != types.Length ||
                hashes.Length != types.Length || expirations.Length != types.Length)
            {
                throw new AssignmentInfrastructureException(
                    "Assignable driver document projection is inconsistent.");
            }

            var documents = new Dictionary<string, DriverDocumentSnapshot>(StringComparer.Ordinal);
            for (var index = 0; index < types.Length; index++)
            {
                documents[types[index]] = new DriverDocumentSnapshot(
                    types[index],
                    statuses[index],
                    objectKeys[index],
                    hashes[index],
                    expirations[index] is { } expiresAt
                        ? new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc))
                        : null);
            }

            candidates.Add(new Candidate(
                new DriverEligibilitySnapshot(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetGuid(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetBoolean(8),
                    reader.IsDBNull(9) ? null : reader.GetBoolean(9),
                    documents,
                    reader.GetString(15)),
                reader.GetInt32(16)));
        }

        return candidates;
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type)
    {
        Value = value ?? DBNull.Value,
    };

    private sealed record OrderRow(
        Guid CityId,
        Guid? ServiceAreaId,
        string Status,
        bool HasActiveAssignment,
        IReadOnlyList<AssignmentVisibilityPackage> Packages);

    private sealed record Candidate(DriverEligibilitySnapshot Snapshot, int ActiveAssignmentCount);
}

public sealed class DisabledAssignableDriversQuery : IAssignableDriversQuery
{
    public Task<AssignableDriverPage> ListAsync(
        ListAssignableDriversQuery query,
        CancellationToken cancellationToken) =>
        throw new AssignmentForbiddenException();
}
