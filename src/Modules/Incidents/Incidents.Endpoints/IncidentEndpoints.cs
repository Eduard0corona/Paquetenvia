using System.Text.Json;
using System.Text.Json.Serialization;
using Incidents.Application.Incidents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;

namespace Incidents.Endpoints;

public static class IncidentEndpoints
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/orders/{orderId}/incidents", OpenIncidentAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("openIncident")
            .WithTags("Incidents")
            .Accepts<OpenIncidentRequest>("application/json")
            .Produces<IncidentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapPost("/api/v1/incidents/{incidentId}/resolution", ResolveIncidentAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("resolveIncident")
            .WithTags("Incidents")
            .Accepts<ResolveIncidentRequest>("application/json")
            .Produces<IncidentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapGet("/api/v1/incidents", ListIncidentsAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("listIncidents")
            .WithTags("Incidents")
            .Produces<IncidentPageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapGet("/api/v1/incidents/{incidentId}", GetIncidentAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("getIncident")
            .WithTags("Incidents")
            .Produces<IncidentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static async Task<IResult> OpenIncidentAsync(
        string orderId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IIncidentService service,
        IncidentOccurrenceAgePolicy occurrencePolicy,
        IClock clock,
        CancellationToken cancellationToken)
    {
        OpenIncidentRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<OpenIncidentRequest>(
                RequestJsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return Conflict("INVALID_REQUEST");
        }

        if (!TryReadContext(
                orderId,
                httpContext,
                session,
                tenantContext,
                out var parsedOrderId,
                out var actorId,
                out var organizationId,
                out var idempotencyKey) ||
            request is null ||
            request.ExtensionData is { Count: > 0 } ||
            request.OccurredAt is not { } occurredAt ||
            occurredAt == default ||
            request.EvidenceProofIds is not { } evidenceProofIds)
        {
            return Conflict("INVALID_REQUEST");
        }

        var command = new OpenIncidentCommand(
            actorId,
            organizationId,
            session.MfaSatisfied,
            idempotencyKey,
            parsedOrderId,
            request.Type ?? string.Empty,
            request.Severity ?? string.Empty,
            request.Description ?? string.Empty,
            request.ReasonCode ?? string.Empty,
            request.NextAction ?? string.Empty,
            occurredAt,
            evidenceProofIds,
            httpContext.TraceIdentifier);
        if (!IncidentRequestPolicy.IsValidCommandShape(command))
        {
            return Conflict("INVALID_REQUEST");
        }

        // OPS-003-INCIDENT-72H-UNIFICATION-CONFIGURABLE-2026-09-27: a report older than the configured
        // maximum occurrence age never reaches the incident service, whatever its idempotency key; an
        // occurred_at beyond the configured clock tolerance stays an invalid request.
        switch (occurrencePolicy.Evaluate(occurredAt, clock.UtcNow))
        {
            case OfflineOperationAge.Expired:
                return Conflict(OfflineOperationAgePolicy.ExpiredCode);
            case OfflineOperationAge.AheadOfServerClock:
                return Conflict("INVALID_REQUEST");
        }

        try
        {
            var result = await service.OpenAsync(command, cancellationToken);
            return Results.Created(
                $"/api/v1/orders/{parsedOrderId:D}/incidents/{result.Id:D}",
                ToResponse(result));
        }
        catch (Exception exception)
        {
            return ToProblem(
                exception,
                () => TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.OpenIncident),
                cancellationToken);
        }
    }

    private static async Task<IResult> ResolveIncidentAsync(
        string incidentId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IIncidentService service,
        CancellationToken cancellationToken)
    {
        ResolveIncidentRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<ResolveIncidentRequest>(
                RequestJsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return Conflict("INVALID_REQUEST");
        }

        if (!TryReadContext(
                incidentId,
                httpContext,
                session,
                tenantContext,
                out var parsedIncidentId,
                out var actorId,
                out var organizationId,
                out var idempotencyKey) ||
            request is null ||
            request.ExtensionData is { Count: > 0 })
        {
            return Conflict("INVALID_REQUEST");
        }

        try
        {
            var result = await service.ResolveAsync(
                new ResolveIncidentCommand(
                    actorId,
                    organizationId,
                    session.MfaSatisfied,
                    idempotencyKey,
                    parsedIncidentId,
                    request.Outcome ?? string.Empty,
                    request.Reason ?? string.Empty,
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Ok(ToResponse(result));
        }
        catch (Exception exception)
        {
            return ToProblem(
                exception,
                () => TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.ResolveIncident),
                cancellationToken);
        }
    }

    /// <summary>
    /// API-INC-LIST-PROOFS-2026-09-29. Every query parameter appears at most once and parses exactly;
    /// anything else is INVALID_REQUEST, decided before capability or any incident is read. Capability
    /// is then settled from the session and again inside the tenant transaction.
    /// </summary>
    private static async Task<IResult> ListIncidentsAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IIncidentReadService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadReadContext(session, tenantContext, out var actorId, out var organizationId))
        {
            return Forbidden();
        }

        if (!TryReadListFilters(httpContext.Request.Query, out var status, out var orderId, out var cursor))
        {
            return Conflict("INVALID_REQUEST");
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.ListIncidents) is { } denied)
        {
            return denied;
        }

        try
        {
            var page = await service.ListAsync(
                new ListIncidentsQuery(actorId, organizationId, session.MfaSatisfied, status, orderId, cursor),
                cancellationToken);
            return Results.Ok(new IncidentPageResponse(page.Items.Select(ToResponse).ToArray(), page.NextCursor));
        }
        catch (Exception exception)
        {
            return ToProblem(
                exception,
                () => TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.ListIncidents),
                cancellationToken);
        }
    }

    /// <summary>
    /// API-INC-LIST-PROOFS-2026-09-29. Capability first; then a malformed, missing or foreign incident
    /// is the same uniform 404.
    /// </summary>
    private static async Task<IResult> GetIncidentAsync(
        string incidentId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IIncidentReadService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadReadContext(session, tenantContext, out var actorId, out var organizationId))
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.GetIncident) is { } denied)
        {
            return denied;
        }

        if (!Guid.TryParseExact(incidentId, "D", out var parsedIncidentId) || parsedIncidentId == Guid.Empty)
        {
            return NotFound();
        }

        try
        {
            var result = await service.GetAsync(
                new GetIncidentQuery(actorId, organizationId, session.MfaSatisfied, parsedIncidentId),
                cancellationToken);
            return Results.Ok(ToResponse(result));
        }
        catch (Exception exception)
        {
            return ToProblem(
                exception,
                () => TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.GetIncident),
                cancellationToken);
        }
    }

    public static IReadOnlyList<string> ListQueryParameters { get; } = ["status", "order_id", "cursor"];

    internal static bool TryReadListFilters(
        IQueryCollection query,
        out string? status,
        out Guid? orderId,
        out IncidentCursor? cursor)
    {
        status = null;
        orderId = null;
        cursor = null;
        if (query.Keys.Any(key => !ListQueryParameters.Contains(key, StringComparer.Ordinal)) ||
            query.Any(pair => pair.Value.Count != 1))
        {
            return false;
        }

        if (query.TryGetValue("status", out var statusValue))
        {
            status = statusValue[0];
            if (string.IsNullOrEmpty(status) || !IncidentReadPolicy.IsValidStatusFilter(status))
            {
                return false;
            }
        }

        if (query.TryGetValue("order_id", out var orderValue))
        {
            if (!Guid.TryParseExact(orderValue[0], "D", out var parsedOrder) || parsedOrder == Guid.Empty)
            {
                return false;
            }

            orderId = parsedOrder;
        }

        return !query.TryGetValue("cursor", out var cursorValue) ||
            IncidentCursorCodec.TryDecode(cursorValue[0], out cursor);
    }

    private static bool TryReadReadContext(
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        out Guid actorId,
        out Guid organizationId)
    {
        actorId = default;
        organizationId = default;
        return session.IsActive &&
            session.UserId is { } userId &&
            (actorId = userId) != Guid.Empty &&
            tenantContext.IsSelected &&
            (organizationId = tenantContext.OrganizationId) != Guid.Empty;
    }

    private static IncidentResponse ToResponse(IncidentResult result) =>
        new(
            result.Id,
            result.OrderId,
            result.Status,
            result.Severity,
            result.ReasonCode,
            result.NextAction,
            result.CustodyAcquired,
            result.OccurredAt,
            result.SlaDueAt,
            result.EvidenceProofIds);

    private static bool TryReadContext(
        string resourceId,
        HttpContext context,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        out Guid parsedResourceId,
        out Guid actorId,
        out Guid organizationId,
        out string idempotencyKey)
    {
        parsedResourceId = default;
        actorId = default;
        organizationId = default;
        idempotencyKey = string.Empty;
        var values = context.Request.Headers["Idempotency-Key"];
        return Guid.TryParseExact(resourceId, "D", out parsedResourceId) &&
            parsedResourceId != Guid.Empty &&
            session.IsActive &&
            session.UserId is { } userId &&
            (actorId = userId) != Guid.Empty &&
            tenantContext.IsSelected &&
            (organizationId = tenantContext.OrganizationId) != Guid.Empty &&
            values.Count == 1 &&
            IdempotencyKeyPolicy.IsValid(values[0]) &&
            (idempotencyKey = values[0]!).Length > 0;
    }

    private static IResult ToProblem(
        Exception exception,
        Func<IResult> refused,
        CancellationToken cancellationToken) =>
        exception switch
        {
            OperationCanceledException when cancellationToken.IsCancellationRequested => throw exception,
            IncidentForbiddenException => refused(),
            IncidentNotFoundException => NotFound(),
            IncidentConflictException conflict => Conflict(PublicCode(conflict.Code)),
            _ => Unavailable(),
        };

    private static string PublicCode(string code) => code switch
    {
        "INVALID_REQUEST" => code,
        "IDEMPOTENCY_CONFLICT" => code,
        "ORDER_STATE_NOT_ALLOWED" => code,
        "EVIDENCE_NOT_AVAILABLE" => code,
        "INCIDENT_STATE_CONFLICT" => code,
        _ => "CONFLICT",
    };

    private static IResult Conflict(string code) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict.",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
}

public sealed record OpenIncidentRequest(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("reason_code")] string? ReasonCode,
    [property: JsonPropertyName("next_action")] string? NextAction,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset? OccurredAt,
    [property: JsonPropertyName("evidence_proof_ids")] IReadOnlyList<Guid>? EvidenceProofIds)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ResolveIncidentRequest(
    [property: JsonPropertyName("outcome")] string? Outcome,
    [property: JsonPropertyName("reason")] string? Reason)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record IncidentPageResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<IncidentResponse> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record IncidentResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("reason_code")] string ReasonCode,
    [property: JsonPropertyName("next_action")] string NextAction,
    [property: JsonPropertyName("custody_acquired")] bool CustodyAcquired,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("sla_due_at")] DateTimeOffset SlaDueAt,
    [property: JsonPropertyName("evidence_proof_ids")] IReadOnlyList<Guid> EvidenceProofIds);
