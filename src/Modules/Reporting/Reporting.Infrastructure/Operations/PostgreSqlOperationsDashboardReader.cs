using System.Globalization;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Domain.Tenancy;
using Reporting.Application.Operations;

namespace Reporting.Infrastructure.Operations;

internal sealed class PostgreSqlOperationsDashboardReader(
    NpgsqlDataSource dataSource,
    IOptions<OperationsDashboardOptions> options,
    IOperationsDashboardTelemetry telemetry) : IOperationsDashboardReader
{
    private const string AuthorizationSql = """
        SELECT set_config('app.current_user_id', @user_id::uuid::text, true);
        SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true);
        SET LOCAL ROLE paqueteria_app;
        SELECT m.role
        FROM identity.users AS u
        JOIN organizations.organization_memberships AS m
          ON m.user_id = u.id
        JOIN organizations.organizations AS organization
          ON organization.id = m.organization_id
        WHERE u.id = @user_id
          AND u.status = 'ACTIVE'
          AND m.organization_id = @organization_id
          AND m.status = 'ACTIVE'
          AND organization.status = 'ACTIVE'
          AND m.role IN ('PLATFORM_ADMIN', 'DISPATCHER')
        ORDER BY CASE m.role WHEN 'PLATFORM_ADMIN' THEN 0 ELSE 1 END
        LIMIT 1;
        """;

    private const string ProjectionSql = """
        WITH page AS (
          SELECT
            order_row.id,
            order_row.version,
            order_row.public_id,
            order_row.owner_org_id,
            owner_organization.display_name AS owner_display_name,
            order_row.operator_org_id,
            operator_organization.display_name AS operator_display_name,
            order_row.client_account_id,
            client_account.name AS client_display_name,
            order_row.status,
            order_row.created_at,
            order_row.updated_at,
            order_row.service_type,
            destination.operating_zone_id,
            destination_zone.name AS zone_name,
            destination_zone.zone_type,
            order_row.total_cents,
            order_row.currency,
            order_row.minimum_total_cents_snapshot,
            order_row.financial_override,
            order_row.service_window_from,
            order_row.service_window_to
          FROM orders.orders AS order_row
          LEFT JOIN organizations.organizations AS owner_organization
            ON owner_organization.id = order_row.owner_org_id
          LEFT JOIN organizations.organizations AS operator_organization
            ON operator_organization.id = order_row.operator_org_id
          LEFT JOIN clients.client_accounts AS client_account
            ON client_account.id = order_row.client_account_id
          LEFT JOIN locations.locations AS destination
            ON destination.id = order_row.destination_location_id
          LEFT JOIN locations.operating_zones AS destination_zone
            ON destination_zone.id = destination.operating_zone_id
          WHERE (@order_id::uuid IS NULL OR order_row.id = @order_id)
            AND (@status::text IS NULL OR order_row.status = @status)
            AND (@delivery_zone_id::uuid IS NULL OR destination.operating_zone_id = @delivery_zone_id)
            AND (@client_account_id::uuid IS NULL OR order_row.client_account_id = @client_account_id)
            AND (@owner_org_id::uuid IS NULL OR order_row.owner_org_id = @owner_org_id)
            AND (@operator_org_id::uuid IS NULL OR order_row.operator_org_id = @operator_org_id)
            AND (@service_type::text IS NULL OR order_row.service_type = @service_type)
            AND (@created_from::timestamptz IS NULL OR order_row.created_at >= @created_from)
            AND (@created_to::timestamptz IS NULL OR order_row.created_at <= @created_to)
            AND (
              @unassigned::boolean IS NULL
              OR @unassigned = false
              OR (
                order_row.status IN ('READY_FOR_PICKUP', 'RESCHEDULED')
                AND NOT EXISTS (
                  SELECT 1
                  FROM dispatch.assignments AS candidate
                  WHERE candidate.order_id = order_row.id
                    AND candidate.status IN ('ACCEPTED', 'ACTIVE')
                )
              )
            )
            AND (
              @cursor_updated_at::timestamptz IS NULL
              OR (order_row.updated_at, order_row.id) < (@cursor_updated_at, @cursor_order_id)
            )
          ORDER BY order_row.updated_at DESC, order_row.id DESC
          LIMIT @page_limit
        ),
        active_assignment AS (
          SELECT
            assignment.order_id,
            assignment.id,
            assignment.assignment_type,
            assignment.status,
            assignment.driver_id,
            count(*) OVER (PARTITION BY assignment.order_id) AS active_count,
            row_number() OVER (
              PARTITION BY assignment.order_id
              ORDER BY assignment.created_at DESC, assignment.id DESC
            ) AS row_number
          FROM dispatch.assignments AS assignment
          JOIN page ON page.id = assignment.order_id
          WHERE assignment.status IN ('ACCEPTED', 'ACTIVE')
        )
        SELECT
          page.id,
          page.version,
          page.public_id,
          page.owner_org_id,
          page.owner_display_name,
          page.operator_org_id,
          page.operator_display_name,
          page.client_account_id,
          page.client_display_name,
          page.status,
          page.created_at,
          page.updated_at,
          page.service_type,
          page.operating_zone_id,
          page.zone_name,
          page.zone_type,
          assignment.id AS assignment_id,
          assignment.assignment_type,
          assignment.status AS assignment_status,
          assignment.driver_id,
          assignment.active_count,
          position.latitude,
          position.longitude,
          position.accuracy_m,
          position.captured_at,
          CASE
            WHEN page.total_cents < page.minimum_total_cents_snapshot
              THEN 'BELOW_MINIMUM_SNAPSHOT'
            WHEN page.financial_override ?& ARRAY['actor_id', 'reason', 'valid_until']
              THEN 'AUTHORIZED_OVERRIDE'
            ELSE NULL
          END AS cost_warning,
          page.service_window_from,
          page.service_window_to,
          page.total_cents,
          page.currency
        FROM page
        LEFT JOIN active_assignment AS assignment
          ON assignment.order_id = page.id AND assignment.row_number = 1
        LEFT JOIN LATERAL (
          SELECT
            ST_Y(driver_position.point)::double precision AS latitude,
            ST_X(driver_position.point)::double precision AS longitude,
            driver_position.accuracy_m::double precision AS accuracy_m,
            driver_position.captured_at
          FROM drivers.driver_positions AS driver_position
          WHERE driver_position.driver_id = assignment.driver_id
          ORDER BY
            driver_position.captured_at DESC,
            driver_position.received_at DESC,
            driver_position.id DESC
          LIMIT 1
        ) AS position ON true
        ORDER BY page.updated_at DESC, page.id DESC;
        """;

    public async Task<OperationsDashboardPage> ReadAsync(
        OperationsDashboardRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        using var measurement = telemetry.MeasureLookup();
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var role = await AuthorizeAsync(connection, transaction, request, cancellationToken);
            if (role is null || !OperationsDashboardAuthorizationPolicy.IsAllowed(role.Value, request.MfaSatisfied))
            {
                telemetry.AuthorizationRejected();
                await transaction.RollbackAsync(cancellationToken);
                throw new OperationsDashboardForbiddenException();
            }

            var page = await ReadPageAsync(connection, transaction, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            telemetry.LookupCompleted();
            return page;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationsDashboardForbiddenException)
        {
            throw;
        }
        catch (OperationsDashboardContractException exception)
        {
            telemetry.ContractInvalid("contract");
            throw new OperationsDashboardUnavailableException(
                "The operations dashboard projection is inconsistent.",
                exception);
        }
        catch (OperationsDashboardUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            telemetry.LookupFailed("database");
            throw new OperationsDashboardUnavailableException(
                "The operations dashboard is unavailable.",
                exception);
        }
    }

    private async Task<OrganizationRole?> AuthorizeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperationsDashboardRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AuthorizationSql, connection, transaction)
        {
            CommandTimeout = options.Value.CommandTimeoutSeconds,
        };
        command.Parameters.Add(new NpgsqlParameter<Guid>("user_id", NpgsqlDbType.Uuid)
        {
            TypedValue = request.ActorId,
        });
        command.Parameters.Add(new NpgsqlParameter<Guid[]>(
            "organization_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = [request.OrganizationId],
        });
        command.Parameters.Add(new NpgsqlParameter<Guid>("organization_id", NpgsqlDbType.Uuid)
        {
            TypedValue = request.OrganizationId,
        });
        OrganizationRole? role = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                role = Convert.ToString(
                    reader.GetValue(0),
                    CultureInfo.InvariantCulture) switch
                {
                    "PLATFORM_ADMIN" => OrganizationRole.PlatformAdmin,
                    "DISPATCHER" => OrganizationRole.Dispatcher,
                    _ => role,
                };
            }
        }
        while (await reader.NextResultAsync(cancellationToken));
        return role;
    }

    private async Task<OperationsDashboardPage> ReadPageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperationsDashboardRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ProjectionSql, connection, transaction)
        {
            CommandTimeout = options.Value.CommandTimeoutSeconds,
        };
        AddProjectionParameters(command, request.Filters);
        var items = new List<OperationsDashboardOrder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Map(reader));
        }

        var pageSize = options.Value.DefaultPageSize;
        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveRange(pageSize, items.Count - pageSize);
        }

        var nextCursor = hasMore && items.Count > 0
            ? OperationsDashboardCursorCodec.Encode(items[^1].UpdatedAt, items[^1].OrderId)
            : null;
        return new OperationsDashboardPage(DateTimeOffset.UtcNow, items, nextCursor);
    }

    private void AddProjectionParameters(NpgsqlCommand command, OperationsDashboardFilters filters)
    {
        AddNullable(command, "order_id", NpgsqlDbType.Uuid, filters.OrderId);
        AddNullable(command, "status", NpgsqlDbType.Text, filters.Status);
        AddNullable(command, "delivery_zone_id", NpgsqlDbType.Uuid, filters.DeliveryZoneId);
        AddNullable(command, "client_account_id", NpgsqlDbType.Uuid, filters.ClientAccountId);
        AddNullable(command, "owner_org_id", NpgsqlDbType.Uuid, filters.OwnerOrganizationId);
        AddNullable(command, "operator_org_id", NpgsqlDbType.Uuid, filters.OperatorOrganizationId);
        AddNullable(command, "service_type", NpgsqlDbType.Text, filters.ServiceType);
        AddNullable(command, "created_from", NpgsqlDbType.TimestampTz, filters.CreatedFrom);
        AddNullable(command, "created_to", NpgsqlDbType.TimestampTz, filters.CreatedTo);
        AddNullable(command, "unassigned", NpgsqlDbType.Boolean, filters.Unassigned);
        AddNullable(command, "cursor_updated_at", NpgsqlDbType.TimestampTz, filters.Cursor?.UpdatedAt);
        AddNullable(command, "cursor_order_id", NpgsqlDbType.Uuid, filters.Cursor?.OrderId);
        command.Parameters.Add(new NpgsqlParameter<int>("page_limit", NpgsqlDbType.Integer)
        {
            TypedValue = checked(options.Value.DefaultPageSize + 1),
        });
    }

    private OperationsDashboardOrder Map(NpgsqlDataReader reader)
    {
        var orderId = reader.GetGuid(0);
        var ownerId = reader.GetGuid(3);
        var ownerName = NullableString(reader, 4);
        if (ownerName is null)
        {
            throw new OperationsDashboardContractException("Owner organization name is unavailable.");
        }

        var operatorId = NullableGuid(reader, 5);
        var operatorName = NullableString(reader, 6);
        if (operatorId.HasValue != (operatorName is not null))
        {
            throw new OperationsDashboardContractException("Operator organization name is unavailable.");
        }

        var clientId = NullableGuid(reader, 7);
        var clientName = NullableString(reader, 8);
        var zoneId = NullableGuid(reader, 13);
        var zoneName = NullableString(reader, 14);
        var zoneType = NullableString(reader, 15);
        if (zoneId.HasValue && (zoneName is null || zoneType is not ("CORE" or "STANDARD" or "EXTENDED" or "EXCLUDED")))
        {
            throw new OperationsDashboardContractException("Destination zone is invalid.");
        }

        var assignmentId = NullableGuid(reader, 16);
        var assignmentType = NullableString(reader, 17);
        var assignmentStatus = NullableString(reader, 18);
        var driverId = NullableGuid(reader, 19);
        var activeCount = reader.IsDBNull(20) ? 0L : reader.GetInt64(20);
        if (activeCount > 1)
        {
            telemetry.ActiveAssignmentInconsistent();
            throw new OperationsDashboardContractException("Multiple active assignments exist.");
        }

        OperationsAssignmentSummary? assignment = null;
        if (assignmentId is not null && driverId is not null)
        {
            if (assignmentType is not ("OWN" or "EXTERNAL" or "ALLY_CAPACITY") ||
                assignmentStatus is not ("ACCEPTED" or "ACTIVE"))
            {
                throw new OperationsDashboardContractException("Active assignment is invalid.");
            }

            assignment = new OperationsAssignmentSummary(
                assignmentId.Value,
                assignmentType,
                assignmentStatus,
                driverId.Value,
                OperationsDashboardProjectionPolicy.DriverReference(driverId.Value));
        }

        OperationsDriverLocation? location = null;
        if (!reader.IsDBNull(21))
        {
            var latitude = reader.GetDouble(21);
            var longitude = reader.GetDouble(22);
            var accuracy = reader.GetDouble(23);
            var capturedAt = reader.GetFieldValue<DateTimeOffset>(24);
            if (!OperationsDashboardProjectionPolicy.IsValidLocation(
                    latitude,
                    longitude,
                    accuracy,
                    capturedAt))
            {
                throw new OperationsDashboardContractException("Driver location is invalid.");
            }

            location = new OperationsDriverLocation(latitude, longitude, accuracy, capturedAt);
        }

        var status = reader.GetString(9);
        var createdAt = reader.GetFieldValue<DateTimeOffset>(10);
        var updatedAt = reader.GetFieldValue<DateTimeOffset>(11);
        var serviceType = reader.GetString(12);
        if (!OperationsDashboardVocabulary.IsStatus(status) ||
            !OperationsDashboardVocabulary.IsServiceType(serviceType) ||
            createdAt.Offset != TimeSpan.Zero ||
            updatedAt.Offset != TimeSpan.Zero)
        {
            throw new OperationsDashboardContractException("Order projection is invalid.");
        }

        var costWarning = NullableString(reader, 25);
        if (costWarning is not null and not ("AUTHORIZED_OVERRIDE" or "BELOW_MINIMUM_SNAPSHOT"))
        {
            throw new OperationsDashboardContractException("Cost warning is invalid.");
        }

        // ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02: the order's delivery window; both bounds or neither (enforced by
        // orders_service_window_check), so a half-set or non-UTC window is an inconsistent projection.
        OperationsTimeWindow? deliveryWindow = null;
        if (!reader.IsDBNull(26) || !reader.IsDBNull(27))
        {
            if (reader.IsDBNull(26) || reader.IsDBNull(27))
            {
                throw new OperationsDashboardContractException("Delivery window is invalid.");
            }

            var windowFrom = reader.GetFieldValue<DateTimeOffset>(26);
            var windowTo = reader.GetFieldValue<DateTimeOffset>(27);
            if (windowFrom.Offset != TimeSpan.Zero || windowTo.Offset != TimeSpan.Zero || windowFrom >= windowTo)
            {
                throw new OperationsDashboardContractException("Delivery window is invalid.");
            }

            deliveryWindow = new OperationsTimeWindow(windowFrom, windowTo);
        }

        // UI-PHASE3-INBOX-TOTAL-2026-10-10: the order total, integer MXN cents with IVA included, exactly as stored;
        // the same value AI-05 Order.total returns to these roles. A negative or non-MXN total fails closed.
        var totalCents = reader.GetInt64(28);
        var currency = NullableString(reader, 29);
        if (!OperationsDashboardProjectionPolicy.IsValidTotal(currency, totalCents))
        {
            throw new OperationsDashboardContractException("Order total is invalid.");
        }

        return new OperationsDashboardOrder(
            orderId,
            reader.GetInt32(1),
            reader.GetString(2),
            new OperationsOrganizationSummary(ownerId, ownerName),
            operatorId is null ? null : new OperationsOrganizationSummary(operatorId.Value, operatorName!),
            clientId is not null && clientName is not null
                ? new OperationsClientSummary(clientId.Value, clientName)
                : null,
            status,
            createdAt,
            updatedAt,
            serviceType,
            zoneId is null ? null : new OperationsZoneSummary(zoneId.Value, zoneName!, zoneType!),
            assignment,
            location,
            new OperationsMoney(currency, totalCents),
            costWarning,
            OperationsDashboardProjectionPolicy.IsUnassignedAlert(status, assignment is not null),
            deliveryWindow);
    }

    private static void ValidateRequest(OperationsDashboardRequest request)
    {
        if (request.ActorId == Guid.Empty || request.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Non-empty actor and organization ids are required.", nameof(request));
        }
    }

    private static void AddNullable<T>(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        T? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type)
        {
            Value = value is null ? DBNull.Value : value,
        });
    }

    private static Guid? NullableGuid(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
