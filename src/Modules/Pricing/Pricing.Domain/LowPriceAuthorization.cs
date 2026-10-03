namespace Pricing.Domain;

/// <summary>
/// LOW-PRICE-MANUAL-AUTH-2026-10-02 (project owner, "Sí, con autorización"; "En la cotización"): a shipment of
/// 52 MXN or less, IVA included, that is not on a consolidated route may be quoted when a DISPATCHER, or a
/// PLATFORM_ADMIN with a satisfied MFA challenge, authorizes it manually with a reason. The authorization is the
/// AI-02 <c>financial_override</c> ("actor, motivo y vigencia") of the quote: <see cref="ActorId"/> is the caller,
/// <see cref="Reason"/> the trimmed reason (1 to 200 characters, operational text without personal data) and
/// <see cref="ValidUntil"/> the quote's own expiry. The order created from the quote copies it unchanged.
/// </summary>
public sealed record LowPriceAuthorization
{
    public const int MaximumReasonLength = 200;

    public LowPriceAuthorization(Guid actorId, string reason, DateTimeOffset validUntil)
    {
        if (actorId == Guid.Empty || !IsValidReason(reason) || validUntil.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The low price authorization is invalid.");
        }

        ActorId = actorId;
        Reason = reason;
        ValidUntil = validUntil;
    }

    public Guid ActorId { get; }

    public string Reason { get; }

    public DateTimeOffset ValidUntil { get; }

    /// <summary>
    /// A reason is already trimmed, holds 1 to <see cref="MaximumReasonLength"/> characters and no control
    /// character (a line break or tab is never part of an operational reason).
    /// </summary>
    public static bool IsValidReason(string? reason) =>
        reason is { Length: >= 1 and <= MaximumReasonLength } &&
        string.Equals(reason, reason.Trim(), StringComparison.Ordinal) &&
        !reason.Any(char.IsControl);

    /// <summary>The trimmed reason, or null when the trimmed text is not a valid reason.</summary>
    public static string? Normalize(string? reason)
    {
        var trimmed = reason?.Trim();
        return IsValidReason(trimmed) ? trimmed : null;
    }
}

/// <summary>
/// AI-02 <c>low_price_guard</c> and AI-07 <c>create_order.low_price_guard</c> on the server: the 52 and 45 tiers
/// (<see cref="TariffRuleEvaluator.RequiresConsolidatedRoute"/>) and any quote whose total with IVA included is
/// 52 MXN or less (<see cref="ThresholdTotalCents"/>, non-strict) need a consolidated route; without one they need
/// a <see cref="LowPriceAuthorization"/> (LOW-PRICE-MANUAL-AUTH-2026-10-02). A quote on a consolidated route, or
/// above the threshold in another tier, never carries one: an authorization that is not needed is refused rather
/// than silently stored.
/// </summary>
public static class LowPriceGuardPolicy
{
    /// <summary>52.00 MXN, IVA included (GATE-011-VAT-INCLUDED-2026-09-29: "52 con IVA incluido").</summary>
    public const long ThresholdTotalCents = 5_200;

    public static bool RequiresAuthorization(PricingTier pricingTier, bool consolidatedRoute, long totalCents) =>
        !consolidatedRoute &&
        (TariffRuleEvaluator.RequiresConsolidatedRoute(pricingTier) || totalCents <= ThresholdTotalCents);
}
