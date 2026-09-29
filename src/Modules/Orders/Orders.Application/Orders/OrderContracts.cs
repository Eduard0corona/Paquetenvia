using System.Collections.Immutable;
using Orders.Domain;

namespace Orders.Application.Orders;

public sealed record OrderAcceptanceInput(
    string TermsVersion,
    string PrivacyVersion,
    DateTimeOffset AcceptedAt,
    string AcceptanceChannel);

public sealed record CreateOrderCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid QuoteId,
    string PayerType,
    OrderAcceptanceInput Acceptance,
    string? RequestId,
    long CodExpectedCents = 0);

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

    Task<OrderPageResult> ListAsync(
        Guid actorId,
        Guid organizationId,
        string? status,
        Guid? ownerOrganizationId,
        string? cursor,
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
    /// A negative amount is never a valid expectation.
    /// </summary>
    public static bool IsCodExpectedCents(long value) => value >= 0;

    /// <summary>
    /// Reads a COD expectation written as text, on a CSV-001 row or as the raw JSON number of
    /// <c>POST /orders</c>. Only a plain run of ASCII digits that fits int64 is accepted: signs, decimal points,
    /// exponents, thousands separators, currency symbols and whitespace are all rejected, so no fractional or
    /// floating-point value can ever become an amount of cents.
    /// </summary>
    public static bool TryParseCodExpectedCents(string? text, out long cents)
    {
        cents = 0;
        return !string.IsNullOrEmpty(text) &&
            text.Length <= MaximumCodExpectedCentsDigits &&
            text.All(char.IsAsciiDigit) &&
            long.TryParse(
                text,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out cents) &&
            IsCodExpectedCents(cents);
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
