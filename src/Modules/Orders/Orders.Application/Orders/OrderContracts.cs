using System.Collections.Immutable;
using Orders.Domain;

namespace Orders.Application.Orders;

public sealed record OrderAcceptanceInput(
    string TermsVersion,
    string PrivacyVersion,
    DateTimeOffset AcceptedAt,
    string AcceptanceChannel);

/// <param name="RestrictedGoodsAcknowledged">
/// ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02: the dispatcher's confirmation that the shipment contains no
/// prohibited goods. It must be true; false is rejected with the uniform conflict before any effect. It is a
/// dispatcher attestation, not part of the customer's legal acceptance, so OrderAcceptanceCanonicalForm v1 is
/// unchanged; it is recorded in the append-only ORDER_CREATED order event and its audit entry.
/// </param>
public sealed record CreateOrderCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid QuoteId,
    string PayerType,
    OrderAcceptanceInput Acceptance,
    string? RequestId,
    long CodExpectedCents = 0,
    bool RestrictedGoodsAcknowledged = false);

public sealed record MoneyResult(string Currency, long AmountCents);

public sealed record OrderResult(
    Guid Id,
    string PublicId,
    Guid OwnerOrganizationId,
    Guid? OperatorOrganizationId,
    string Status,
    MoneyResult PriceNet,
    int Version,
    Guid OriginLocationId,
    Guid DestinationLocationId,
    string ServiceType,
    Guid QuoteId,
    Guid CityId,
    Guid? ServiceAreaId,
    string PricingTier,
    MoneyResult Total,
    DateTimeOffset? ClaimWindowEndsAt,
    DateTimeOffset? FinalizedAt);

public sealed record OrderTimelineItem(string EventType, DateTimeOffset OccurredAt);

public sealed record OrderDetailResult(OrderResult Order, IReadOnlyList<OrderTimelineItem> Timeline)
{
    public IReadOnlyList<OrderTimelineItem> Timeline { get; } = Timeline.ToImmutableArray();
}

public sealed record OrderPageResult(IReadOnlyList<OrderResult> Items, string? NextCursor)
{
    public IReadOnlyList<OrderResult> Items { get; } = Items.ToImmutableArray();
}

public interface IOrderService
{
    Task<OrderResult> CreateAsync(CreateOrderCommand command, CancellationToken cancellationToken);

    /// <param name="codPendingReconciliation">
    /// API-FIN-COD-VISIBILITY-2026-09-29: true keeps only orders whose COD collection is RECORDED and not yet
    /// RECONCILED. It reveals financial state, so the endpoint admits it only for a caller that also holds
    /// getOrderFinancials, decided before this service is reached. FIN-PENDING-COD-LIST-FINANCE-2026-10-02: when it
    /// is true the service re-checks that authorization inside its tenant transaction, before any order is read,
    /// and throws <see cref="OrderListForbiddenException"/> when it no longer holds.
    /// </param>
    /// <param name="mfaSatisfied">
    /// Whether the session satisfied an MFA challenge; it only matters for that in-transaction re-check, where
    /// PLATFORM_ADMIN and FINANCE need it and DISPATCHER does not (FINANCE-COD-MFA-2026-09-27).
    /// </param>
    Task<OrderPageResult> ListAsync(
        Guid actorId,
        Guid organizationId,
        string? status,
        Guid? ownerOrganizationId,
        string? cursor,
        bool codPendingReconciliation,
        bool mfaSatisfied,
        CancellationToken cancellationToken);

    Task<OrderDetailResult> GetAsync(
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken);
}

public enum OrderConflictCode
{
    InvalidRequest,
    QuoteUnavailable,
    IdempotencyConflict,
}

public sealed class OrderConflictException(OrderConflictCode code)
    : Exception("The order request conflicts with current state.")
{
    public OrderConflictCode Code { get; } = code;
}

/// <summary>
/// FIN-PENDING-COD-LIST-FINANCE-2026-10-02: the order list filtered by COD pending reconciliation was refused inside
/// the tenant transaction because the actor no longer holds an active role that reads order financials there. It is
/// raised before any order is read and is reported as the uniform 403.
/// </summary>
public sealed class OrderListForbiddenException : Exception
{
    public OrderListForbiddenException() : base("The order list is not permitted.")
    {
    }
}

