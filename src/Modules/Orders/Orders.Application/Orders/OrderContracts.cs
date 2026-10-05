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
    bool RestrictedGoodsAcknowledged = false,
    OrderServiceWindow? ServiceWindow = null);

/// <summary>
/// ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02: the delivery window the dispatcher committed to, as two UTC instants.
/// Absent means the order carries no window of its own and the zone's schedule applies.
/// </summary>
public sealed record OrderServiceWindow(DateTimeOffset From, DateTimeOffset To);

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
    DateTimeOffset? FinalizedAt,
    OrderServiceWindow? ServiceWindow = null);

public sealed record OrderTimelineItem(string EventType, DateTimeOffset OccurredAt);

/// <param name="AllowedTransitions">
/// UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: the advisory transitions the caller could request now
/// (<see cref="OrderAllowedTransitionsPolicy"/>); empty when none.
/// </param>
public sealed record OrderDetailResult(
    OrderResult Order,
    IReadOnlyList<OrderTimelineItem> Timeline,
    IReadOnlyList<OrderAllowedTransition>? AllowedTransitions = null)
{
    public IReadOnlyList<OrderTimelineItem> Timeline { get; } = Timeline.ToImmutableArray();

    public IReadOnlyList<OrderAllowedTransition> AllowedTransitions { get; } =
        (AllowedTransitions ?? []).ToImmutableArray();
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
    /// <param name="publicId">
    /// UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: exact tracking number (<c>public_id</c>) filter. A value that is not
    /// a well-formed <see cref="OrderPublicIdPolicy"/> identifier matches nothing and is never sent to the database;
    /// RLS keeps the match inside the orders the selected organization may read.
    /// </param>
    Task<OrderPageResult> ListAsync(
        Guid actorId,
        Guid organizationId,
        string? status,
        Guid? ownerOrganizationId,
        string? cursor,
        bool codPendingReconciliation,
        bool mfaSatisfied,
        string? publicId,
        CancellationToken cancellationToken);

    /// <param name="mfaSatisfied">
    /// Whether the session satisfied an MFA challenge; it only feeds the advisory
    /// <see cref="OrderDetailResult.AllowedTransitions"/> (PLATFORM_ADMIN transitions need it).
    /// </param>
    Task<OrderDetailResult> GetAsync(
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        bool mfaSatisfied,
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

/// <summary>
/// ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02 (owner: "Sí, opcional"): <c>CreateOrderRequest.service_window</c> is an
/// optional delivery window <c>{from, to}</c>. Each bound is an RFC 3339 date-time with an explicit offset
/// (<c>Z</c> or <c>±hh:mm</c>; a local time without offset is ambiguous and rejected) and whole-second precision, and
/// is normalized to UTC before it is stored, hashed or returned. The pilot operates in
/// <see cref="PilotTimeZone"/>; the client converts the wall-clock time the dispatcher types into an instant with
/// that zone, never with the browser's zone. The window must satisfy <c>from &lt; to</c>,
/// <c>to - from &lt;= </c><see cref="MaximumSpan"/>, and, against the server clock with the repository's 5-minute
/// tolerance (<see cref="OrderAcceptanceInputPolicy.MaximumFutureSkew"/>), <c>from &gt;= now - 5 min</c>,
/// <c>to &gt; now</c> and <c>from &lt;= now + </c><see cref="MaximumLeadTime"/>. AI-04 defines no same-calendar-day
/// rule, so none is enforced. The project owner confirmed the 12-hour span and the 30-day lead time, no CSV window
/// columns for now and no validation against a zone schedule in the pilot (ORD-SERVICE-WINDOW-LIMITS-CONFIRMED-2026-10-03).
/// Any violation is the uniform 409 before a transaction opens.
/// </summary>
public static class OrderServiceWindowPolicy
{
    public const string PilotTimeZone = "America/Mazatlan";
    public static readonly TimeSpan ClockTolerance = OrderAcceptanceInputPolicy.MaximumFutureSkew;
    public static readonly TimeSpan MaximumSpan = TimeSpan.FromHours(12);
    public static readonly TimeSpan MaximumLeadTime = TimeSpan.FromDays(30);

    /// <summary>The longest accepted bound text (<c>yyyy-MM-ddTHH:mm:ss.fffffff+hh:mm</c>).</summary>
    public const int MaximumInstantLength = 33;

    private static readonly System.Text.RegularExpressions.Regex InstantPattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.(?<fraction>\d{1,7}))?(?:Z|[+-]\d{2}:\d{2})$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Reads one bound: an RFC 3339 date-time with an explicit offset and no sub-second part (an all-zero fraction
    /// is accepted), returned as a UTC instant.
    /// </summary>
    public static bool TryParseInstant(string? text, out DateTimeOffset instant)
    {
        instant = default;
        if (string.IsNullOrEmpty(text) || text.Length > MaximumInstantLength)
        {
            return false;
        }

        var match = InstantPattern.Match(text);
        if (!match.Success ||
            (match.Groups["fraction"].Success && match.Groups["fraction"].Value.Any(digit => digit != '0')) ||
            !DateTimeOffset.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        instant = parsed.ToUniversalTime();
        return true;
    }

    /// <summary>The shape rules that do not depend on the clock: UTC, whole seconds, from before to, bounded span.</summary>
    public static bool IsWellFormed(OrderServiceWindow? window) =>
        window is not null &&
        window.From.Offset == TimeSpan.Zero &&
        window.To.Offset == TimeSpan.Zero &&
        window.From.UtcTicks % TimeSpan.TicksPerSecond == 0 &&
        window.To.UtcTicks % TimeSpan.TicksPerSecond == 0 &&
        window.From < window.To &&
        window.To - window.From <= MaximumSpan;

    /// <summary>The window has not ended, does not start before now minus the tolerance and is not too far ahead.</summary>
    public static bool IsWithinServerTime(OrderServiceWindow window, DateTimeOffset serverNow) =>
        window.From >= serverNow - ClockTolerance &&
        window.To > serverNow &&
        window.From <= serverNow + MaximumLeadTime;

    /// <summary>The canonical UTC text of a bound, used in the idempotency request hash.</summary>
    public static string Format(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
