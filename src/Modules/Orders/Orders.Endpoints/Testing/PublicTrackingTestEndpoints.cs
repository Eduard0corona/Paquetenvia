using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orders.Application.Tracking;

namespace Orders.Endpoints.Testing;

public static class PublicTrackingTestEndpoints
{
    public static IEndpointRouteBuilder MapPublicTrackingTestProbe(
        this IEndpointRouteBuilder endpoints,
        IWebHostEnvironment environment)
    {
        if (!string.Equals(environment.EnvironmentName, "Testing", StringComparison.Ordinal))
        {
            return endpoints;
        }

        endpoints.MapGet("/__tests/tracking/{token}", FindAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> FindAsync(
        string token,
        IPublicTrackingProjectionReader reader,
        HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        var result = await reader.FindAsync(token, httpContext.RequestAborted);
        if (!result.IsFound || result.Projection is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not Found",
                type: "https://httpstatuses.com/404");
        }

        var projection = result.Projection;
        return Results.Ok(new
        {
            public_id = projection.PublicId,
            public_status = PublicOrderStatusPolicy.ToContractValue(projection.PublicStatus),
            estimated_window = projection.EstimatedWindow,
            timeline = projection.Timeline.Select(item => new
            {
                code = ToContractValue(item.Code),
                occurred_at = item.OccurredAt,
            }),
        });
    }

    private static string ToContractValue(PublicTimelineEventCode code) => code switch
    {
        PublicTimelineEventCode.OrderCreated => "ORDER_CREATED",
        PublicTimelineEventCode.PickupScheduled => "PICKUP_SCHEDULED",
        PublicTimelineEventCode.PickedUp => "PICKED_UP",
        PublicTimelineEventCode.InTransit => "IN_TRANSIT",
        PublicTimelineEventCode.OutForDelivery => "OUT_FOR_DELIVERY",
        PublicTimelineEventCode.DeliveryAttempted => "DELIVERY_ATTEMPTED",
        PublicTimelineEventCode.Rescheduled => "RESCHEDULED",
        PublicTimelineEventCode.Delivered => "DELIVERED",
        PublicTimelineEventCode.Returning => "RETURNING",
        PublicTimelineEventCode.Returned => "RETURNED",
        PublicTimelineEventCode.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown public event code."),
    };
}
