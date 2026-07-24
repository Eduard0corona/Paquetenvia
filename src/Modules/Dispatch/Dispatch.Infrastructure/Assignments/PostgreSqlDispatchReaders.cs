using System.Data.Common;
using Dispatch.Application.Assignments;
using Drivers.Application.Eligibility;
using Npgsql;
using NpgsqlTypes;

namespace Dispatch.Infrastructure.Assignments;

public sealed class PostgreSqlDispatchAuthorizationReader : IDispatchAuthorizationReader
{
    public async Task<DispatchAuthorizationSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid actorId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT u.status,m.role,m.status
            FROM identity.users u
            LEFT JOIN organizations.organization_memberships m
              ON m.user_id=u.id AND m.organization_id=@organization_id
            WHERE u.id=@actor_id
            ORDER BY CASE m.role WHEN 'PLATFORM_ADMIN' THEN 0 WHEN 'DISPATCHER' THEN 1 ELSE 2 END
            LIMIT 1
            """;
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("actor_id", NpgsqlDbType.Uuid)
        {
            TypedValue = actorId,
        });
        command.Parameters.Add(new NpgsqlParameter<Guid>("organization_id", NpgsqlDbType.Uuid)
        {
            TypedValue = organizationId,
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new(null, false, false);
        }

        return new(
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(0) == "ACTIVE",
            !reader.IsDBNull(2) && reader.GetString(2) == "ACTIVE");
    }
}

public sealed class PostgreSqlDispatchDriverEligibilityReader : IDispatchDriverEligibilityReader
{
    public async Task<DriverEligibilitySnapshot?> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        EvaluateOwnDriverEligibilityCommand eligibility,
        CancellationToken cancellationToken)
    {
        const string profileSql =
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
                   ) END
            FROM drivers.driver_profiles p
            LEFT JOIN identity.users u ON u.id=p.user_id
            WHERE p.id=@driver_id AND p.org_id=@organization_id
            """;
        var npgsqlConnection = (NpgsqlConnection)connection;
        var npgsqlTransaction = (NpgsqlTransaction)transaction;

        Guid driverId;
        Guid organizationId;
        Guid userId;
        Guid homeCityId;
        string driverType;
        string vehicleType;
        string profileStatus;
        string? userStatus;
        bool membershipActive;
        bool? serviceAreaEligible;

        await using (var profile = new NpgsqlCommand(profileSql, npgsqlConnection, npgsqlTransaction))
        {
            profile.Parameters.Add(P("driver_id", NpgsqlDbType.Uuid, eligibility.DriverId));
            profile.Parameters.Add(P("organization_id", NpgsqlDbType.Uuid, eligibility.OrganizationId));
            profile.Parameters.Add(P("service_area_id", NpgsqlDbType.Uuid, eligibility.ServiceAreaId));
            profile.Parameters.Add(P("city_id", NpgsqlDbType.Uuid, eligibility.CityId));
            await using var reader = await profile.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            driverId = reader.GetGuid(0);
            organizationId = reader.GetGuid(1);
            userId = reader.GetGuid(2);
            homeCityId = reader.GetGuid(3);
            driverType = reader.GetString(4);
            vehicleType = reader.GetString(5);
            profileStatus = reader.GetString(6);
            userStatus = reader.IsDBNull(7) ? null : reader.GetString(7);
            membershipActive = reader.GetBoolean(8);
            serviceAreaEligible = reader.IsDBNull(9) ? null : reader.GetBoolean(9);
        }

