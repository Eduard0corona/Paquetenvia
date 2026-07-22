namespace Orders.Application.Tracking;

public enum PublicOrderStatus
{
    Created,
    Scheduled,
    InTransit,
    OutForDelivery,
    DeliveryException,
    Delivered,
    Returning,
    Returned,
    Cancelled,
}

public sealed class PublicStatusMappingException(string internalStatus)
    : Exception("The internal order status has no public mapping.")
{
    public string InternalStatus { get; } = internalStatus;
}

public sealed class PublicOrderStatusPolicy
{
    public PublicOrderStatus Map(string internalStatus) => internalStatus switch
    {
        "DRAFT" => PublicOrderStatus.Created,
        "CONFIRMED" => PublicOrderStatus.Created,
        "READY_FOR_PICKUP" => PublicOrderStatus.Scheduled,
        "ASSIGNED" => PublicOrderStatus.Scheduled,
        "AT_PICKUP" => PublicOrderStatus.Scheduled,
        "PICKED_UP" => PublicOrderStatus.InTransit,
        "IN_TRANSIT" => PublicOrderStatus.InTransit,
        "DELIVERING" => PublicOrderStatus.OutForDelivery,
        "FAILED_ATTEMPT" => PublicOrderStatus.DeliveryException,
        "RESCHEDULED" => PublicOrderStatus.Scheduled,
        "RETURNING" => PublicOrderStatus.Returning,
        "RETURNED" => PublicOrderStatus.Returned,
        "DELIVERED" => PublicOrderStatus.Delivered,
        "CLOSED" => PublicOrderStatus.Delivered,
        "CLAIM_OPEN" => PublicOrderStatus.Delivered,
        "CLAIM_RESOLVED" => PublicOrderStatus.Delivered,
        "CANCELLED" => PublicOrderStatus.Cancelled,
        _ => throw new PublicStatusMappingException(internalStatus),
    };

    public static string ToContractValue(PublicOrderStatus status) => status switch
    {
        PublicOrderStatus.Created => "CREATED",
        PublicOrderStatus.Scheduled => "SCHEDULED",
        PublicOrderStatus.InTransit => "IN_TRANSIT",
        PublicOrderStatus.OutForDelivery => "OUT_FOR_DELIVERY",
        PublicOrderStatus.DeliveryException => "DELIVERY_EXCEPTION",
        PublicOrderStatus.Delivered => "DELIVERED",
        PublicOrderStatus.Returning => "RETURNING",
        PublicOrderStatus.Returned => "RETURNED",
        PublicOrderStatus.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown public status."),
    };
}
