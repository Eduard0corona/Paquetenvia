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
    string? RequestId);

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
