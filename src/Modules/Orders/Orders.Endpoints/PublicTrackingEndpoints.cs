using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orders.Application.Tracking;

namespace Orders.Endpoints;

public static class PublicTrackingEndpointDefaults
{
    public const string Path = "/api/v1/tracking/{token}";
    public const string PathPrefix = "/api/v1/tracking";
    public const string CorsPolicy = "PublicTracking";
    public const string RateLimitPolicy = "PublicTrackingLookup";

    // Case-insensitive like routing: every path routing can send to the lookup gets the privacy headers.
    public static bool IsLookupPath(PathString path) =>
        path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase);
}

public static class PublicTrackingEndpoints
{
    public static IApplicationBuilder UsePublicTrackingResponseHeaders(
        this IApplicationBuilder app) =>
        app.UseMiddleware<PublicTrackingResponseHeadersMiddleware>();

    public static IEndpointRouteBuilder MapPublicTrackingEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(PublicTrackingEndpointDefaults.Path, FindAsync)
            .AllowAnonymous()
            .RequireCors(PublicTrackingEndpointDefaults.CorsPolicy)
            .RequireRateLimiting(PublicTrackingEndpointDefaults.RateLimitPolicy)
            .WithName("publicTracking")
            .WithTags("Tracking")
            .Produces<PublicTrackingResponse>(StatusCodes.Status200OK)
            .Produces<PublicTrackingProblemResponse>(
                StatusCodes.Status404NotFound,
                "application/problem+json")
            .Produces<PublicTrackingProblemResponse>(
                StatusCodes.Status429TooManyRequests,
                "application/problem+json")
            .Produces<PublicTrackingProblemResponse>(
                StatusCodes.Status503ServiceUnavailable,
                "application/problem+json");
        return endpoints;
    }

    private static async Task<IResult> FindAsync(
        HttpContext httpContext,
        IPublicTrackingProjectionReader reader,
        CancellationToken cancellationToken)
    {
        // The route value is the redacted placeholder: the token is read only from the feature set by
        // PublicTrackingPathRedaction before hosting diagnostics ran. Without it the host is misconfigured
        // and the token may already be in logs, so the lookup fails loudly instead of serving.
        var feature = httpContext.Features.Get<IPublicTrackingTokenFeature>()
            ?? throw new InvalidOperationException(
                "Public tracking path redaction is not registered; refusing to serve the lookup.");
        var result = feature.Token is { } token
            ? await reader.FindAsync(token, cancellationToken)
            : PublicTrackingLookupResult.NotFound;
        if (!result.IsFound || result.Projection is null)
        {
            return Results.Json(
                PublicTrackingProblemResponse.NotFound,
                statusCode: StatusCodes.Status404NotFound,
                contentType: "application/problem+json");
        }

        var projection = result.Projection;
        return Results.Ok(new PublicTrackingResponse(
            projection.PublicId,
            PublicOrderStatusPolicy.ToContractValue(projection.PublicStatus),
            projection.AggregateVersion,
            projection.EstimatedWindow,
            projection.Timeline.Select(static item => new PublicTrackingTimelineResponse(
                ToContractValue(item.Code),
                item.OccurredAt)).ToArray()));
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
        _ => throw new ArgumentOutOfRangeException(
            nameof(code),
            code,
            "Unknown public event code."),
    };
}

internal sealed class PublicTrackingResponseHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (PublicTrackingEndpointDefaults.IsLookupPath(context.Request.Path))
        {
            context.Response.OnStarting(static state =>
            {
                var response = (HttpResponse)state;
                response.Headers.CacheControl = "no-store, private";
                response.Headers.Pragma = "no-cache";
                response.Headers["Referrer-Policy"] = "no-referrer";
                response.Headers.XContentTypeOptions = "nosniff";
                response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
                return Task.CompletedTask;
            }, context.Response);
        }

        await next(context);
    }
}

public sealed record PublicTrackingResponse(
    [property: JsonPropertyName("public_id")] string PublicId,
    [property: JsonPropertyName("public_status")] string PublicStatus,
    [property: JsonPropertyName("aggregate_version")] long AggregateVersion,
    [property: JsonPropertyName("estimated_window")]
        IReadOnlyDictionary<string, string?>? EstimatedWindow,
    [property: JsonPropertyName("timeline")]
        IReadOnlyList<PublicTrackingTimelineResponse> Timeline);

public sealed record PublicTrackingTimelineResponse(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt);

public sealed record PublicTrackingProblemResponse(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] int Status)
{
    public static PublicTrackingProblemResponse NotFound { get; } =
        new("about:blank", "Not Found", StatusCodes.Status404NotFound);
}
