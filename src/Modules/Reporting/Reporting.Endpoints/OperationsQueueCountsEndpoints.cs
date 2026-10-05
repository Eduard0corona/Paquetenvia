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

/// <summary>
/// UI-PHASE2-QUEUE-COUNTS-2026-10-05: AI-05 getOperationsQueueCounts. The order is request shape (no query parameter
/// at all), then the capability, then the counts read in one tenant transaction.
/// </summary>
public static class OperationsQueueCountsEndpoints
{
    public const string Route = "/api/v1/operations/queue-counts";

    public static IEndpointRouteBuilder MapOperationsQueueCountsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, GetAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .AddEndpointFilter<OperationsDashboardResponseHeadersFilter>()
            .WithName("getOperationsQueueCounts")
            .WithTags("Operations")
            .Produces<OperationsQueueCountsResponse>(StatusCodes.Status200OK)
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
        IOperationsQueueCountsReader reader,
        IOperationsDashboardTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        // The operation takes no parameter: any query string is a malformed request, decided before capability
        // and before anything is read, so no caller can believe a filter was applied.
        if (httpContext.Request.Query.Count != 0 || httpContext.Request.QueryString.HasValue)
        {
            telemetry.ContractInvalid("contract");
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request.");
        }

        if (!session.IsActive || session.UserId is not { } actorId || !tenantContext.IsSelected)
        {
            telemetry.AuthorizationRejected();
            return TenantCapabilityGate.Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.GetOperationsQueueCounts) is { } denied)
        {
            telemetry.AuthorizationRejected();
            return denied;
        }

        try
        {
            var counts = await reader.ReadAsync(
                new OperationsQueueCountsRequest(actorId, tenantContext.OrganizationId, session.MfaSatisfied),
                cancellationToken);
            return Results.Ok(ToResponse(counts));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationsDashboardForbiddenException)
        {
            return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.GetOperationsQueueCounts);
        }
        catch (OperationsDashboardUnavailableException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service unavailable.");
        }
    }

    internal static OperationsQueueCountsResponse ToResponse(OperationsQueueCounts counts) => new(
        counts.GeneratedAt,
        counts.Total,
        counts.ByStatus.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
        new OperationsQueuesResponse(
            counts.Unassigned,
            counts.NeedsAttention,
            counts.PriceReview,
            counts.DeliveredNotClosed,
            counts.EnRoute));
}

public sealed record OperationsQueueCountsResponse(
    [property: JsonPropertyName("generated_at")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("by_status")] IReadOnlyDictionary<string, long> ByStatus,
    [property: JsonPropertyName("queues")] OperationsQueuesResponse Queues);

public sealed record OperationsQueuesResponse(
    [property: JsonPropertyName("unassigned")] long Unassigned,
    [property: JsonPropertyName("needs_attention")] long NeedsAttention,
    [property: JsonPropertyName("price_review")] long PriceReview,
    [property: JsonPropertyName("delivered_not_closed")] long DeliveredNotClosed,
    [property: JsonPropertyName("en_route")] long EnRoute);
