using System.Text.Json;
using System.Text.Json.Serialization;
using Orders.Application.Orders;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;

namespace Orders.Endpoints;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/orders", CreateAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("createOrder")
            .WithTags("Orders")
            .Accepts<CreateOrderRequest>("application/json")
            .Produces<OrderResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/v1/orders", ListAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("listOrders")
            .WithTags("Orders")
            .Produces<OrderPageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/v1/orders/{orderId:guid}", GetAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("getOrder")
            .WithTags("Orders")
            .Produces<OrderDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapPost("/api/v1/orders/{orderId:guid}/transitions", TransitionAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("transitionOrder")
            .WithTags("Orders")
            .Accepts<TransitionOrderRequest>("application/json")
            .Produces<OrderResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext httpContext,
        CreateOrderRequest request,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IOrderService service,
        Paqueteria.Application.IClock clock,
        CancellationToken cancellationToken)
    {
        if (!TryReadIdempotencyKey(httpContext.Request, out var idempotencyKey) ||
            !IsValid(request) ||
            !OrderAcceptanceInputPolicy.IsWithinAcceptanceWindow(request.Acceptance.AcceptedAt, clock.UtcNow))
        {
            return Conflict();
        }

        if (!session.IsActive || session.UserId is not { } actorId || !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.CreateOrder) is { } denied)
        {
            return denied;
        }

        try
        {
            var result = await service.CreateAsync(
                new CreateOrderCommand(
                    actorId,
                    tenantContext.OrganizationId,
                    idempotencyKey,
                    request.QuoteId,
                    request.PayerType,
                    new OrderAcceptanceInput(
                        request.Acceptance.TermsVersion,
                        request.Acceptance.PrivacyVersion,
                        request.Acceptance.AcceptedAt,
                        request.Acceptance.AcceptanceChannel),
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Created($"/api/v1/orders/{result.Id:D}", ToResponse(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrderConflictException)
        {
            return Conflict();
        }
        catch (OrderServiceUnavailableException)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> ListAsync(
        string? status,
        Guid? owner_org_id,
        string? cod_pending_reconciliation,
        string? cursor,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IOrderService service,
        CancellationToken cancellationToken)
    {
        if (!session.IsActive || session.UserId is not { } actorId || !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.ListOrders) is { } denied)
        {
            return denied;
        }

        // API-FIN-COD-VISIBILITY-2026-09-29: the COD filter reveals financial state, so its mere presence also
        // requires the getOrderFinancials capability, decided before any order is read.
        if (cod_pending_reconciliation is not null &&
            TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.GetOrderFinancials) is { } financeDenied)
        {
            return financeDenied;
        }

        if (!OrderListCodFilter.TryParse(cod_pending_reconciliation, out var codPendingReconciliation))
        {
            return Results.Ok(new OrderPageResponse([], null));
        }

        try
        {
            var page = await service.ListAsync(
                actorId,
                tenantContext.OrganizationId,
                status,
                owner_org_id,
                cursor,
                codPendingReconciliation,
                cancellationToken);
            return Results.Ok(new OrderPageResponse(
                page.Items.Select(ToResponse).ToArray(),
                page.NextCursor));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrderServiceUnavailableException)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> GetAsync(
        Guid orderId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IOrderService service,
        CancellationToken cancellationToken)
    {
        if (!session.IsActive || session.UserId is not { } actorId || !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.GetOrder) is { } denied)
        {
            return denied;
        }

        try
        {
            var detail = await service.GetAsync(
                actorId,
                tenantContext.OrganizationId,
                orderId,
                cancellationToken);
            return Results.Ok(ToDetailResponse(detail));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrderNotFoundException)
        {
            return NotFound();
        }
        catch (OrderServiceUnavailableException)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> TransitionAsync(
        Guid orderId,
        HttpContext httpContext,
        TransitionOrderRequest request,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IOrderTransitionService service,
        OfflineOperationAgePolicy offlinePolicy,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!TryReadIdempotencyKey(httpContext.Request, out var idempotencyKey) ||
            orderId == Guid.Empty ||
            request is null ||
            string.IsNullOrWhiteSpace(request.Reason) ||
            request.Reason.Length > OrderTransitionInputPolicy.MaximumReasonLength ||
            request.ExpectedVersion < 1 ||
            !OrderTransitionInputPolicy.IsValidMetadataForTarget(
                request.TargetStatus,
                request.Metadata?.GetRawText(),
                OrderTransitionInputPolicy.DefaultMaximumMetadataUtf8Bytes))
        {
            return Conflict();
        }

        if (!session.IsActive || session.UserId is not { } actorId || !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        // OPS-003-SERVER-72H-REJECTION: an offline replay older than 72 hours never reaches the
        // transition service, whatever its idempotency key; a clock ahead of the tolerance is invalid.
        switch (offlinePolicy.Evaluate(request.ClientOccurredAt, clock.UtcNow))
        {
            case OfflineOperationAge.Expired:
                return OfflineOperationExpired();
            case OfflineOperationAge.AheadOfServerClock:
                return Conflict();
        }

        try
        {
            var result = await service.TransitionAsync(
                new TransitionOrderCommand(
                    actorId,
                    tenantContext.OrganizationId,
                    idempotencyKey,
                    orderId,
                    request.TargetStatus,
                    request.Reason,
                    request.ExpectedVersion,
                    request.Metadata?.GetRawText(),
                    session.MfaSatisfied,
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Ok(ToResponse(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrderTransitionForbiddenException)
        {
            return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.TransitionOrder);
        }
        catch (OrderTransitionConflictException)
        {
            return Conflict();
        }
        catch (OrderTransitionInfrastructureException)
        {
            return Conflict();
        }
    }

    private static bool IsValid(CreateOrderRequest request) =>
        request is not null &&
        request.QuoteId != Guid.Empty &&
        OrderInputPolicy.IsPayerType(request.PayerType) &&
        request.Acceptance is not null &&
        OrderAcceptanceInputPolicy.IsValid(
            request.Acceptance.TermsVersion,
            request.Acceptance.PrivacyVersion,
            request.Acceptance.AcceptedAt,
            request.Acceptance.AcceptanceChannel);

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

    private static OrderResponse ToResponse(OrderResult result) => new(
        result.Id,
        result.PublicId,
        result.OwnerOrganizationId,
        result.OperatorOrganizationId,
        result.Status,
        new MoneyResponse(result.PriceNet.Currency, result.PriceNet.AmountCents),
        result.Version,
        result.OriginLocationId,
        result.DestinationLocationId,
        result.ServiceType,
        result.QuoteId,
        result.CityId,
        result.ServiceAreaId,
        result.PricingTier,
        new MoneyResponse(result.Total.Currency, result.Total.AmountCents),
        result.ClaimWindowEndsAt,
        result.FinalizedAt);

    private static OrderDetailResponse ToDetailResponse(OrderDetailResult result)
    {
        var order = ToResponse(result.Order);
        return new OrderDetailResponse(
            order.Id,
            order.PublicId,
            order.OwnerOrganizationId,
            order.OperatorOrganizationId,
            order.Status,
            order.PriceNet,
            order.Version,
            order.OriginLocationId,
            order.DestinationLocationId,
            order.ServiceType,
            order.QuoteId,
            order.CityId,
            order.ServiceAreaId,
            order.PricingTier,
            order.Total,
            order.ClaimWindowEndsAt,
            order.FinalizedAt,
            result.Timeline.Select(item => new OrderTimelineResponse(
                item.EventType,
                item.OccurredAt)).ToArray());
    }

    private static IResult Conflict() =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict.");

    private static IResult OfflineOperationExpired() =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict.",
            extensions: new Dictionary<string, object?> { ["code"] = OfflineOperationAgePolicy.ExpiredCode });

    private static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
}

public sealed record CreateOrderRequest(
    [property: JsonPropertyName("quote_id")] Guid QuoteId,
    [property: JsonPropertyName("payer_type")] string PayerType,
    [property: JsonPropertyName("acceptance")] OrderAcceptanceRequest Acceptance);

public sealed record OrderAcceptanceRequest(
    [property: JsonPropertyName("terms_version")] string TermsVersion,
    [property: JsonPropertyName("privacy_version")] string PrivacyVersion,
    [property: JsonPropertyName("accepted_at")] DateTimeOffset AcceptedAt,
    [property: JsonPropertyName("acceptance_channel")] string AcceptanceChannel);

public sealed record TransitionOrderRequest(
    [property: JsonPropertyName("target_status")] string? TargetStatus,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("expected_version")] int ExpectedVersion,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata,
    [property: JsonPropertyName("client_occurred_at")] DateTimeOffset? ClientOccurredAt = null);

public sealed record MoneyResponse(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("amount_cents")] long AmountCents);

public sealed record OrderResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("public_id")] string PublicId,
    [property: JsonPropertyName("owner_org_id")] Guid OwnerOrganizationId,
    [property: JsonPropertyName("operator_org_id")] Guid? OperatorOrganizationId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("price_net")] MoneyResponse PriceNet,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("origin_location_id")] Guid OriginLocationId,
    [property: JsonPropertyName("destination_location_id")] Guid DestinationLocationId,
    [property: JsonPropertyName("service_type")] string ServiceType,
    [property: JsonPropertyName("quote_id")] Guid QuoteId,
    [property: JsonPropertyName("city_id")] Guid CityId,
    [property: JsonPropertyName("service_area_id")] Guid? ServiceAreaId,
    [property: JsonPropertyName("pricing_tier")] string PricingTier,
    [property: JsonPropertyName("total")] MoneyResponse Total,
    [property: JsonPropertyName("claim_window_ends_at")] DateTimeOffset? ClaimWindowEndsAt,
    [property: JsonPropertyName("finalized_at")] DateTimeOffset? FinalizedAt);

