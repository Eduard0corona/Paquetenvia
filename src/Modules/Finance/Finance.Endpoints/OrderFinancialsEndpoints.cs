using System.Text.Json.Serialization;
using Finance.Application;
using Finance.Application.Financials;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Tenancy;

namespace Finance.Endpoints;

/// <summary>
/// The read-only FIN-001 financial surface published in AI-05 as getOrderFinancials and
/// getRouteFinancials: cost by modality, margin by order and by route, and the COD position, tenant scoped
/// and derived on read. A malformed identifier cannot name a visible resource, so it is the same uniform
/// 404 as a missing or foreign one.
/// </summary>
public static class OrderFinancialsEndpoints
{
    public static IEndpointRouteBuilder MapFinancialsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/orders/{orderId}/financials", GetOrderAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("getOrderFinancials")
            .WithTags("Finance")
            .Produces<OrderFinancialsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/v1/routes/{routeId}/financials", GetRouteAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("getRouteFinancials")
            .WithTags("Finance")
            .Produces<RouteFinancialsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> GetOrderAsync(
        string orderId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IOrderFinancialsService service,
        CancellationToken cancellationToken)
    {
        if (!FinanceEndpointBinding.TrySession(session, tenantContext, out var actorId))
        {
            return FinanceEndpointBinding.Forbidden();
        }

        if (!FinanceEndpointBinding.TryGuid(orderId, out var order))
        {
            return FinanceEndpointBinding.NotFound();
        }

        try
        {
            var result = await service.GetOrderFinancialsAsync(
                new(actorId, tenantContext.OrganizationId, order, session.MfaSatisfied),
                cancellationToken);
            return Results.Ok(ToResponse(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FinanceForbiddenException) { return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.GetOrderFinancials); }
        catch (FinanceNotFoundException) { return FinanceEndpointBinding.NotFound(); }
        catch (FinanceConflictException exception)
        {
            return FinanceEndpointBinding.Conflict(FinanceEndpointBinding.PublicCode(exception.Code));
        }
        catch (FinanceUnavailableException) { return FinanceEndpointBinding.Unavailable(); }
    }

    private static async Task<IResult> GetRouteAsync(
        string routeId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IOrderFinancialsService service,
        CancellationToken cancellationToken)
    {
        if (!FinanceEndpointBinding.TrySession(session, tenantContext, out var actorId))
        {
            return FinanceEndpointBinding.Forbidden();
        }

        if (!FinanceEndpointBinding.TryGuid(routeId, out var route))
        {
            return FinanceEndpointBinding.NotFound();
        }

        try
        {
            var result = await service.GetRouteFinancialsAsync(
                new(actorId, tenantContext.OrganizationId, route, session.MfaSatisfied),
                cancellationToken);
            return Results.Ok(ToResponse(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FinanceForbiddenException) { return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.GetRouteFinancials); }
        catch (FinanceNotFoundException) { return FinanceEndpointBinding.NotFound(); }
        catch (FinanceConflictException exception)
        {
            return FinanceEndpointBinding.Conflict(FinanceEndpointBinding.PublicCode(exception.Code));
        }
        catch (FinanceUnavailableException) { return FinanceEndpointBinding.Unavailable(); }
    }

    private static OrderFinancialsResponse ToResponse(OrderFinancialsResult result) => new(
        result.OrderId,
        result.OrderStatus,
        result.Currency,
        result.RevenueCents,
        result.CostCents,
        result.MarginCents,
        result.MarginBasisPoints,
        result.CostByModality.Select(ToResponse).ToArray(),
        ToResponse(result.Cod),
        result.CodRecord is { } record
            ? new CodTransactionResponse(
                record.Id,
                record.OrderId,
                record.AmountCents,
                record.Status,
                record.RecordedAt,
                record.ReconciledAt)
            : null);

    private static RouteFinancialsResponse ToResponse(RouteFinancialsResult result) => new(
        result.RouteId,
        result.RouteStatus,
        result.Currency,
        result.OrderCount,
        result.RevenueCentsTotal,
        result.CostCentsTotal,
        result.MarginCentsTotal,
        result.MarginBasisPoints,
        result.CostByModality.Select(ToResponse).ToArray(),
        result.CodExpectedCentsTotal,
        result.CodPendingReconciliationCentsTotal,
        result.CodPendingReconciliationCount,
        result.Orders
            .Select(order => new RouteOrderFinancialsResponse(
                order.OrderId,
                order.RevenueCents,
                order.CostCents,
                order.MarginCents,
                order.MarginBasisPoints,
                order.CostByModality.Select(ToResponse).ToArray(),
                ToResponse(order.Cod)))
            .ToArray());

    private static ModalityCostResponse ToResponse(ModalityCostResult cost) => new(
        cost.Modality,
        cost.CostCents,
        cost.AssignmentCount);

    private static CodPositionResponse ToResponse(CodPositionResult cod) => new(
        cod.ExpectedCents,
        cod.Status,
        cod.AmountCents,
        cod.Recorded,
        cod.Reconciled,
        cod.SatisfiesDeliveryRequirement,
        cod.SatisfiesCloseRequirement);
}

public sealed record ModalityCostResponse(
    [property: JsonPropertyName("modality")] string Modality,
    [property: JsonPropertyName("cost_cents")] long CostCents,
    [property: JsonPropertyName("assignment_count")] int AssignmentCount);

/// <remarks>
/// AI-05 money is never a null integer, so <c>amount_cents</c> is omitted, not null, until a collection
/// is recorded.
/// </remarks>
public sealed record CodPositionResponse(
    [property: JsonPropertyName("expected_cents")] long ExpectedCents,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("amount_cents"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? AmountCents,
    [property: JsonPropertyName("recorded")] bool Recorded,
    [property: JsonPropertyName("reconciled")] bool Reconciled,
    [property: JsonPropertyName("satisfies_delivery_requirement")] bool SatisfiesDeliveryRequirement,
    [property: JsonPropertyName("satisfies_close_requirement")] bool SatisfiesCloseRequirement);

public sealed record OrderFinancialsResponse(
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("order_status")] string OrderStatus,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("revenue_cents")] long RevenueCents,
    [property: JsonPropertyName("cost_cents")] long CostCents,
    [property: JsonPropertyName("margin_cents")] long MarginCents,
    [property: JsonPropertyName("margin_basis_points")] long? MarginBasisPoints,
    [property: JsonPropertyName("cost_by_modality")] IReadOnlyList<ModalityCostResponse> CostByModality,
    [property: JsonPropertyName("cod")] CodPositionResponse Cod,
    [property: JsonPropertyName("cod_record"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    CodTransactionResponse? CodRecord);

public sealed record RouteOrderFinancialsResponse(
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("revenue_cents")] long RevenueCents,
    [property: JsonPropertyName("cost_cents")] long CostCents,
    [property: JsonPropertyName("margin_cents")] long MarginCents,
    [property: JsonPropertyName("margin_basis_points")] long? MarginBasisPoints,
    [property: JsonPropertyName("cost_by_modality")] IReadOnlyList<ModalityCostResponse> CostByModality,
    [property: JsonPropertyName("cod")] CodPositionResponse Cod);

public sealed record RouteFinancialsResponse(
    [property: JsonPropertyName("route_id")] Guid RouteId,
    [property: JsonPropertyName("route_status")] string RouteStatus,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("order_count")] int OrderCount,
    [property: JsonPropertyName("revenue_cents_total")] long RevenueCentsTotal,
    [property: JsonPropertyName("cost_cents_total")] long CostCentsTotal,
    [property: JsonPropertyName("margin_cents_total")] long MarginCentsTotal,
    [property: JsonPropertyName("margin_basis_points")] long? MarginBasisPoints,
    [property: JsonPropertyName("cost_by_modality")] IReadOnlyList<ModalityCostResponse> CostByModality,
    [property: JsonPropertyName("cod_expected_cents_total")] long CodExpectedCentsTotal,
    [property: JsonPropertyName("cod_pending_reconciliation_cents_total")] long CodPendingReconciliationCentsTotal,
    [property: JsonPropertyName("cod_pending_reconciliation_count")] int CodPendingReconciliationCount,
    [property: JsonPropertyName("orders")] IReadOnlyList<RouteOrderFinancialsResponse> Orders);
