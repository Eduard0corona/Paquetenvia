using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Drivers.Application.Locations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Tenancy;

namespace Drivers.Endpoints;

public static class DriverLocationEndpoints
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static IEndpointRouteBuilder MapDriverLocationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/driver/me/location-updates", PublishAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(
                StatusCodes.Status403Forbidden,
                StatusCodes.Status401Unauthorized)
            .RequireRateLimiting(DriverLocationEndpointOptions.RateLimitPolicy)
            .WithName("publishDriverLocation")
            .WithTags("Driver")
            .Accepts<DriverLocationBatchRequest>("application/json")
            .Produces<DriverLocationBatchResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static async Task<IResult> PublishAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IDriverLocationIngestionService service,
        IOptions<DriverLocationEndpointOptions> endpointOptions,
        CancellationToken cancellationToken)
    {
        if (httpContext.Request.ContentLength is { } contentLength &&
            contentLength > endpointOptions.Value.MaximumRequestBodyBytes)
        {
            return Conflict();
        }
        var bodySizeFeature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
        {
            bodySizeFeature.MaxRequestBodySize = endpointOptions.Value.MaximumRequestBodyBytes;
        }

        DriverLocationBatchRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<DriverLocationBatchRequest>(
                RequestJsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return Conflict();
        }
        catch (BadHttpRequestException)
        {
            return Conflict();
        }

        if (request?.Positions is not { Count: >= 1 and <= 20 } ||
            request.ExtensionData is { Count: > 0 } ||
            request.Positions.Any(position =>
                position is null || position.ExtensionData is { Count: > 0 }))
        {
            return Conflict();
        }

        if (!session.IsActive ||
            session.UserId is not { } actorId ||
            !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        var positions = request.Positions.Select(value => ToInput(value!)).ToArray();
        try
        {
            var result = await service.PublishAsync(
                new PublishDriverLocationBatchCommand(
                    actorId,
                    tenantContext.OrganizationId,
                    positions),
                cancellationToken);
            return Results.Accepted(value: ToResponse(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DriverLocationForbiddenException)
        {
            return Forbidden();
        }
        catch (DriverLocationNotFoundException)
        {
            return NotFound();
        }
        catch (DriverLocationProviderUnavailableException)
        {
            return Unavailable();
        }
        catch (DriverLocationInfrastructureException)
        {
            return Unavailable();
        }
    }

    private static DriverLocationPointInput ToInput(DriverLocationPointRequest request)
    {
        var hasClientEventId = Guid.TryParseExact(
            request.ClientEventId,
            "D",
            out var clientEventId);
        var hasCapturedAt = DateTimeOffset.TryParseExact(
            request.CapturedAt,
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var capturedAt);
        return new(
            hasClientEventId ? clientEventId : null,
            request.Latitude,
            request.Longitude,
            request.AccuracyMeters,
            hasCapturedAt ? capturedAt : null,
            request.HeadingDegrees,
            request.SpeedMetersPerSecond);
    }

    private static DriverLocationBatchResponse ToResponse(DriverLocationBatchResult result) => new(
        result.Items.Select(item => new DriverLocationItemResponse(
            item.ClientEventId ?? Guid.Empty,
            item.PositionId,
            item.Status switch
            {
                DriverLocationItemStatus.Accepted => "ACCEPTED",
                DriverLocationItemStatus.Duplicate => "DUPLICATE",
                _ => "REJECTED",
            },
            item.Duplicate,
            item.ErrorCode)).ToArray(),
        result.AcceptedCount,
        result.DuplicateCount,
        result.RejectedCount);

    private static IResult Conflict() => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Conflict.",
        extensions: new Dictionary<string, object?> { ["code"] = "INVALID_REQUEST" });

    private static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
}

public sealed record DriverLocationBatchRequest(
    [property: JsonPropertyName("positions")] IReadOnlyList<DriverLocationPointRequest?>? Positions)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record DriverLocationPointRequest(
    [property: JsonPropertyName("client_event_id")] string? ClientEventId,
    [property: JsonPropertyName("lat")] double? Latitude,
    [property: JsonPropertyName("lng")] double? Longitude,
    [property: JsonPropertyName("accuracy_m")] double? AccuracyMeters,
    [property: JsonPropertyName("captured_at")] string? CapturedAt,
    [property: JsonPropertyName("heading_degrees")] double? HeadingDegrees,
    [property: JsonPropertyName("speed_mps")] double? SpeedMetersPerSecond)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record DriverLocationItemResponse(
    [property: JsonPropertyName("client_event_id")] Guid ClientEventId,
    [property: JsonPropertyName("position_id")] Guid? PositionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("duplicate")] bool Duplicate,
    [property: JsonPropertyName("error_code")] string? ErrorCode);

public sealed record DriverLocationBatchResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<DriverLocationItemResponse> Items,
    [property: JsonPropertyName("accepted_count")] int AcceptedCount,
    [property: JsonPropertyName("duplicate_count")] int DuplicateCount,
    [property: JsonPropertyName("rejected_count")] int RejectedCount);
