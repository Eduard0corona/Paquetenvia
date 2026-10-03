using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Tenancy;
using Reporting.Application.Operations;

namespace Reporting.Endpoints;

public static class OperationsDashboardEndpoints
{
    private static readonly string[] KnownParameters =
    [
        "order_id",
        "status",
        "delivery_zone_id",
        "client_account_id",
        "owner_org_id",
        "operator_org_id",
        "service_type",
        "created_from",
        "created_to",
        "unassigned",
        "cursor",
    ];

    public static IEndpointRouteBuilder MapOperationsDashboardEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/operations/dashboard", GetAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .AddEndpointFilter<OperationsDashboardResponseHeadersFilter>()
            .WithName("getOperationsDashboard")
            .WithTags("Operations")
            .Produces<OperationsDashboardResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IOperationsDashboardReader reader,
        IOperationsDashboardTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        if (!session.IsActive ||
            session.UserId is not { } actorId ||
            !tenantContext.IsSelected)
        {
            telemetry.AuthorizationRejected();
            return Forbidden();
        }

        if (!TryBindFilters(httpContext.Request.Query, out var filters))
        {
            telemetry.ContractInvalid("contract");
            return InvalidRequest();
        }

        try
        {
            var page = await reader.ReadAsync(
                new OperationsDashboardRequest(
                    actorId,
                    tenantContext.OrganizationId,
                    session.MfaSatisfied,
                    filters!),
                cancellationToken);
            return Results.Ok(ToResponse(page));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationsDashboardForbiddenException)
        {
            return Forbidden();
        }
        catch (OperationsDashboardUnavailableException)
        {
            return Unavailable();
        }
    }

    internal static bool TryBindFilters(
        IQueryCollection query,
        out OperationsDashboardFilters? filters)
    {
        filters = null;
        foreach (var name in KnownParameters)
        {
            if (query.TryGetValue(name, out var values) && values.Count != 1)
            {
                return false;
            }
        }

        if (!TryGuid(query, "order_id", out var orderId) ||
            !TryGuid(query, "delivery_zone_id", out var deliveryZoneId) ||
            !TryGuid(query, "client_account_id", out var clientAccountId) ||
            !TryGuid(query, "owner_org_id", out var ownerOrganizationId) ||
            !TryGuid(query, "operator_org_id", out var operatorOrganizationId) ||
            !TryExact(query, "status", OperationsDashboardVocabulary.IsStatus, out var status) ||
            !TryExact(query, "service_type", OperationsDashboardVocabulary.IsServiceType, out var serviceType) ||
            !TryUtc(query, "created_from", out var createdFrom) ||
            !TryUtc(query, "created_to", out var createdTo) ||
            !TryBoolean(query, "unassigned", out var unassigned) ||
            !TryCursor(query, out var cursor))
        {
            return false;
        }

        if (createdFrom is not null &&
            createdTo is not null &&
            (createdFrom > createdTo || createdTo - createdFrom > TimeSpan.FromDays(31)))
        {
            return false;
        }

        filters = new OperationsDashboardFilters(
            orderId,
            status,
            deliveryZoneId,
            clientAccountId,
            ownerOrganizationId,
            operatorOrganizationId,
            serviceType,
            createdFrom,
            createdTo,
            unassigned,
            cursor);
        return true;
    }

    private static bool TryGuid(IQueryCollection query, string name, out Guid? value)
    {
        value = null;
        if (!query.TryGetValue(name, out var values))
        {
            return true;
        }

        var text = values[0];
        if (text is null ||
            !Guid.TryParseExact(text, "D", out var parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(parsed.ToString("D"), text, StringComparison.Ordinal))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryExact(
        IQueryCollection query,
        string name,
        Func<string?, bool> policy,
        out string? value)
    {
        value = null;
        if (!query.TryGetValue(name, out var values))
        {
            return true;
        }

        value = values[0];
        return policy(value);
    }

    private static bool TryUtc(
        IQueryCollection query,
        string name,
        out DateTimeOffset? value)
    {
        value = null;
        if (!query.TryGetValue(name, out var values))
        {
            return true;
        }

        var text = values[0];
        if (text is null ||
            !text.EndsWith('Z') ||
            !DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryBoolean(
        IQueryCollection query,
        string name,
        out bool? value)
    {
        value = null;
        if (!query.TryGetValue(name, out var values))
        {
            return true;
        }

        value = values[0] switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };
        return value is not null;
    }

    private static bool TryCursor(
        IQueryCollection query,
        out OperationsDashboardCursor? cursor)
    {
        cursor = null;
        return !query.TryGetValue("cursor", out var values) ||
            OperationsDashboardCursorCodec.TryDecode(values[0], out cursor);
    }

    private static OperationsDashboardResponse ToResponse(OperationsDashboardPage page) => new(
        page.GeneratedAt,
        page.Items.Select(ToResponse).ToArray(),
        page.NextCursor);

    private static OperationsDashboardOrderResponse ToResponse(OperationsDashboardOrder item) => new(
        item.OrderId,
        item.AggregateVersion,
        item.PublicId,
        new OperationsOrganizationResponse(item.Owner.OrganizationId, item.Owner.DisplayName),
        item.Operator is null
            ? null
            : new OperationsOrganizationResponse(
                item.Operator.OrganizationId,
                item.Operator.DisplayName),
        item.Client is null
            ? null
            : new OperationsClientResponse(
                item.Client.ClientAccountId,
                item.Client.DisplayName),
        item.Status,
        item.CreatedAt,
        item.UpdatedAt,
        item.ServiceType,
        null,
        item.DeliveryWindow is null
            ? null
            : new OperationsTimeWindowResponse(item.DeliveryWindow.From, item.DeliveryWindow.To),
        item.DeliveryZone is null
            ? null
            : new OperationsZoneResponse(
                item.DeliveryZone.OperatingZoneId,
                item.DeliveryZone.Name,
                item.DeliveryZone.ZoneType),
        item.Assignment is null
            ? null
            : new OperationsAssignmentResponse(
                item.Assignment.AssignmentId,
                item.Assignment.AssignmentType,
                item.Assignment.Status,
                item.Assignment.DriverId,
                item.Assignment.DriverReference),
        item.LatestDriverLocation is null
            ? null
            : new OperationsDriverLocationResponse(
                item.LatestDriverLocation.Latitude,
                item.LatestDriverLocation.Longitude,
                item.LatestDriverLocation.AccuracyMeters,
                item.LatestDriverLocation.CapturedAt),
        item.CostWarning,
        item.UnassignedAlert);

    private static IResult InvalidRequest() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request.");

    private static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
}

internal sealed class OperationsDashboardResponseHeadersFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var response = context.HttpContext.Response;
        response.Headers.CacheControl = "no-store, private";
        response.Headers.Pragma = "no-cache";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        return await next(context);
    }
}

