using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Orders.Application.Csv;
using Orders.Application.Orders;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;

namespace Orders.Endpoints;

/// <summary>
/// CSV-001 upload, preview and commit. Preview resolves no service that can create an order;
/// commit accepts only a file whose digest matches the previewed one and whose every row
/// prevalidates, then delegates each row to the authoritative ORD-001 create path.
/// </summary>
public static class CsvOrderImportEndpoints
{
    private const string PreviewRoute = "/api/v1/orders/csv/preview";
    private const string CommitRoute = "/api/v1/orders/csv/commit";
    private const long MultipartEnvelopeAllowanceBytes = 8 * 1024;

    public static IEndpointRouteBuilder MapCsvOrderImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(PreviewRoute, PreviewAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .DisableAntiforgery()
            .WithName("previewOrderCsv")
            .WithTags("Orders")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<CsvImportPreviewResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapPost(CommitRoute, CommitAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .DisableAntiforgery()
            .WithName("commitOrderCsv")
            .WithTags("Orders")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<CsvImportCommitResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces<CsvImportPreviewResponse>(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> PreviewAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        if (!session.IsActive || session.UserId is null || !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.PreviewOrderCsv) is { } denied)
        {
            return denied;
        }

        var upload = await TryReadUploadAsync(httpContext, cancellationToken);
        if (upload is null)
        {
            return Conflict();
        }

        return Results.Ok(ToPreviewResponse(
            CsvOrderImportPrevalidator.Prevalidate(upload.Content)));
    }

    private static async Task<IResult> CommitAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ICsvOrderImportCommitService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadIdempotencyKey(httpContext.Request, out var idempotencyKey))
        {
            return Conflict();
        }

