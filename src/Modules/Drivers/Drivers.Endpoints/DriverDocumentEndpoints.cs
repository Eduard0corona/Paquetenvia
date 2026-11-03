using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Drivers.Application.Documents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;
using static Drivers.Endpoints.VoiceEndpointResults;

namespace Drivers.Endpoints;

/// <summary>
/// DRV-DOC-001-IN-PERSON-2026-11-02 (docs/adr/ADR-DRV-DOC-001-IN-PERSON-DRIVER-DOCUMENTS.md; AI-05
/// <c>x-capability-matrix</c> driver_document_operations): dispatch records, lists and revokes in-person driver
/// documents, and the driver reads its own.
/// <list type="bullet">
/// <item>listDriverDocuments, recordDriverDocument and revokeDriverDocument: DISPATCHER members, and PLATFORM_ADMIN
/// members with a satisfied MFA challenge (403 <c>MFA_REQUIRED</c> when only the second factor is missing); listMyDriverDocuments:
/// DRIVER members only (Y4). Every other role, FINANCE and VIEWER included, is the uniform 403 before anything is read.</item>
/// <item>Shape first, without any state: a query other than one cursor on the list, a missing or malformed
/// Idempotency-Key, a query or a body that is not exactly the published one on the record, and a query or a body on the
/// revoke are 409 INVALID_REQUEST. Then the capability, then the service.</item>
/// <item>An unknown, malformed or foreign driver or document is the uniform 404. JSON only: a document file never
/// travels through the API (AI-08 DRV-DOC-001); no answer or problem carries a number, a file, a key or a hash.</item>
/// </list>
/// Every answer, problems included, is <c>Cache-Control: no-store</c>. Not under SEC-004 (no owner limit names them).
/// </summary>
public static class DriverDocumentEndpoints
{
    public const string ListPath = "/api/v1/drivers/documents";
    public const string RecordPath = "/api/v1/drivers/{driverId}/documents";
    public const string RevokePath = "/api/v1/drivers/{driverId}/documents/{documentId}/revoke";
    public const string MinePath = "/api/v1/driver/me/documents";
    private const long MaximumBodyBytes = 1024;

    private static readonly HashSet<string> RecordFields = new(StringComparer.Ordinal)
    {
        "document_type", "decision", "expiry_basis", "expires_on", "rejection_code",
    };

    public static IEndpointRouteBuilder MapDriverDocumentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(ListPath, ListAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("listDriverDocuments")
            .WithTags("Drivers")
            .Produces<DriverDocumentPageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapPost(RecordPath, RecordAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("recordDriverDocument")
            .WithTags("Drivers")
            .Accepts<RecordDriverDocumentRequest>("application/json")
            .Produces<DriverDocumentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapPost(RevokePath, RevokeAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("revokeDriverDocument")
            .WithTags("Drivers")
            .Produces<DriverDocumentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapGet(MinePath, ListMineAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("listMyDriverDocuments")
            .WithTags("Drivers")
            .Produces<MyDriverDocumentsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IDriverDocumentService service,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext);
        if (!TryReadListQuery(httpContext.Request.Query, out var afterDriverId))
        {
            return Conflict();
        }

        if (Refusal(session, tenantContext, TenantCapabilities.ListDriverDocuments, out var actorId) is { } refused)
        {
            return refused;
        }

        return await ExecuteAsync(async () =>
        {
            var page = await service.ListAsync(
                new ListDriverDocumentsQuery(actorId, tenantContext.OrganizationId, afterDriverId),
                cancellationToken);
            return Results.Ok(DriverDocumentPageResponse.From(page));
        }, cancellationToken);
    }

    private static async Task<IResult> RecordAsync(
        string driverId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IDriverDocumentService service,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext);
        var request = httpContext.Request;
        var keys = request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !IdempotencyKeyPolicy.IsValid(keys[0]) || request.QueryString.HasValue ||
            !request.HasJsonContentType() || request.ContentLength is not (> 0 and <= MaximumBodyBytes))
        {
            return Conflict();
        }

        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
        {
            bodySize.MaxRequestBodySize = MaximumBodyBytes;
        }

        RecordDriverDocumentRequest? shape;
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, default, cancellationToken);
            shape = RecordDriverDocumentRequest.TryRead(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or BadHttpRequestException)
        {
            return Conflict();
        }

