using Drivers.Application.Eligibility;
using Drivers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Drivers.Infrastructure.Eligibility;

public sealed class PostgreSqlDriverEligibilityService(
    TenantTransactionContext<DriversDbContext> transactionContext,
    IOptions<DriversOptions> options) : IDriverEligibilityService
{
    public Task<DriverEligibilityResult> EvaluateAsync(
        EvaluateOwnDriverEligibilityCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var policy = options.Value.Eligibility.ToPolicy();
        return transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
            async (dbContext, token) =>
            {
                var snapshot = await ReadSnapshotAsync(dbContext, command, token);
                return DriverEligibilityPolicy.Evaluate(command, snapshot, policy);
            },
            cancellationToken);
    }

    private static async Task<DriverEligibilitySnapshot?> ReadSnapshotAsync(
        DriversDbContext dbContext,
        EvaluateOwnDriverEligibilityCommand command,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
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
            WHERE p.id=@driver_id AND p.org_id=@organization_id;
            """;

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

        await using (var profile = new NpgsqlCommand(profileSql, connection, transaction))
        {
            profile.Parameters.Add(new NpgsqlParameter<Guid>("driver_id", NpgsqlDbType.Uuid)
            {
                TypedValue = command.DriverId,
            });
            profile.Parameters.Add(new NpgsqlParameter<Guid>("organization_id", NpgsqlDbType.Uuid)
            {
                TypedValue = command.OrganizationId,
            });
            profile.Parameters.Add(new NpgsqlParameter<Guid?>("service_area_id", NpgsqlDbType.Uuid)
            {
                TypedValue = command.ServiceAreaId,
            });
            profile.Parameters.Add(new NpgsqlParameter<Guid>("city_id", NpgsqlDbType.Uuid)
            {
                TypedValue = command.CityId,
            });

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
            ORDER BY document_type,created_at DESC,id DESC;
            """;
        var documents = new Dictionary<string, DriverDocumentSnapshot>(StringComparer.Ordinal);
        await using (var documentCommand = new NpgsqlCommand(documentsSql, connection, transaction))
        {
            documentCommand.Parameters.Add(new NpgsqlParameter<Guid>("driver_id", NpgsqlDbType.Uuid)
            {
                TypedValue = command.DriverId,
            });
            documentCommand.Parameters.Add(new NpgsqlParameter<Guid>("organization_id", NpgsqlDbType.Uuid)
            {
                TypedValue = command.OrganizationId,
            });
            await using var reader = await documentCommand.ExecuteReaderAsync(cancellationToken);
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
}
