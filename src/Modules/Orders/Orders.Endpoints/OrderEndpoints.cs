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
            !OrderAcceptanceInputPolicy.IsWithinAcceptanceWindow(request.Acceptance.AcceptedAt, clock.UtcNow) ||
            !TryReadCodExpectedCents(request.CodExpectedCents, out var codExpectedCents) ||
            !TryReadServiceWindow(request.ServiceWindow, out var serviceWindow) ||
            (serviceWindow is not null && !OrderServiceWindowPolicy.IsWithinServerTime(serviceWindow, clock.UtcNow)))
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
                    httpContext.TraceIdentifier,
                    codExpectedCents,
                    RestrictedGoodsAcknowledged: true,
                    ServiceWindow: serviceWindow),
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
        string? public_id,
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
            // FIN-PENDING-COD-LIST-FINANCE-2026-10-02: a caller without listOrders is admitted only to the COD
            // pending list itself, exactly cod_pending_reconciliation=true, and only as FINANCE with MFA
            // (MFA_REQUIRED without it). Every other shape keeps the refusal listOrders already gives. Both
            // decisions read the request and the session only, before any order is read.
            if (!OrderListCodFilter.IsPendingOnly(cod_pending_reconciliation))
            {
                return denied;
            }

            if (TenantCapabilityGate.Deny(
                    session,
                    tenantContext,
                    TenantCapabilityRefinements.ListOrdersCodPendingReconciliationOnly) is { } pendingDenied)
            {
                return pendingDenied;
            }
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
                session.MfaSatisfied,
                public_id,
                cancellationToken);
            return Results.Ok(new OrderPageResponse(
                page.Items.Select(ToResponse).ToArray(),
                page.NextCursor));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrderListForbiddenException)
        {
            return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.GetOrderFinancials);
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
                session.MfaSatisfied,
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
        catch (OrderTransitionConflictException exception)
        {
            return TransitionConflict(exception.RejectionCode);
        }
        catch (OrderTransitionInfrastructureException)
        {
            return Conflict();
        }
    }

    /// <summary>
    /// ORD-002-GUARD-CODES-2026-10-05: the AI-05 TransitionConflict. The service sets a rule code only for an order of
    /// the selected organization whose caller holds the transitionOrder capability; any other conflict, and any value
    /// outside the closed enum, is the uniform 409 without a code.
    /// </summary>
    private static IResult TransitionConflict(string? rejectionCode) =>
        OrderTransitionRejectionCodes.IsDefined(rejectionCode)
            ? Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict.",
                extensions: new Dictionary<string, object?> { ["code"] = rejectionCode })
            : Conflict();

    private static bool IsValid(CreateOrderRequest request) =>
        request is not null &&
        request.QuoteId != Guid.Empty &&
        OrderInputPolicy.IsPayerType(request.PayerType) &&
        request.Acceptance is not null &&
        OrderAcceptanceInputPolicy.IsValid(
            request.Acceptance.TermsVersion,
            request.Acceptance.PrivacyVersion,
            request.Acceptance.AcceptedAt,
            request.Acceptance.AcceptanceChannel) &&
        IsRestrictedGoodsAcknowledged(request.RestrictedGoodsAcknowledged);

    /// <summary>
    /// ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02: the dispatcher must confirm the shipment contains no prohibited
    /// goods with the JSON literal <c>true</c>. Absent, null, <c>false</c>, a quoted <c>"true"</c> or any other
    /// value is the uniform 409 before any effect.
    /// </summary>
    private static bool IsRestrictedGoodsAcknowledged(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.True };

    /// <summary>
    /// D6-COD-EXPECTED: an absent (or JSON null) <c>cod_expected_cents</c> is zero. A present value must be a JSON
    /// number whose literal is a plain non-negative integer of at most 2,000,000 cents (COD-CAP-20000-2026-10-02);
    /// <c>1.5</c>, <c>1e3</c>, <c>150.0</c>, <c>-1</c>, <c>2000001</c> and a quoted <c>"150"</c> are the uniform
    /// 409, the same as every other invalid contract value, because the literal is checked rather than whatever a
    /// lenient number binder would coerce it to.
    /// </summary>
    private static bool TryReadCodExpectedCents(JsonElement? value, out long cents)
    {
        cents = 0;
        if (value is not { } element || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return element.ValueKind == JsonValueKind.Number &&
            OrderInputPolicy.TryParseCodExpectedCents(element.GetRawText(), out cents);
    }

    /// <summary>
    /// ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02: an absent (or JSON null) <c>service_window</c> means no window. A
    /// present one must be an object with exactly the string members <c>from</c> and <c>to</c>, each an RFC 3339
    /// date-time with an explicit offset and whole seconds, read as text so no lenient binder can assume a local
    /// zone; the result is normalized to UTC and must satisfy the shape rules of
    /// <see cref="OrderServiceWindowPolicy"/>. Anything else is the uniform 409.
    /// </summary>
    private static bool TryReadServiceWindow(JsonElement? value, out OrderServiceWindow? window)
    {
        window = null;
        if (value is not { } element || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? fromText = null;
        string? toText = null;
        foreach (var member in element.EnumerateObject())
        {
            if (member.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            switch (member.Name)
            {
                case "from" when fromText is null:
                    fromText = member.Value.GetString();
                    break;
                case "to" when toText is null:
                    toText = member.Value.GetString();
                    break;
                default:
                    return false;
            }
        }

        if (!OrderServiceWindowPolicy.TryParseInstant(fromText, out var from) ||
            !OrderServiceWindowPolicy.TryParseInstant(toText, out var to))
        {
            return false;
        }

        var candidate = new OrderServiceWindow(from, to);
        if (!OrderServiceWindowPolicy.IsWellFormed(candidate))
        {
            return false;
        }

        window = candidate;
        return true;
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
        result.FinalizedAt,
        ToResponse(result.ServiceWindow));

    private static ServiceWindowResponse? ToResponse(OrderServiceWindow? window) =>
        window is null ? null : new ServiceWindowResponse(window.From, window.To);

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
            order.ServiceWindow,
            result.Timeline.Select(item => new OrderTimelineResponse(
                item.EventType,
                item.OccurredAt)).ToArray(),
            result.AllowedTransitions.Select(item => new OrderAllowedTransitionResponse(
                item.TargetStatus,
                item.RequiredMetadata.ToArray())).ToArray());
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

/// <summary>
/// AI-05 CreateOrderRequest. <c>cod_expected_cents</c> (D6-COD-EXPECTED) is the optional COD the dispatcher
/// declares, in MXN integer cents; absent means zero. It is written to <c>orders.cod_expected_cents</c> and is read
/// back only through the finance operations (FIN-001/SET-001), never through the Order response a VIEWER can read.
/// <c>restricted_goods_acknowledged</c> (ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02) is required and must be the JSON
/// literal <c>true</c>.
/// <c>service_window</c> (ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02) is the optional delivery window <c>{from, to}</c>;
/// absent means the zone's schedule applies. It is stored on the order and returned as <c>Order.service_window</c>.
/// </summary>
public sealed record CreateOrderRequest(
    [property: JsonPropertyName("quote_id")] Guid QuoteId,
    [property: JsonPropertyName("payer_type")] string PayerType,
    [property: JsonPropertyName("acceptance")] OrderAcceptanceRequest Acceptance,
    [property: JsonPropertyName("cod_expected_cents")] JsonElement? CodExpectedCents = null,
    [property: JsonPropertyName("restricted_goods_acknowledged")] JsonElement? RestrictedGoodsAcknowledged = null,
    [property: JsonPropertyName("service_window")] JsonElement? ServiceWindow = null);

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
    [property: JsonPropertyName("finalized_at")] DateTimeOffset? FinalizedAt,
    [property: JsonPropertyName("service_window")] ServiceWindowResponse? ServiceWindow);

/// <summary>ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02: the order's delivery window as UTC instants.</summary>
public sealed record ServiceWindowResponse(
    [property: JsonPropertyName("from")] DateTimeOffset From,
    [property: JsonPropertyName("to")] DateTimeOffset To);

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
    [property: JsonPropertyName("service_window")] ServiceWindowResponse? ServiceWindow,
    [property: JsonPropertyName("timeline")] IReadOnlyList<OrderTimelineResponse> Timeline,
    [property: JsonPropertyName("allowed_transitions")] IReadOnlyList<OrderAllowedTransitionResponse> AllowedTransitions);

/// <summary>
/// UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: one advisory ORD-002 transition the caller could request now with
/// transitionOrder, and the metadata members that request must carry. Guard internals are never exposed.
/// </summary>
public sealed record OrderAllowedTransitionResponse(
    [property: JsonPropertyName("target_status")] string TargetStatus,
    [property: JsonPropertyName("required_metadata")] IReadOnlyList<string> RequiredMetadata);

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
    /// <summary>
    /// FIN-PENDING-COD-LIST-FINANCE-2026-10-02: the single request shape a caller without listOrders may use, the
    /// parameter present with exactly the value <c>true</c>. Absent, <c>false</c> or any other value is not it.
    /// </summary>
    public static bool IsPendingOnly(string? value) => string.Equals(value, "true", StringComparison.Ordinal);

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