        if (shape is null)
        {
            return Conflict();
        }

        if (Refusal(session, tenantContext, TenantCapabilities.RecordDriverDocument, out var actorId) is { } refused)
        {
            return refused;
        }

        if (!TryParseId(driverId, out var parsedDriverId))
        {
            return NotFound();
        }

        return await ExecuteAsync(async () =>
        {
            var recorded = await service.RecordAsync(
                new RecordDriverDocumentCommand(
                    actorId,
                    tenantContext.OrganizationId,
                    parsedDriverId,
                    shape.DocumentType,
                    shape.Decision,
                    shape.ExpiryBasis,
                    shape.ExpiresOn,
                    shape.RejectionCode,
                    keys[0]!,
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Json(DriverDocumentResponse.From(recorded), statusCode: StatusCodes.Status201Created);
        }, cancellationToken);
    }

    private static async Task<IResult> RevokeAsync(
        string driverId,
        string documentId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IDriverDocumentService service,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext);
        var request = httpContext.Request;
        if (request.QueryString.HasValue || request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            return Conflict();
        }

        if (Refusal(session, tenantContext, TenantCapabilities.RevokeDriverDocument, out var actorId) is { } refused)
        {
            return refused;
        }

        if (!TryParseId(driverId, out var parsedDriverId) || !TryParseId(documentId, out var parsedDocumentId))
        {
            return NotFound();
        }

        return await ExecuteAsync(async () =>
        {
            var revoked = await service.RevokeAsync(
                new RevokeDriverDocumentCommand(
                    actorId,
                    tenantContext.OrganizationId,
                    parsedDriverId,
                    parsedDocumentId,
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Ok(DriverDocumentResponse.From(revoked));
        }, cancellationToken);
    }

    private static async Task<IResult> ListMineAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IDriverDocumentService service,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext);
        if (Refusal(session, tenantContext, TenantCapabilities.ListMyDriverDocuments, out var actorId) is { } refused)
        {
            return refused;
        }

        return await ExecuteAsync(async () =>
        {
            var mine = await service.ListMineAsync(actorId, tenantContext.OrganizationId, cancellationToken);
            return Results.Ok(MyDriverDocumentsResponse.From(mine));
        }, cancellationToken);
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DriverDocumentForbiddenException)
        {
            return Forbidden();
        }
        catch (DriverDocumentNotFoundException)
        {
            return NotFound();
        }
        catch (DriverDocumentConflictException conflict)
        {
            return Conflict(conflict.Code);
        }
        catch (DriverDocumentUnavailableException)
        {
            return Unavailable();
        }
    }

    private static IResult? Refusal(
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        TenantCapability capability,
        out Guid actorId)
    {
        if (!TrySession(session, tenantContext, out actorId))
        {
            return Forbidden();
        }

        return TenantCapabilityGate.Deny(session, tenantContext, capability);
    }

    private static void NoStore(HttpContext httpContext) => httpContext.Response.Headers.CacheControl = "no-store";

    /// <summary>A hyphenated UUID that is not empty; anything else is the uniform 404.</summary>
    private static bool TryParseId(string? value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;

    /// <summary>Only <c>cursor</c>, at most once, and only a value this operation issued.</summary>
    public static bool TryReadListQuery(IQueryCollection query, out Guid? afterDriverId)
    {
        afterDriverId = null;
        if (query.Keys.Any(key => !string.Equals(key, "cursor", StringComparison.Ordinal)) ||
            query.Any(pair => pair.Value.Count != 1))
        {
            return false;
        }

        if (!query.TryGetValue("cursor", out var value))
        {
            return true;
        }

        if (!DriverDocumentCursorCodec.TryDecode(value[0], out var after))
        {
            return false;
        }

        afterDriverId = after;
        return true;
    }

    /// <summary>
    /// The recordDriverDocument body (AI-05 RecordDriverDocumentRequest): only the published members, each a string
    /// at most once, and exactly one of the two shapes - VALID with expiry_basis (PRINTED_EXPIRY with expires_on as
    /// yyyy-MM-dd, or PERMANENT_LICENSE_REVIEW for DRIVER_LICENSE without a date) and no rejection_code, or REJECTED with
    /// a rejection_code and no expiry. Anything else is null (409 INVALID_REQUEST); nothing is echoed.
    /// </summary>
    public sealed record RecordDriverDocumentRequest(
        string DocumentType,
        string Decision,
        string? ExpiryBasis,
        DateOnly? ExpiresOn,
        string? RejectionCode)
    {
        public static RecordDriverDocumentRequest? TryRead(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!RecordFields.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.String ||
                    !values.TryAdd(property.Name, property.Value.GetString()!))
                {
                    return null;
                }
            }

            if (!values.TryGetValue("document_type", out var type) ||
                !DriverDocumentVocabulary.RecordableTypes.Contains(type) ||
                !values.TryGetValue("decision", out var decision))
            {
                return null;
            }

            values.TryGetValue("expiry_basis", out var basis);
            values.TryGetValue("rejection_code", out var rejection);
            DateOnly? expiresOn = null;
            if (values.TryGetValue("expires_on", out var printed))
            {
                if (!DateOnly.TryParseExact(printed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return null;
                }

                expiresOn = date;
            }

            var valid = decision switch
            {
                DriverDocumentVocabulary.Valid =>
                    rejection is null &&
                    (basis == DriverDocumentVocabulary.PrintedExpiry
                        ? expiresOn is not null
                        : basis == DriverDocumentVocabulary.PermanentLicenseReview &&
                          expiresOn is null &&
                          type == DriverDocumentVocabulary.DriverLicense),
                DriverDocumentVocabulary.Rejected =>
                    basis is null && expiresOn is null &&
                    rejection is not null && DriverDocumentVocabulary.RejectionCodes.Contains(rejection),
                _ => false,
            };
            return valid ? new RecordDriverDocumentRequest(type, decision, basis, expiresOn, rejection) : null;
        }
    }
}