        const string documentsSql =
            """
            SELECT DISTINCT ON (document_type)
                   document_type,status,object_key,sha256,expires_at
            FROM drivers.driver_documents
            WHERE driver_id=@driver_id AND org_id=@organization_id
            ORDER BY document_type,created_at DESC,id DESC
            """;
        var documents = new Dictionary<string, DriverDocumentSnapshot>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(documentsSql, npgsqlConnection, npgsqlTransaction))
        {
            command.Parameters.Add(P("driver_id", NpgsqlDbType.Uuid, eligibility.DriverId));
            command.Parameters.Add(P("organization_id", NpgsqlDbType.Uuid, eligibility.OrganizationId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var type = reader.GetString(0);
                documents[type] = new DriverDocumentSnapshot(
                    type,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetFieldValue<byte[]>(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
            }
        }

        return new DriverEligibilitySnapshot(
            driverId,
            organizationId,
            userId,
            homeCityId,
            driverType,
            vehicleType,
            profileStatus,
            userStatus,
            membershipActive,
            serviceAreaEligible,
            documents);
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type)
    {
        Value = value ?? DBNull.Value,
    };
}

public sealed class PostgreSqlAssignmentReplayEvidenceReader : IAssignmentReplayEvidenceReader
{
    public async Task<AssignmentReplayEvidence> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid assignmentId,
        Guid orderId,
        Guid driverId,
        long costCents,
        CancellationToken cancellationToken)
    {
        const string assignmentSql =
            """
            SELECT id,order_id,driver_id,status,cost_cents
            FROM dispatch.assignments
            WHERE id=@assignment_id AND order_id=@order_id AND driver_id=@driver_id
              AND cost_cents=@cost_cents
              AND (owner_org_id=@organization_id OR operator_org_id=@organization_id)
            """;
        var npgsqlConnection = (NpgsqlConnection)connection;
        var npgsqlTransaction = (NpgsqlTransaction)transaction;
        int assignmentCount;
        Guid? foundAssignmentId;
        Guid? foundOrderId;
        Guid? foundDriverId;
        string? status;
        long? foundCost;
        await using (var command = new NpgsqlCommand(assignmentSql, npgsqlConnection, npgsqlTransaction))
        {
            command.Parameters.Add(P("assignment_id", NpgsqlDbType.Uuid, assignmentId));
            command.Parameters.Add(P("order_id", NpgsqlDbType.Uuid, orderId));
            command.Parameters.Add(P("driver_id", NpgsqlDbType.Uuid, driverId));
            command.Parameters.Add(P("cost_cents", NpgsqlDbType.Bigint, costCents));
            command.Parameters.Add(P("organization_id", NpgsqlDbType.Uuid, organizationId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                assignmentCount = 1;
                foundAssignmentId = reader.GetGuid(0);
                foundOrderId = reader.GetGuid(1);
                foundDriverId = reader.GetGuid(2);
                status = reader.GetString(3);
                foundCost = reader.GetInt64(4);
            }
            else
            {
                assignmentCount = 0;
                foundAssignmentId = null;
                foundOrderId = null;
                foundDriverId = null;
                status = null;
                foundCost = null;
            }
        }

        const string eventSql =
            """
            SELECT count(*)::integer,min(payload->>'previous_status'),min(payload->>'new_status')
            FROM orders.order_events
            WHERE order_id=@order_id AND event_type='ORDER_STATUS_CHANGED'
              AND owner_org_id=@organization_id
              AND payload->>'assignment_id'=@assignment_id_text
            """;
        int eventCount;
        string? previousStatus;
        string? newStatus;
        await using (var command = new NpgsqlCommand(eventSql, npgsqlConnection, npgsqlTransaction))
        {
            command.Parameters.Add(P("order_id", NpgsqlDbType.Uuid, orderId));
            command.Parameters.Add(P("organization_id", NpgsqlDbType.Uuid, organizationId));
            command.Parameters.Add(P("assignment_id_text", NpgsqlDbType.Text, assignmentId.ToString("D")));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            eventCount = reader.GetInt32(0);
            previousStatus = reader.IsDBNull(1) ? null : reader.GetString(1);
            newStatus = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        return new(
            assignmentCount,
            foundAssignmentId,
            foundOrderId,
            foundDriverId,
            status,
            foundCost,
            eventCount,
            previousStatus,
            newStatus);
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type)
    {
        Value = value ?? DBNull.Value,
    };
}
