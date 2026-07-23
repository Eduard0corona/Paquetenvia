namespace Orders.Domain;

public enum PayerType
{
    Sender,
    Recipient,
    BusinessAccount,
}

public enum OrderStatus
{
    Draft,
    Confirmed,
    ReadyForPickup,
    Assigned,
    AtPickup,
    PickedUp,
    InTransit,
    Delivering,
    FailedAttempt,
    Rescheduled,
    Returning,
    Returned,
    Delivered,
    Closed,
    ClaimOpen,
    ClaimResolved,
    Cancelled,
}

public static class OrderContractValues
{
    public static string ToContractValue(this PayerType value) => value switch
    {
        PayerType.Sender => "SENDER",
        PayerType.Recipient => "RECIPIENT",
        PayerType.BusinessAccount => "BUSINESS_ACCOUNT",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown payer type."),
    };

    public static string ToContractValue(this OrderStatus value) => value switch
    {
        OrderStatus.Draft => "DRAFT",
        OrderStatus.Confirmed => "CONFIRMED",
        OrderStatus.ReadyForPickup => "READY_FOR_PICKUP",
        OrderStatus.Assigned => "ASSIGNED",
        OrderStatus.AtPickup => "AT_PICKUP",
        OrderStatus.PickedUp => "PICKED_UP",
        OrderStatus.InTransit => "IN_TRANSIT",
        OrderStatus.Delivering => "DELIVERING",
        OrderStatus.FailedAttempt => "FAILED_ATTEMPT",
        OrderStatus.Rescheduled => "RESCHEDULED",
        OrderStatus.Returning => "RETURNING",
        OrderStatus.Returned => "RETURNED",
        OrderStatus.Delivered => "DELIVERED",
        OrderStatus.Closed => "CLOSED",
        OrderStatus.ClaimOpen => "CLAIM_OPEN",
        OrderStatus.ClaimResolved => "CLAIM_RESOLVED",
        OrderStatus.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown order status."),
    };

    public static bool TryParseOrderStatus(string? value, out OrderStatus status)
    {
        status = value switch
        {
            "DRAFT" => OrderStatus.Draft,
            "CONFIRMED" => OrderStatus.Confirmed,
            "READY_FOR_PICKUP" => OrderStatus.ReadyForPickup,
            "ASSIGNED" => OrderStatus.Assigned,
            "AT_PICKUP" => OrderStatus.AtPickup,
            "PICKED_UP" => OrderStatus.PickedUp,
            "IN_TRANSIT" => OrderStatus.InTransit,
            "DELIVERING" => OrderStatus.Delivering,
            "FAILED_ATTEMPT" => OrderStatus.FailedAttempt,
            "RESCHEDULED" => OrderStatus.Rescheduled,
            "RETURNING" => OrderStatus.Returning,
            "RETURNED" => OrderStatus.Returned,
            "DELIVERED" => OrderStatus.Delivered,
            "CLOSED" => OrderStatus.Closed,
            "CLAIM_OPEN" => OrderStatus.ClaimOpen,
            "CLAIM_RESOLVED" => OrderStatus.ClaimResolved,
            "CANCELLED" => OrderStatus.Cancelled,
            _ => default,
        };

        return value is not null && Enum.IsDefined(status) &&
            string.Equals(status.ToContractValue(), value, StringComparison.Ordinal);
    }
}