        if (!session.IsActive || session.UserId is not { } actorId || !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.CommitOrderCsv) is { } denied)
        {
            return denied;
        }

        var upload = await TryReadUploadAsync(httpContext, cancellationToken);
        if (upload?.ContentDigest is null)
        {
            return Conflict();
        }

        var prevalidation = CsvOrderImportPrevalidator.Prevalidate(upload.Content);
        if (!string.Equals(prevalidation.ContentDigest, upload.ContentDigest, StringComparison.Ordinal))
        {
            return Conflict();
        }

        if (!prevalidation.IsCommittable)
        {
            return Results.Json(
                ToPreviewResponse(prevalidation),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        try
        {
            var result = await service.CommitAsync(
                new CsvOrderImportCommitCommand(
                    actorId,
                    tenantContext.OrganizationId,
                    idempotencyKey,
                    prevalidation.ContentDigest,
                    prevalidation.ValidRows,
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Ok(ToCommitResponse(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CsvOrderImportBatchConflictException)
        {
            return Conflict(CsvOrderImportRowFailureCodes.IdempotencyConflict);
        }
        catch (OrderServiceUnavailableException)
        {
            return Unavailable();
        }
    }

    /// <summary>
    /// Reads the multipart upload and, on commit, the echoed digest. Returns null for every
    /// transport-level rejection so the caller answers with the module's uniform conflict.
    /// </summary>
    private static async Task<CsvUpload?> TryReadUploadAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!httpContext.Request.HasFormContentType)
        {
            return null;
        }

        var maximumRequestBytes = CsvOrderImportContract.MaximumFileBytes + MultipartEnvelopeAllowanceBytes;
        if (httpContext.Request.ContentLength is { } contentLength && contentLength > maximumRequestBytes)
        {
            return null;
        }

        var bodySizeFeature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
        {
            bodySizeFeature.MaxRequestBodySize = maximumRequestBytes;
        }

        IFormCollection form;
        try
        {
            form = await httpContext.Request.ReadFormAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or BadHttpRequestException or IOException)
        {
            return null;
        }

        var file = form.Files[CsvOrderImportContract.ColumnFile];
        if (file is null || file.Length <= 0 || file.Length > CsvOrderImportContract.MaximumFileBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream(checked((int)file.Length));
        await using (var stream = file.OpenReadStream())
        {
            await stream.CopyToAsync(buffer, cancellationToken);
        }

        var digests = form["content_digest"];
        if (digests.Count > 1)
        {
            return null;
        }

        return new CsvUpload(buffer.ToArray(), digests.Count == 1 ? digests[0] : null);
    }

    private static bool TryReadIdempotencyKey(HttpRequest request, out string value)
    {
        value = string.Empty;
        var values = request.Headers["Idempotency-Key"];
        if (values.Count != 1 || !IdempotencyKeyPolicy.IsValid(values[0]))
        {
            return false;
        }

        value = values[0]!;
        return true;
    }

    private static CsvImportPreviewResponse ToPreviewResponse(CsvOrderImportPrevalidation prevalidation) => new(
        prevalidation.ContentDigest,
        prevalidation.TotalRows,
        prevalidation.ValidRowCount,
        prevalidation.InvalidRowCount,
        prevalidation.FileErrors,
        prevalidation.Rows
            .Select(row => new CsvImportRowPreviewResponse(
                row.RowNumber,
                row.QuoteId,
                row.PayerType,
                row.Valid,
                row.Errors
                    .Select(error => new CsvImportRowErrorResponse(error.Column, error.Code))
                    .ToArray(),
                row.CodExpectedCents))
            .ToArray());

    private static CsvImportCommitResponse ToCommitResponse(CsvOrderImportCommitResult result) => new(
        result.ContentDigest,
        result.TotalRows,
        result.CreatedRows,
        result.FailedRows,
        result.Rows
            .Select(row => new CsvImportRowOutcomeResponse(
                row.RowNumber,
                row.QuoteId,
                row.Status,
                row.OrderId,
                row.PublicId,
                row.ErrorCode))
            .ToArray());

    /// <summary>
    /// The module's uniform conflict. Every transport-level rejection reports the same opaque
    /// <c>CONFLICT</c>, so a caller cannot probe the upload rules; only a batch key that is already
    /// bound to other content is named, because the caller has to know to pick a new key.
    /// </summary>
    private static IResult Conflict(string code = "CONFLICT") =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict.",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");

    private sealed record CsvUpload(byte[] Content, string? ContentDigest);
}

public sealed record CsvImportRowErrorResponse(
    [property: JsonPropertyName("column")] string Column,
    [property: JsonPropertyName("code")] string Code);

public sealed record CsvImportRowPreviewResponse(
    [property: JsonPropertyName("row_number")] int RowNumber,
    [property: JsonPropertyName("quote_id")] string? QuoteId,
    [property: JsonPropertyName("payer_type")] string? PayerType,
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("errors")] IReadOnlyList<CsvImportRowErrorResponse> Errors,
    [property: JsonPropertyName("cod_expected_cents"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? CodExpectedCents);

public sealed record CsvImportPreviewResponse(
    [property: JsonPropertyName("content_digest")] string ContentDigest,
    [property: JsonPropertyName("total_rows")] int TotalRows,
    [property: JsonPropertyName("valid_rows")] int ValidRows,
    [property: JsonPropertyName("invalid_rows")] int InvalidRows,
    [property: JsonPropertyName("file_errors")] IReadOnlyList<string> FileErrors,
    [property: JsonPropertyName("rows")] IReadOnlyList<CsvImportRowPreviewResponse> Rows);

public sealed record CsvImportRowOutcomeResponse(
    [property: JsonPropertyName("row_number")] int RowNumber,
    [property: JsonPropertyName("quote_id")] string QuoteId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("order_id")] Guid? OrderId,
    [property: JsonPropertyName("public_id")] string? PublicId,
    [property: JsonPropertyName("error_code")] string? ErrorCode);

public sealed record CsvImportCommitResponse(
    [property: JsonPropertyName("content_digest")] string ContentDigest,
    [property: JsonPropertyName("total_rows")] int TotalRows,
    [property: JsonPropertyName("created_rows")] int CreatedRows,
    [property: JsonPropertyName("failed_rows")] int FailedRows,
    [property: JsonPropertyName("rows")] IReadOnlyList<CsvImportRowOutcomeResponse> Rows);
