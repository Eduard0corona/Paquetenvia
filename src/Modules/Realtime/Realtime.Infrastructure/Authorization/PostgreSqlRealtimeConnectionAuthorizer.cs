using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Tracking;
using Organizations.Application.Auditing;
using Paqueteria.Domain.Tenancy;
using Realtime.Application.Authorization;
using Realtime.Application.Configuration;

namespace Realtime.Infrastructure.Authorization;

internal sealed class PostgreSqlRealtimeConnectionAuthorizer(
    NpgsqlDataSource dataSource,
    IPublicTrackingProjectionReader trackingReader,
    IPlatformAdminTenantActivationAudit platformAdminAudit,
    IOptions<RealtimeOptions> options,
    ILogger<PostgreSqlRealtimeConnectionAuthorizer> logger) : IRealtimeConnectionAuthorizer
{
    private const string OperationsQuery = """
        SELECT m.role
        FROM identity.users AS u
        JOIN organizations.organization_memberships AS m
          ON m.user_id=u.id
        JOIN organizations.organizations AS o
          ON o.id=m.organization_id
        WHERE u.id=@user_id
          AND u.status='ACTIVE'
          AND m.organization_id=@organization_id
          AND m.status='ACTIVE'
          AND m.role IN ('PLATFORM_ADMIN','DISPATCHER')
          AND o.status='ACTIVE'
        ORDER BY CASE m.role WHEN 'PLATFORM_ADMIN' THEN 0 ELSE 1 END
        LIMIT 1;
        """;

    private const string DriverQuery = """
        SELECT d.id
        FROM identity.users AS u
        JOIN organizations.organization_memberships AS m
          ON m.user_id=u.id
        JOIN organizations.organizations AS o
          ON o.id=m.organization_id
        JOIN drivers.driver_profiles AS d
          ON d.user_id=u.id AND d.org_id=m.organization_id
        WHERE u.id=@user_id
          AND u.status='ACTIVE'
          AND m.organization_id=@organization_id
          AND m.role='DRIVER'
          AND m.status='ACTIVE'
          AND o.status='ACTIVE'
          AND d.driver_type='OWN'
          AND d.status='ACTIVE'
        LIMIT 1;
        """;

    private const string AssignmentsQuery = """
        SELECT a.id
        FROM dispatch.assignments AS a
        WHERE a.driver_id=@driver_id
          AND a.status IN ('ACCEPTED','ACTIVE')
        ORDER BY a.created_at, a.id
        LIMIT @assignment_limit;
        """;

    public async ValueTask<ConnectionAuthorizationResult<OperationsConnectionAuthorization>>
        AuthorizeOperationsAsync(
            PrivateRealtimeConnectionRequest request,
            CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        try
        {
            var role = await ExecuteWithRetryAsync(
                request,
                async (connection, transaction, ct) =>
                {
                    await using var command = new NpgsqlCommand(
                        OperationsQuery,
                        connection,
                        transaction)
                    {
                        CommandTimeout = options.Value.AuthorizationCommandTimeoutSeconds,
                    };
                    AddPrivateParameters(command, request);
                    var value = await command.ExecuteScalarAsync(ct);
                    return value is null or DBNull
                        ? null
                        : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                },
                cancellationToken);

            var resolvedRole = role switch
            {
                "DISPATCHER" => OrganizationRole.Dispatcher,
                "PLATFORM_ADMIN" => OrganizationRole.PlatformAdmin,
                _ => (OrganizationRole?)null,
            };
            if (resolvedRole is null ||
                !RealtimeOperationsRolePolicy.IsAllowed(resolvedRole.Value, request.MfaSatisfied))
            {
                return ConnectionAuthorizationResult<OperationsConnectionAuthorization>.Rejected;
            }

            var authorization = new OperationsConnectionAuthorization(
                request.OrganizationId,
                resolvedRole.Value);
            if (resolvedRole == OrganizationRole.PlatformAdmin)
            {
                await platformAdminAudit.RecordAsync(
                    request.UserId,
                    request.OrganizationId,
                    request.RequestId,
                    cancellationToken);
            }

            return ConnectionAuthorizationResult<OperationsConnectionAuthorization>.Authorized(authorization);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RealtimeAuthorizationInfrastructureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError("realtime_connection_authorization_failed");
            throw new RealtimeAuthorizationInfrastructureException(
                "Realtime connection authorization is unavailable.",
                exception);
        }
    }

    public async ValueTask<ConnectionAuthorizationResult<DriverConnectionAuthorization>>
        AuthorizeDriverAsync(
            PrivateRealtimeConnectionRequest request,
            CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        try
        {
            var authorization = await ExecuteWithRetryAsync(
                request,
                async (connection, transaction, ct) =>
                {
                    await using var driverCommand = new NpgsqlCommand(
                        DriverQuery,
                        connection,
                        transaction)
                    {
                        CommandTimeout = options.Value.AuthorizationCommandTimeoutSeconds,
                    };
                    AddPrivateParameters(driverCommand, request);
                    var driverValue = await driverCommand.ExecuteScalarAsync(ct);
                    if (driverValue is not Guid driverId || driverId == Guid.Empty)
                    {
                        return null;
                    }

                    await using var assignmentsCommand = new NpgsqlCommand(
                        AssignmentsQuery,
                        connection,
                        transaction)
                    {
                        CommandTimeout = options.Value.AuthorizationCommandTimeoutSeconds,
                    };
                    assignmentsCommand.Parameters.Add(new NpgsqlParameter<Guid>("driver_id", NpgsqlDbType.Uuid)
                    {
                        TypedValue = driverId,
                    });
                    assignmentsCommand.Parameters.Add(new NpgsqlParameter<int>("assignment_limit", NpgsqlDbType.Integer)
                    {
                        TypedValue = checked(options.Value.MaximumDriverAssignmentGroups + 1),
                    });
                    var assignments = new List<Guid>();
                    await using var reader = await assignmentsCommand.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        assignments.Add(reader.GetGuid(0));
                    }

                    if (assignments.Count > options.Value.MaximumDriverAssignmentGroups)
                    {
                        return null;
                    }

                    return new DriverConnectionAuthorization(
                        request.OrganizationId,
                        driverId,
                        assignments);
                },
                cancellationToken);

            return authorization is null
                ? ConnectionAuthorizationResult<DriverConnectionAuthorization>.Rejected
                : ConnectionAuthorizationResult<DriverConnectionAuthorization>.Authorized(authorization);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RealtimeAuthorizationInfrastructureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError("realtime_connection_authorization_failed");
            throw new RealtimeAuthorizationInfrastructureException(
                "Realtime connection authorization is unavailable.",
                exception);
        }
    }

    public async ValueTask<ConnectionAuthorizationResult<TrackingConnectionAuthorization>>
        AuthorizeTrackingAsync(
            string exactToken,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactToken);
        try
        {
            var result = await trackingReader.FindAsync(exactToken, cancellationToken);
            if (!result.IsFound || result.Projection is null)
            {
                return ConnectionAuthorizationResult<TrackingConnectionAuthorization>.Rejected;
            }

            var authorization = new TrackingConnectionAuthorization(result.Projection.PublicId);
            return ConnectionAuthorizationResult<TrackingConnectionAuthorization>.Authorized(authorization);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError("realtime_connection_authorization_failed");
            throw new RealtimeAuthorizationInfrastructureException(
                "Realtime connection authorization is unavailable.",
                exception);
        }
    }

    internal async Task<T> ExecuteWithRetryAsync<T>(
        PrivateRealtimeConnectionRequest request,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await ApplyTenantContextAsync(connection, transaction, request, cancellationToken);
                var result = await operation(connection, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (NpgsqlException exception)
                when (exception.IsTransient && attempt < options.Value.AuthorizationRetryCount)
            {
                logger.LogWarning("realtime_connection_authorization_retry");
            }
        }
    }

    private async Task ApplyTenantContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PrivateRealtimeConnectionRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT set_config('app.current_user_id', @user_id::uuid::text, true);
            SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true);
            SET LOCAL ROLE paqueteria_app;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = options.Value.AuthorizationCommandTimeoutSeconds,
        };
        command.Parameters.Add(new NpgsqlParameter<Guid>("user_id", NpgsqlDbType.Uuid)
        {
            TypedValue = request.UserId,
        });
        command.Parameters.Add(new NpgsqlParameter<Guid[]>(
            "organization_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = [request.OrganizationId],
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddPrivateParameters(
        NpgsqlCommand command,
        PrivateRealtimeConnectionRequest request)
    {
        command.Parameters.Add(new NpgsqlParameter<Guid>("user_id", NpgsqlDbType.Uuid)
        {
            TypedValue = request.UserId,
        });
        command.Parameters.Add(new NpgsqlParameter<Guid>("organization_id", NpgsqlDbType.Uuid)
        {
            TypedValue = request.OrganizationId,
        });
    }

    private static void ValidateRequest(PrivateRealtimeConnectionRequest request)
    {
        if (request.UserId == Guid.Empty || request.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Non-empty authorization identifiers are required.", nameof(request));
        }
    }
}