public sealed record OrderTimelineResponse(
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt);

public sealed record OrderDetailResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("public_id")] string PublicId,
    [property: JsonPropertyName("owner_org_id")] Guid OwnerOrganizationId,
    [property: JsonPropertyName("operator_org_id")] Guid? OperatorOrganizationId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("price_net")] MoneyResponse PriceNet,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("origin_location_id")] Guid OriginLocationId,
    [property: JsonPropertyName("destination_location_id")] Guid DestinationLocationId,
    [property: JsonPropertyName("service_type")] string ServiceType,
    [property: JsonPropertyName("quote_id")] Guid QuoteId,
    [property: JsonPropertyName("city_id")] Guid CityId,
    [property: JsonPropertyName("service_area_id")] Guid? ServiceAreaId,
    [property: JsonPropertyName("pricing_tier")] string PricingTier,
    [property: JsonPropertyName("total")] MoneyResponse Total,
    [property: JsonPropertyName("claim_window_ends_at")] DateTimeOffset? ClaimWindowEndsAt,
    [property: JsonPropertyName("finalized_at")] DateTimeOffset? FinalizedAt,
    [property: JsonPropertyName("timeline")] IReadOnlyList<OrderTimelineResponse> Timeline);

public sealed record OrderPageResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<OrderResponse> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

/// <summary>
/// API-FIN-COD-VISIBILITY-2026-09-29: <c>cod_pending_reconciliation</c> accepts exactly <c>true</c> or <c>false</c>;
/// absent means no filter. Any other value is an unmatched filter, answered like an unknown status with an empty
/// page, and only after the capability gate.
/// </summary>
public static class OrderListCodFilter
{
    public static bool TryParse(string? value, out bool codPendingReconciliation)
    {
        codPendingReconciliation = false;
        switch (value)
        {
            case null:
            case "false":
                return true;
            case "true":
                codPendingReconciliation = true;
                return true;
            default:
                return false;
        }
    }
}
