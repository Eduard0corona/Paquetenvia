using System.Text.Json.Serialization;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;
using Pricing.Application.Quotes;

namespace Pricing.Endpoints;

public static class QuoteEndpoints
{
    public static IEndpointRouteBuilder MapQuoteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/quotes", CreateAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("createQuote")
            .WithTags("Quotes")
            .Accepts<CreateQuoteRequest>("application/json")
            .Produces<QuoteResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/v1/quotes/{quoteId:guid}", GetAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("getQuote")
            .WithTags("Pricing")
            .Produces<QuoteResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext httpContext,
        CreateQuoteRequest request,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IQuoteService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadIdempotencyKey(httpContext.Request, out var idempotencyKey) ||
            !IsServiceType(request.ServiceType) ||
            !IsValid(request))
        {
            return Unprocessable();
        }

        if (!session.IsActive || session.UserId is not { } actorId || !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.CreateQuote) is { } denied)
        {
            return denied;
        }

        // LOW-PRICE-MANUAL-AUTH-2026-10-02: the authorization field has its own capability, decided from the session
        // before the service reads or writes anything (the idempotency record included).
        QuoteLowPriceAuthorizationInput? lowPriceAuthorization = null;
        if (request.LowPriceAuthorization is { } authorization)
        {
            if (TenantCapabilityGate.Deny(
                    session,
                    tenantContext,
                    TenantFieldCapabilities.CreateQuoteLowPriceAuthorization) is { } authorizationDenied)
            {
                return authorizationDenied;
            }

            lowPriceAuthorization = new QuoteLowPriceAuthorizationInput(
                QuoteLowPriceAuthorizationPolicy.NormalizeReason(authorization.Reason)!);
        }

        try
        {
            var result = await service.CreateAsync(
                new CreateQuoteCommand(
                    actorId,
                    tenantContext.OrganizationId,
                    idempotencyKey,
                    request.ClientAccountId,
                    ToApplication(request.Origin),
                    ToApplication(request.Destination),
                    request.ServiceType,
                    request.ConsolidatedRoute,
                    request.Packages.Select(ToApplication).ToArray(),
                    httpContext.TraceIdentifier,
                    lowPriceAuthorization),
                cancellationToken);
            return Results.Created(
                $"/api/v1/quotes/{result.Id:D}",
                ToResponse(result, RevealsLowPriceAuthorizationDetails(session, tenantContext)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (QuoteValidationException)
        {
            return Unprocessable();
        }
        catch (QuoteConflictException)
        {
            return Conflict();
        }
        catch (QuoteServiceUnavailableException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
        }
    }

    private static async Task<IResult> GetAsync(
        Guid quoteId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IQuoteService service,
        CancellationToken cancellationToken)
    {
        if (!session.IsActive || session.UserId is not { } actorId || !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.GetQuote) is { } denied)
        {
            return denied;
        }

        try
        {
            return Results.Ok(ToResponse(
                await service.GetAsync(actorId, tenantContext.OrganizationId, quoteId, cancellationToken),
                RevealsLowPriceAuthorizationDetails(session, tenantContext)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (QuoteNotFoundException)
        {
            return NotFound();
        }
        catch (QuoteServiceUnavailableException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
        }
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

    private static bool IsValid(CreateQuoteRequest request) =>
        request.Origin is not null && request.Destination is not null &&
        request.Packages is { Count: > 0 and <= QuoteInputLimits.MaximumPackages } &&
        IsValid(request.Origin) && IsValid(request.Destination) && request.Packages.All(IsValid) &&
        (request.LowPriceAuthorization is null || IsValid(request.LowPriceAuthorization));

    /// <summary>LOW-PRICE-MANUAL-AUTH-2026-10-02: the reason, once trimmed, holds 1 to 200 characters.</summary>
    private static bool IsValid(LowPriceAuthorizationInput request) =>
        QuoteLowPriceAuthorizationPolicy.NormalizeReason(request.Reason) is not null;

    /// <summary>
    /// LOW-PRICE-MANUAL-AUTH-2026-10-02: who authorized and why are financial control data, so only a caller that
    /// may read order financials in the selected organization (getOrderFinancials: DISPATCHER, PLATFORM_ADMIN with
    /// MFA) receives them. Every other reader of the quote (VIEWER, PLATFORM_ADMIN without MFA) only learns that an
    /// authorization exists and until when it is valid.
    /// </summary>
    private static bool RevealsLowPriceAuthorizationDetails(
        IOrganizationRequestSession session,
        ITenantContext tenantContext) =>
        tenantContext.IsSelected &&
        TenantCapabilities.GetOrderFinancials.Evaluate(session, tenantContext.OrganizationId) ==
        TenantCapabilityDecision.Allowed;

    private static bool IsValid(AddressInput request) =>
        !string.IsNullOrWhiteSpace(request.AddressText) && request.AddressText.Trim().Length >= 8 &&
        !string.IsNullOrWhiteSpace(request.ContactName) && !string.IsNullOrWhiteSpace(request.Phone) &&
        (request.References is null || request.References.Length <= 500) &&
        request.Lat is >= -90 and <= 90 &&
        request.Lng is >= -180 and <= 180 &&
        !double.IsNaN(request.Lat.Value) && !double.IsInfinity(request.Lat.Value) &&
        !double.IsNaN(request.Lng.Value) && !double.IsInfinity(request.Lng.Value);

    private static bool IsValid(PackageInput request) =>
        !string.IsNullOrWhiteSpace(request.Description) && request.Description.Length <= 250 &&
        request.WeightGrams >= 1 && request.DeclaredValueCents >= 0 &&
        request.LengthMm is null or > 0 && request.WidthMm is null or > 0 && request.HeightMm is null or > 0;

    private static bool IsServiceType(string? value) =>
        value is "SAME_DAY" or "URGENT" or "SCHEDULED_ROUTE";

    private static QuoteAddressInput ToApplication(AddressInput input) => new(
        input.AddressText,
        input.ContactName,
        input.Phone,
        input.Lat,
        input.Lng,
        input.References);

    private static QuotePackageInput ToApplication(PackageInput input) => new(
        input.Description,
        input.WeightGrams,
        input.DeclaredValueCents,
        input.LengthMm,
        input.WidthMm,
        input.HeightMm);

    private static QuoteResponse ToResponse(QuoteResult result, bool revealAuthorizationDetails) => new(
        result.Id,
        new MoneyResponse(result.Net.Currency, result.Net.AmountCents),
        new MoneyResponse(result.Tax.Currency, result.Tax.AmountCents),
        new MoneyResponse(result.Total.Currency, result.Total.AmountCents),
        result.RuleIds,
        result.Breakdown.Select(line => new BreakdownResponse(
            line.LineType, line.RuleId, line.AmountCents, line.PricingTier, line.TaxMode)).ToArray(),
        result.ExpiresAt,
        result.OriginLocationId,
        result.DestinationLocationId,
        result.ServiceType,
        result.ConsolidatedRoute,
        result.PackageSnapshot.Select(package => new PackageInput(
            package.Description,
            package.WeightGrams,
            package.DeclaredValueCents,
            package.LengthMm,
            package.WidthMm,
            package.HeightMm)).ToArray(),
        result.CityId,
        result.ServiceAreaId,
        result.PricingTier,
        result.MinimumTotalCentsSnapshot,
        result.PricingPolicyVersion,
        result.Status,
        result.RequestSnapshotRedacted,
        result.LowPriceAuthorization is not { } authorization
            ? null
            : new LowPriceAuthorizationResponse(
                authorization.ValidUntil,
                revealAuthorizationDetails ? authorization.ActorId : null,
                revealAuthorizationDetails ? authorization.Reason : null));

    private static IResult Unprocessable() => Results.Problem(
        statusCode: StatusCodes.Status422UnprocessableEntity,
        title: "Validation failed.");
    private static IResult Forbidden() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");
    private static IResult Conflict() => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict.");
    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found.");
}

public sealed record CreateQuoteRequest(
    [property: JsonPropertyName("client_account_id")] Guid? ClientAccountId,
    [property: JsonPropertyName("origin")] AddressInput Origin,
    [property: JsonPropertyName("destination")] AddressInput Destination,
    [property: JsonPropertyName("service_type")] string ServiceType,
    [property: JsonPropertyName("consolidated_route")] bool ConsolidatedRoute,
    [property: JsonPropertyName("packages")] IReadOnlyList<PackageInput> Packages,
    [property: JsonPropertyName("low_price_authorization")] LowPriceAuthorizationInput? LowPriceAuthorization = null);

/// <summary>
/// LOW-PRICE-MANUAL-AUTH-2026-10-02: manual authorization of a shipment of 52 MXN or less, IVA included, that is not
/// on a consolidated route. The reason is operational text without personal data.
/// </summary>
public sealed record LowPriceAuthorizationInput(
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>
/// The authorization a quote carries: its validity always, and who authorized it and why only for a caller that may
/// read order financials.
/// </summary>
public sealed record LowPriceAuthorizationResponse(
    [property: JsonPropertyName("valid_until")] DateTimeOffset ValidUntil,
    [property: JsonPropertyName("actor_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ActorId,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason);

public sealed record AddressInput(
    [property: JsonPropertyName("address_text")] string AddressText,
    [property: JsonPropertyName("contact_name")] string ContactName,
    [property: JsonPropertyName("phone")] string Phone,
    [property: JsonPropertyName("lat")] double? Lat,
    [property: JsonPropertyName("lng")] double? Lng,
    [property: JsonPropertyName("references")] string? References);

public sealed record PackageInput(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("weight_grams")] int WeightGrams,
    [property: JsonPropertyName("declared_value_cents")] long DeclaredValueCents,
    [property: JsonPropertyName("length_mm")] int? LengthMm,
    [property: JsonPropertyName("width_mm")] int? WidthMm,
    [property: JsonPropertyName("height_mm")] int? HeightMm);

public sealed record MoneyResponse(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("amount_cents")] long AmountCents);

public sealed record BreakdownResponse(
    [property: JsonPropertyName("line_type")] string LineType,
    [property: JsonPropertyName("rule_id")] Guid RuleId,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("pricing_tier")] string PricingTier,
    [property: JsonPropertyName("tax_mode")] string TaxMode);

public sealed record QuoteResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("net")] MoneyResponse Net,
    [property: JsonPropertyName("tax")] MoneyResponse Tax,
    [property: JsonPropertyName("total")] MoneyResponse Total,
    [property: JsonPropertyName("rule_ids")] IReadOnlyList<Guid> RuleIds,
    [property: JsonPropertyName("breakdown")] IReadOnlyList<BreakdownResponse> Breakdown,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("origin_location_id")] Guid OriginLocationId,
    [property: JsonPropertyName("destination_location_id")] Guid DestinationLocationId,
    [property: JsonPropertyName("service_type")] string ServiceType,
    [property: JsonPropertyName("consolidated_route")] bool ConsolidatedRoute,
    [property: JsonPropertyName("package_snapshot")] IReadOnlyList<PackageInput> PackageSnapshot,
    [property: JsonPropertyName("city_id")] Guid CityId,
    [property: JsonPropertyName("service_area_id")] Guid? ServiceAreaId,
    [property: JsonPropertyName("pricing_tier")] string PricingTier,
    [property: JsonPropertyName("minimum_total_cents_snapshot")] long MinimumTotalCentsSnapshot,
    [property: JsonPropertyName("pricing_policy_version")] string PricingPolicyVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("request_snapshot_redacted")] IReadOnlyDictionary<string, object?> RequestSnapshotRedacted,
    [property: JsonPropertyName("low_price_authorization")] LowPriceAuthorizationResponse? LowPriceAuthorization);