public sealed class OrderNotFoundException : Exception
{
    public OrderNotFoundException() : base("The order was not found.")
    {
    }
}

public sealed class OrderServiceUnavailableException : Exception
{
    public OrderServiceUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public interface IOrderPublicIdGenerator
{
    string Create();
}

public static class OrderInputPolicy
{
    /// <summary>The longest decimal text an int64 amount of cents can have (<c>9223372036854775807</c>).</summary>
    public const int MaximumCodExpectedCentsDigits = 19;

    /// <summary>
    /// D6-COD-EXPECTED: the dispatcher-declared COD expectation is MXN integer cents (int64), zero meaning no COD.
    /// A negative amount is never a valid expectation, and COD-CAP-20000-2026-10-02 caps it at 2,000,000 cents
    /// (20,000.00 MXN) per order, inclusive.
    /// </summary>
    public static bool IsCodExpectedCents(long value) => OrderCodExpectationPolicy.IsValid(value);

    /// <summary>
    /// Reads a COD expectation written as text, on a CSV-001 row or as the raw JSON number of
    /// <c>POST /orders</c>. Only a plain run of ASCII digits that fits int64 is accepted: signs, decimal points,
    /// exponents, thousands separators, currency symbols and whitespace are all rejected, so no fractional or
    /// floating-point value can ever become an amount of cents. A well-formed amount above the
    /// COD-CAP-20000-2026-10-02 cap is rejected exactly like a malformed one.
    /// </summary>
    public static bool TryParseCodExpectedCents(string? text, out long cents)
    {
        if (!string.IsNullOrEmpty(text) &&
            text.Length <= MaximumCodExpectedCentsDigits &&
            text.All(char.IsAsciiDigit) &&
            long.TryParse(
                text,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) &&
            IsCodExpectedCents(parsed))
        {
            cents = parsed;
            return true;
        }

        cents = 0;
        return false;
    }

    public static bool IsPayerType(string? value) =>
        value is "SENDER" or "RECIPIENT" or "BUSINESS_ACCOUNT";

    public static bool TryParsePayerType(string? value, out PayerType payerType)
    {
        payerType = value switch
        {
            "SENDER" => PayerType.Sender,
            "RECIPIENT" => PayerType.Recipient,
            "BUSINESS_ACCOUNT" => PayerType.BusinessAccount,
            _ => default,
        };
        return IsPayerType(value);
    }

    public static bool IsAcceptanceChannel(string? value) =>
        value is "WEB" or "PWA" or "ASSISTED" or "API";
}

/// <summary>
/// AI05-INPUT-LIMITS, with the values the owner approved in AI-05 <c>x-pilot-contract-deltas</c>
/// (ORD-ACCEPTANCE-LIMITS): a version identifier is 1 to <see cref="MaximumVersionLength"/> characters of
/// <c>^[A-Za-z0-9._-]+$</c>, the same on <c>POST /orders</c> and on a CSV-001 row, and <c>accepted_at</c>
/// lies no later than <see cref="MaximumFutureSkew"/> after and no earlier than <see cref="MaximumAge"/>
/// before server time. Everything is rejected before a transaction opens, so the append-only
/// <c>orders.order_acceptances</c> evidence never stores an unbounded or implausible client value.
/// </summary>
public static class OrderAcceptanceInputPolicy
{
    public const int MaximumVersionLength = 64;
    public const string VersionPattern = "^[A-Za-z0-9._-]+$";
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(72);

    public static bool IsValid(
        string? termsVersion,
        string? privacyVersion,
        DateTimeOffset acceptedAt,
        string? acceptanceChannel) =>
        IsVersion(termsVersion) &&
        IsVersion(privacyVersion) &&
        acceptedAt != default &&
        OrderInputPolicy.IsAcceptanceChannel(acceptanceChannel);

    public static bool IsVersion(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= MaximumVersionLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    /// <summary>Both bounds are inclusive: exactly +5 minutes and exactly -72 hours are accepted.</summary>
    public static bool IsWithinAcceptanceWindow(DateTimeOffset acceptedAt, DateTimeOffset serverNow) =>
        acceptedAt <= serverNow + MaximumFutureSkew && acceptedAt >= serverNow - MaximumAge;
}
