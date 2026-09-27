using System.Text.Json;
using System.Text.Json.Serialization;
using Finance.Application;
using Finance.Application.Settlements;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Tenancy;

namespace Finance.Endpoints;

/// <summary>
/// The eight normative AI-05 settlement operations: createSettlement, listSettlements
/// (AI05-LIST-SETTLEMENTS), getSettlement, addSettlementAdjustment, approveSettlement, markSettlementPaid,
/// voidSettlement and exportSettlementCsv.
/// </summary>
public static class SettlementEndpoints
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static IEndpointRouteBuilder MapSettlementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/settlements", CreateAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("createSettlement")
            .WithTags("Finance")
            .Accepts<CreateSettlementRequest>("application/json")
            .Produces<SettlementResponse>(StatusCodes.Status201Created)
            .ProducesSettlementProblems();

        endpoints.MapGet("/api/v1/settlements", ListAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("listSettlements")
            .WithTags("Finance")
            .Produces<SettlementPageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/v1/settlements/{settlementId}", GetAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("getSettlement")
            .WithTags("Finance")
            .Produces<SettlementResponse>(StatusCodes.Status200OK)
            .ProducesSettlementProblems();

        endpoints.MapPost("/api/v1/settlements/{settlementId}/adjustments", AdjustAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("addSettlementAdjustment")
            .WithTags("Finance")
            .Accepts<AddSettlementAdjustmentRequest>("application/json")
            .Produces<SettlementResponse>(StatusCodes.Status201Created)
            .ProducesSettlementProblems();

        endpoints.MapPost("/api/v1/settlements/{settlementId}/approve", ApproveAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("approveSettlement")
            .WithTags("Finance")
            .Produces<SettlementResponse>(StatusCodes.Status200OK)
            .ProducesSettlementProblems();

        endpoints.MapPost("/api/v1/settlements/{settlementId}/pay", PayAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("markSettlementPaid")
            .WithTags("Finance")
            .Produces<SettlementResponse>(StatusCodes.Status200OK)
            .ProducesSettlementProblems();

        endpoints.MapPost("/api/v1/settlements/{settlementId}/void", VoidAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("voidSettlement")
            .WithTags("Finance")
            .Accepts<VoidSettlementRequest>("application/json")
            .Produces<SettlementResponse>(StatusCodes.Status200OK)
            .ProducesSettlementProblems();

        endpoints.MapGet("/api/v1/settlements/{settlementId}/export.csv", ExportAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("exportSettlementCsv")
            .WithTags("Finance")
            .Produces(StatusCodes.Status200OK, typeof(string), "text/csv")
            .ProducesSettlementProblems();

        return endpoints;
    }

    private static RouteHandlerBuilder ProducesSettlementProblems(this RouteHandlerBuilder builder) => builder
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> CreateAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ISettlementService service,
        CancellationToken cancellationToken)
    {
        var request = await ReadAsync<CreateSettlementRequest>(httpContext, cancellationToken);
        if (request is not { DriverId: { } driverId, PeriodFrom: { } periodFrom, PeriodTo: { } periodTo } ||
            request.ExtensionData is { Count: > 0 } ||
            !FinanceEndpointBinding.TryReadIdempotencyKey(httpContext.Request, out var key) ||
            !FinanceEndpointBinding.TrySession(session, tenantContext, out var actorId))
        {
            return FinanceEndpointBinding.Conflict(InvalidRequest);
        }

        return await RespondAsync(
            () => service.CreateAsync(
                new(
                    actorId,
                    tenantContext.OrganizationId,
                    key,
                    driverId,
                    periodFrom,
                    periodTo,
                    session.MfaSatisfied,
                    httpContext.TraceIdentifier),
                cancellationToken),
            StatusCodes.Status201Created,
            cancellationToken);
    }

    private static async Task<IResult> GetAsync(
        string settlementId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ISettlementService service,
        CancellationToken cancellationToken)
    {
        if (!FinanceEndpointBinding.TrySession(session, tenantContext, out var actorId))
        {
            return FinanceEndpointBinding.Forbidden();
        }

        if (!FinanceEndpointBinding.TryGuid(settlementId, out var settlement))
        {
            return FinanceEndpointBinding.NotFound();
        }

        return await RespondAsync(
            () => service.GetAsync(
                new(actorId, tenantContext.OrganizationId, settlement, session.MfaSatisfied),
                cancellationToken),
            StatusCodes.Status200OK,
            cancellationToken);
    }

    /// <summary>
    /// AI05-LIST-SETTLEMENTS. Each filter may appear at most once and must parse exactly; anything else is the
    /// uniform INVALID_REQUEST, decided before capability or any persisted settlement is read.
    /// </summary>
    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ISettlementService service,
        CancellationToken cancellationToken)
    {
        if (!FinanceEndpointBinding.TrySession(session, tenantContext, out var actorId))
        {
            return FinanceEndpointBinding.Forbidden();
        }

        if (!TryReadListFilters(httpContext.Request.Query, out var status, out var periodFrom, out var periodTo,
                out var cursor))
        {
            return FinanceEndpointBinding.Conflict(InvalidRequest);
        }

        try
        {
            var page = await service.ListAsync(
                new(actorId, tenantContext.OrganizationId, status, periodFrom, periodTo, cursor, session.MfaSatisfied),
                cancellationToken);
            return Results.Json(
                new SettlementPageResponse(page.Items.Select(ToResponse).ToArray(), page.NextCursor),
                statusCode: StatusCodes.Status200OK);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (Failure(exception) is { } failure) { return failure; }
    }

    internal static readonly string[] ListQueryParameters = ["status", "period_from", "period_to", "cursor"];

    internal static bool TryReadListFilters(
        IQueryCollection query,
        out string? status,
        out DateOnly? periodFrom,
        out DateOnly? periodTo,
        out SettlementCursor? cursor)
    {
        status = null;
        periodFrom = null;
        periodTo = null;
        cursor = null;
        if (query.Keys.Any(key => !ListQueryParameters.Contains(key, StringComparer.Ordinal)) ||
            query.Any(pair => pair.Value.Count != 1))
        {
            return false;
        }

        if (query.TryGetValue("status", out var statusValue))
        {
            // The vocabulary itself is decided by SettlementInputPolicy before any transaction opens.
            status = statusValue[0];
            if (string.IsNullOrEmpty(status))
            {
                return false;
            }
        }

        if (!TryDate(query, "period_from", out periodFrom) || !TryDate(query, "period_to", out periodTo))
        {
            return false;
        }

        if (query.TryGetValue("cursor", out var cursorValue))
        {
            if (!SettlementCursorCodec.TryDecode(cursorValue[0], out cursor))
            {
                return false;
            }
        }

        return periodFrom is not { } from || periodTo is not { } to || from <= to;
    }

    private static bool TryDate(IQueryCollection query, string name, out DateOnly? value)
    {
        value = null;
        if (!query.TryGetValue(name, out var raw))
        {
            return true;
        }

        if (!DateOnly.TryParseExact(
                raw[0], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static async Task<IResult> AdjustAsync(
        HttpContext httpContext,
        string settlementId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ISettlementService service,
        CancellationToken cancellationToken)
    {
        var request = await ReadAsync<AddSettlementAdjustmentRequest>(httpContext, cancellationToken);
        if (request is not { AmountCents: { } amountCents } || request.Reason is null ||
            request.ExtensionData is { Count: > 0 } ||
            !FinanceEndpointBinding.TryGuid(settlementId, out var settlement) ||
            !FinanceEndpointBinding.TryReadIdempotencyKey(httpContext.Request, out var key) ||
            !FinanceEndpointBinding.TrySession(session, tenantContext, out var actorId))
        {
            return FinanceEndpointBinding.Conflict(InvalidRequest);
        }

        return await RespondAsync(
            () => service.AddAdjustmentAsync(
                new(
                    actorId,
                    tenantContext.OrganizationId,
                    key,
                    settlement,
                    amountCents,
                    request.Reason,
                    session.MfaSatisfied,
                    httpContext.TraceIdentifier),
                cancellationToken),
            StatusCodes.Status201Created,
            cancellationToken);
    }

    private static Task<IResult> ApproveAsync(
        HttpContext httpContext,
        string settlementId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ISettlementService service,
        CancellationToken cancellationToken) =>
        TransitionAsync(httpContext, settlementId, session, tenantContext, service.ApproveAsync, cancellationToken);

    private static Task<IResult> PayAsync(
        HttpContext httpContext,
        string settlementId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ISettlementService service,
        CancellationToken cancellationToken) =>
        TransitionAsync(httpContext, settlementId, session, tenantContext, service.MarkPaidAsync, cancellationToken);

    private static async Task<IResult> TransitionAsync(
        HttpContext httpContext,
        string settlementId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        Func<SettlementTransitionCommand, CancellationToken, Task<SettlementResult>> operation,
        CancellationToken cancellationToken)
    {
        if (!FinanceEndpointBinding.TryGuid(settlementId, out var settlement) ||
            !FinanceEndpointBinding.TryReadIdempotencyKey(httpContext.Request, out var key) ||
            !FinanceEndpointBinding.TrySession(session, tenantContext, out var actorId))
        {
            return FinanceEndpointBinding.Conflict(InvalidRequest);
        }

        return await RespondAsync(
            () => operation(
                new(
                    actorId,
                    tenantContext.OrganizationId,
                    key,
                    settlement,
                    session.MfaSatisfied,
                    httpContext.TraceIdentifier),
                cancellationToken),
            StatusCodes.Status200OK,
            cancellationToken);
    }

    private static async Task<IResult> VoidAsync(
        HttpContext httpContext,
        string settlementId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ISettlementService service,
        CancellationToken cancellationToken)
    {
        var request = await ReadAsync<VoidSettlementRequest>(httpContext, cancellationToken);
        if (request?.Reason is null || request.ExtensionData is { Count: > 0 } ||
            !FinanceEndpointBinding.TryGuid(settlementId, out var settlement) ||
            !FinanceEndpointBinding.TryReadIdempotencyKey(httpContext.Request, out var key) ||
            !FinanceEndpointBinding.TrySession(session, tenantContext, out var actorId))
        {
            return FinanceEndpointBinding.Conflict(InvalidRequest);
        }

        return await RespondAsync(
            () => service.VoidAsync(
                new(
                    actorId,
                    tenantContext.OrganizationId,
                    key,
                    settlement,
                    request.Reason,
                    session.MfaSatisfied,
                    httpContext.TraceIdentifier),
                cancellationToken),
            StatusCodes.Status200OK,
            cancellationToken);
    }

    internal const string ExportCacheControl = "no-store";

    private static async Task<IResult> ExportAsync(
        HttpContext httpContext,
        string settlementId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ISettlementService service,
        CancellationToken cancellationToken)
    {
        if (!FinanceEndpointBinding.TrySession(session, tenantContext, out var actorId))
        {
            return FinanceEndpointBinding.Forbidden();
        }

        if (!FinanceEndpointBinding.TryGuid(settlementId, out var settlement))
        {
            return FinanceEndpointBinding.NotFound();
        }

        try
        {
            var document = await service.ExportCsvAsync(
                new(actorId, tenantContext.OrganizationId, settlement, session.MfaSatisfied),
                cancellationToken);

            // AI05-EXPORT-NO-STORE: the export carries payee and money data; no cache may keep a copy.
            httpContext.Response.Headers.CacheControl = ExportCacheControl;
            return Results.File(document.Content, SettlementCsvWriter.ContentType, document.FileName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (Failure(exception) is { } failure) { return failure; }
    }

    private static async Task<IResult> RespondAsync(
        Func<Task<SettlementResult>> operation,
        int successStatus,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Json(ToResponse(await operation()), statusCode: successStatus);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (Failure(exception) is { } failure) { return failure; }
    }

    /// <summary>
    /// The public outcome of a settlement failure. The Finance gateway reports racing writers and database
    /// uniqueness as a FIN-001 concurrency conflict, which settlements publish as the uniform CONFLICT.
    /// </summary>
    private static IResult? Failure(Exception exception) => exception switch
    {
        FinanceForbiddenException => FinanceEndpointBinding.Forbidden(),
        FinanceNotFoundException => FinanceEndpointBinding.NotFound(),
        SettlementConflictException conflict => FinanceEndpointBinding.Conflict(PublicCode(conflict.Code)),
        FinanceConflictException => FinanceEndpointBinding.Conflict(ConflictCode),
        FinanceUnavailableException => FinanceEndpointBinding.Unavailable(),
        _ => null,
    };

    private const string InvalidRequest = "INVALID_REQUEST";
    private const string ConflictCode = "CONFLICT";

    internal static IReadOnlyList<string> PublicCodes { get; } =
    [
        InvalidRequest, ConflictCode, "SETTLEMENT_STATE_CONFLICT", "CASH_PENDING", "INCIDENT_PENDING",
        "CLAIM_PENDING",
    ];

    internal static string PublicCode(SettlementConflictCode code) => code switch
    {
        SettlementConflictCode.InvalidRequest => InvalidRequest,
        SettlementConflictCode.SettlementStateConflict => "SETTLEMENT_STATE_CONFLICT",
        SettlementConflictCode.CashPending => "CASH_PENDING",
        SettlementConflictCode.IncidentPending => "INCIDENT_PENDING",
        SettlementConflictCode.ClaimPending => "CLAIM_PENDING",
        _ => ConflictCode,
    };

    private static async Task<T?> ReadAsync<T>(HttpContext httpContext, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await httpContext.Request.ReadFromJsonAsync<T>(RequestJsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or BadHttpRequestException)
        {
            return null;
        }
    }

    private static SettlementResponse ToResponse(SettlementResult result) => new(
        result.Id,
        result.PayeeType,
        result.PayeeId,
        result.Status,
        result.TotalCents,
        result.PeriodFrom,
        result.PeriodTo,
        result.CreatedAt,
        result.Lines.Select(line => new SettlementLineResponse(
            line.Id,
            line.LineType,
            line.OrderId,
            line.AmountCents,
            line.SourceReference,
            line.CreatedAt)).ToArray());
}

public sealed record CreateSettlementRequest(
    [property: JsonPropertyName("driver_id")] Guid? DriverId,
    [property: JsonPropertyName("period_from")] DateOnly? PeriodFrom,
    [property: JsonPropertyName("period_to")] DateOnly? PeriodTo)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record AddSettlementAdjustmentRequest(
    [property: JsonPropertyName("amount_cents")] long? AmountCents,
    [property: JsonPropertyName("reason")] string? Reason)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record VoidSettlementRequest(
    [property: JsonPropertyName("reason")] string? Reason)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record SettlementLineResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("line_type")] string LineType,
    [property: JsonPropertyName("order_id")] Guid? OrderId,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("source_reference")] string SourceReference,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record SettlementPageResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<SettlementResponse> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record SettlementResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("payee_type")] string PayeeType,
    [property: JsonPropertyName("payee_id")] Guid PayeeId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("total_cents")] long TotalCents,
    [property: JsonPropertyName("period_from")] DateOnly PeriodFrom,
    [property: JsonPropertyName("period_to")] DateOnly PeriodTo,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("lines")] IReadOnlyList<SettlementLineResponse> Lines);
