using System.Text.Json;
using System.Text.Json.Serialization;
using Finance.Application;
using Finance.Application.Cod;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;

namespace Finance.Endpoints;

/// <summary>The two normative AI-05 Finance operations: recordCodCollection and reconcileCod.</summary>
public static class CodEndpoints
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static IEndpointRouteBuilder MapCodEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/orders/{orderId}/cod-records", RecordAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("recordCodCollection")
            .WithTags("Finance")
            .Accepts<RecordCodRequest>("application/json")
            .Produces<CodTransactionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapPost("/api/v1/cod-records/{codId}/reconcile", ReconcileAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("reconcileCod")
            .WithTags("Finance")
            .Produces<CodTransactionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> RecordAsync(
        HttpContext httpContext,
        string orderId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ICodTransactionService service,
        CancellationToken cancellationToken)
    {
        var request = await ReadAsync<RecordCodRequest>(httpContext, cancellationToken);
        if (request is null || !FinanceEndpointBinding.TryGuid(orderId, out var order) ||
            request.AmountCents is not { } amountCents || request.ExtensionData is { Count: > 0 } ||
            !FinanceEndpointBinding.TryReadIdempotencyKey(httpContext.Request, out var key) ||
            !FinanceEndpointBinding.TrySession(session, tenantContext, out var actorId))
        {
            return FinanceEndpointBinding.Conflict("INVALID_REQUEST");
        }

        try
        {
            var result = await service.RecordAsync(
                new(
                    actorId,
                    tenantContext.OrganizationId,
                    key,
                    order,
                    amountCents,
                    request.Reference,
                    session.MfaSatisfied,
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Json(ToResponse(result), statusCode: StatusCodes.Status201Created);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FinanceForbiddenException) { return FinanceEndpointBinding.Forbidden(); }
        catch (FinanceNotFoundException) { return FinanceEndpointBinding.NotFound(); }
        catch (FinanceConflictException exception)
        {
            return FinanceEndpointBinding.Conflict(FinanceEndpointBinding.PublicCode(exception.Code));
        }
        catch (FinanceUnavailableException) { return FinanceEndpointBinding.Unavailable(); }
    }

    private static async Task<IResult> ReconcileAsync(
        HttpContext httpContext,
        string codId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ICodTransactionService service,
        CancellationToken cancellationToken)
    {
        if (!FinanceEndpointBinding.TryGuid(codId, out var cod) ||
            !FinanceEndpointBinding.TryReadIdempotencyKey(httpContext.Request, out var key) ||
            !FinanceEndpointBinding.TrySession(session, tenantContext, out var actorId))
        {
            return FinanceEndpointBinding.Conflict("INVALID_REQUEST");
        }

        try
        {
            var result = await service.ReconcileAsync(
                new(
                    actorId,
                    tenantContext.OrganizationId,
                    key,
                    cod,
                    session.MfaSatisfied,
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Ok(ToResponse(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FinanceForbiddenException) { return FinanceEndpointBinding.Forbidden(); }
        catch (FinanceNotFoundException) { return FinanceEndpointBinding.NotFound(); }
        catch (FinanceConflictException exception)
        {
            return FinanceEndpointBinding.Conflict(FinanceEndpointBinding.PublicCode(exception.Code));
        }
        catch (FinanceUnavailableException) { return FinanceEndpointBinding.Unavailable(); }
    }

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

    private static CodTransactionResponse ToResponse(CodTransactionResult result) => new(
        result.Id,
        result.OrderId,
        result.AmountCents,
        result.Status,
        result.RecordedAt,
        result.ReconciledAt);
}

public sealed record RecordCodRequest(
    [property: JsonPropertyName("amount_cents")] long? AmountCents,
    [property: JsonPropertyName("reference")] string? Reference)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record CodTransactionResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("recorded_at")] DateTimeOffset? RecordedAt,
    [property: JsonPropertyName("reconciled_at")] DateTimeOffset? ReconciledAt);