public sealed record DriverDocumentResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("document_type")] string DocumentType,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("rejection_code")] string? RejectionCode,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("valid_through")] DateOnly? ValidThrough,
    [property: JsonPropertyName("expiry_basis")] string? ExpiryBasis,
    [property: JsonPropertyName("recorded_at")] DateTimeOffset RecordedAt,
    [property: JsonPropertyName("revoked_at")] DateTimeOffset? RevokedAt)
{
    public static DriverDocumentResponse From(DriverDocumentResult value) => new(
        value.Id,
        value.DocumentType,
        value.Status,
        value.RejectionCode,
        value.ExpiresAt,
        value.ValidThrough,
        value.ExpiryBasis,
        value.RecordedAt,
        value.RevokedAt);
}

public sealed record DriverDocumentSlotResponse(
    [property: JsonPropertyName("document_type")] string DocumentType,
    [property: JsonPropertyName("document")] DriverDocumentResponse? Document)
{
    public static DriverDocumentSlotResponse From(DriverDocumentSlotResult value) =>
        new(value.DocumentType, value.Document is null ? null : DriverDocumentResponse.From(value.Document));
}

public sealed record DriverDocumentSetResponse(
    [property: JsonPropertyName("driver_id")] Guid DriverId,
    [property: JsonPropertyName("documents")] IReadOnlyList<DriverDocumentSlotResponse> Documents)
{
    public static DriverDocumentSetResponse From(DriverDocumentSetResult value) =>
        new(value.DriverId, value.Documents.Select(DriverDocumentSlotResponse.From).ToArray());
}

public sealed record DriverDocumentPageResponse(
    [property: JsonPropertyName("documents_enabled")] bool DocumentsEnabled,
    [property: JsonPropertyName("items")] IReadOnlyList<DriverDocumentSetResponse> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor)
{
    public static DriverDocumentPageResponse From(DriverDocumentPageResult value) => new(
        value.DocumentsEnabled,
        value.Items.Select(DriverDocumentSetResponse.From).ToArray(),
        value.NextAfterDriverId is { } after ? DriverDocumentCursorCodec.Encode(after) : null);
}

public sealed record MyDriverDocumentsResponse(
    [property: JsonPropertyName("documents_enabled")] bool DocumentsEnabled,
    [property: JsonPropertyName("documents")] IReadOnlyList<DriverDocumentSlotResponse> Documents)
{
    public static MyDriverDocumentsResponse From(MyDriverDocumentsResult value) =>
        new(value.DocumentsEnabled, value.Documents.Select(DriverDocumentSlotResponse.From).ToArray());
}