public sealed record OperationsDashboardResponse(
    [property: JsonPropertyName("generated_at")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("items")] IReadOnlyList<OperationsDashboardOrderResponse> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record OperationsDashboardOrderResponse(
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("aggregate_version")] int AggregateVersion,
    [property: JsonPropertyName("public_id")] string PublicId,
    [property: JsonPropertyName("owner")] OperationsOrganizationResponse Owner,
    [property: JsonPropertyName("operator")] OperationsOrganizationResponse? Operator,
    [property: JsonPropertyName("client")] OperationsClientResponse? Client,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("service_type")] string ServiceType,
    [property: JsonPropertyName("pickup_window")] OperationsTimeWindowResponse? PickupWindow,
    [property: JsonPropertyName("delivery_window")] OperationsTimeWindowResponse? DeliveryWindow,
    [property: JsonPropertyName("delivery_zone")] OperationsZoneResponse? DeliveryZone,
    [property: JsonPropertyName("assignment")] OperationsAssignmentResponse? Assignment,
    [property: JsonPropertyName("latest_driver_location")] OperationsDriverLocationResponse? LatestDriverLocation,
    [property: JsonPropertyName("cost_warning")] string? CostWarning,
    [property: JsonPropertyName("unassigned_alert")] bool UnassignedAlert);

public sealed record OperationsOrganizationResponse(
    [property: JsonPropertyName("organization_id")] Guid OrganizationId,
    [property: JsonPropertyName("display_name")] string DisplayName);

public sealed record OperationsClientResponse(
    [property: JsonPropertyName("client_account_id")] Guid ClientAccountId,
    [property: JsonPropertyName("display_name")] string DisplayName);

public sealed record OperationsZoneResponse(
    [property: JsonPropertyName("operating_zone_id")] Guid OperatingZoneId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("zone_type")] string ZoneType);

public sealed record OperationsAssignmentResponse(
    [property: JsonPropertyName("assignment_id")] Guid AssignmentId,
    [property: JsonPropertyName("assignment_type")] string AssignmentType,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("driver_id")] Guid DriverId,
    [property: JsonPropertyName("driver_reference")] string DriverReference);

public sealed record OperationsDriverLocationResponse(
    [property: JsonPropertyName("lat")] double Latitude,
    [property: JsonPropertyName("lng")] double Longitude,
    [property: JsonPropertyName("accuracy_m")] double AccuracyMeters,
    [property: JsonPropertyName("captured_at")] DateTimeOffset CapturedAt);

public sealed record OperationsTimeWindowResponse(
    [property: JsonPropertyName("from")] DateTimeOffset From,
    [property: JsonPropertyName("to")] DateTimeOffset To);
