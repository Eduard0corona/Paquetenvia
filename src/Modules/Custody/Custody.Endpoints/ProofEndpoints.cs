using System.Text.Json;
using System.Text.Json.Serialization;
using Custody.Application.ProofUploads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;

namespace Custody.Endpoints;

public static class ProofEndpoints
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static IEndpointRouteBuilder MapProofEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/orders/{orderId}/proof-upload-sessions",
                CreateUploadSessionAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("createProofUploadSession")
            .WithTags("Custody")
            .Accepts<CreateProofUploadSessionRequest>("application/json")
            .Produces<ProofUploadSessionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapPost("/api/v1/orders/{orderId}/proofs", FinalizeProofAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("finalizeProof")
            .WithTags("Custody")
            .Accepts<FinalizeProofRequest>("application/json")
            .Produces<ProofResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static async Task<IResult> CreateUploadSessionAsync(
        string orderId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IProofUploadSessionService service,
        CancellationToken cancellationToken)
    {
        CreateProofUploadSessionRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<CreateProofUploadSessionRequest>(
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
            !ProofRequestPolicy.IsSupportedProofType(request.ProofType) ||
            request.SizeBytes is not > 0 ||
            (request.Sha256 is not null &&
             !ProofRequestPolicy.TryParseSha256(request.Sha256, out _)) ||
            string.IsNullOrWhiteSpace(request.ContentType))
        {
            return Conflict("INVALID_REQUEST");
        }

        try
        {
            var validRequest = request;
            var result = await service.CreateAsync(
                new CreateProofUploadSessionCommand(
                    actorId,
                    organizationId,
                    session.MfaSatisfied,
                    idempotencyKey,
                    parsedOrderId,
                    validRequest.ProofType!,
                    validRequest.ContentType!,
                    validRequest.SizeBytes!.Value,
                    validRequest.Sha256 is null
                        ? null
                        : Convert.FromHexString(validRequest.Sha256),
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Created(
                $"/api/v1/orders/{parsedOrderId:D}/proof-upload-sessions/{result.Id:D}",
                new ProofUploadSessionResponse(
                    result.Id,
                    result.Status,
                    result.UploadUrl,
                    result.ObjectKey,
                    result.ExpiresAt,
                    result.RequiredHeaders));
        }
        catch (Exception exception)
        {
            return ToProblem(
                exception,
                () => TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.CreateProofUploadSession),
                cancellationToken);
        }
    }

    private static async Task<IResult> FinalizeProofAsync(
        string orderId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IProofFinalizationService service,
        CancellationToken cancellationToken)
    {
        FinalizeProofRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<FinalizeProofRequest>(
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
            request.UploadSessionId is not { } uploadSessionId ||
            uploadSessionId == Guid.Empty ||
            !ProofRequestPolicy.IsSupportedProofType(request.ProofType) ||
            request.CapturedAt is not { } capturedAt ||
            !ProofRequestPolicy.TryParseSha256(request.Sha256, out var sha256))
        {
            return Conflict("INVALID_REQUEST");
        }

        try
        {
            var validRequest = request;
            var result = await service.FinalizeAsync(
                new FinalizeProofCommand(
                    actorId,
                    organizationId,
                    session.MfaSatisfied,
                    idempotencyKey,
                    parsedOrderId,
                    validRequest.UploadSessionId!.Value,
                    validRequest.ProofType!,
                    sha256,
                    validRequest.CapturedAt!.Value,
                    validRequest.Latitude,
                    validRequest.Longitude,
                    validRequest.RecipientName,
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Created(
                $"/api/v1/orders/{parsedOrderId:D}/proofs/{result.Id:D}",
                new ProofResponse(
                    result.Id,
                    result.ProofType,
                    result.Sha256,
                    result.CapturedAt));
        }
        catch (Exception exception)
        {
            return ToProblem(
                exception,
                () => TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.FinalizeProof),
                cancellationToken);
        }
    }

    private static bool TryReadContext(
        string orderId,
        HttpContext context,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        out Guid parsedOrderId,
        out Guid actorId,
        out Guid organizationId,
        out string idempotencyKey)
    {
        parsedOrderId = default;
        actorId = default;
        organizationId = default;
        idempotencyKey = string.Empty;
        var values = context.Request.Headers["Idempotency-Key"];
        return Guid.TryParseExact(orderId, "D", out parsedOrderId) &&
            parsedOrderId != Guid.Empty &&
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
            ProofForbiddenException => refused(),
            ProofNotFoundException => NotFound(),
            ProofConflictException conflict => Conflict(PublicCode(conflict.Code)),
            ProofStorageUnavailableException => Unavailable(),
            _ => Unavailable(),
        };

    private static string PublicCode(string code) => code switch
    {
        "INVALID_REQUEST" => code,
        "IDEMPOTENCY_CONFLICT" => code,
        "ORDER_STATE_NOT_ALLOWED" => code,
        "UPLOAD_SESSION_NOT_READY" => code,
        "PROOF_OBJECT_NOT_READY" => code,
        "PII_PROTECTION_UNAVAILABLE" => code,
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

public sealed record CreateProofUploadSessionRequest(
    [property: JsonPropertyName("proof_type")] string? ProofType,
    [property: JsonPropertyName("content_type")] string? ContentType,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("sha256")] string? Sha256)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ProofUploadSessionResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("upload_url")] string UploadUrl,
    [property: JsonPropertyName("object_key")] string ObjectKey,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("required_headers")] IReadOnlyDictionary<string, string> RequiredHeaders);

public sealed record FinalizeProofRequest(
    [property: JsonPropertyName("upload_session_id")] Guid? UploadSessionId,
    [property: JsonPropertyName("proof_type")] string? ProofType,
    [property: JsonPropertyName("captured_at")] DateTimeOffset? CapturedAt,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("lat")] double? Latitude,
    [property: JsonPropertyName("lng")] double? Longitude,
    [property: JsonPropertyName("recipient_name")] string? RecipientName)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ProofResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("proof_type")] string ProofType,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("captured_at")] DateTimeOffset CapturedAt);
