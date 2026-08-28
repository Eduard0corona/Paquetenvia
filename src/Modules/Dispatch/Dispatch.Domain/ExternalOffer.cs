namespace Dispatch.Domain;

public enum ExternalOfferStatus
{
    Open,
    Accepted,
    Expired,
    Cancelled,
}

public static class ExternalOfferContractValues
{
    public static string ToContractValue(this ExternalOfferStatus value) => value switch
    {
        ExternalOfferStatus.Open => "OPEN",
        ExternalOfferStatus.Accepted => "ACCEPTED",
        ExternalOfferStatus.Expired => "EXPIRED",
        ExternalOfferStatus.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}

public static class ExternalOfferPolicy
{
    public static bool CanCreate(string orderStatus, DateTimeOffset expiresAt, DateTimeOffset now) =>
        orderStatus is "READY_FOR_PICKUP" or "RESCHEDULED" &&
        expiresAt.Offset == TimeSpan.Zero &&
        expiresAt > now;

    public static bool CanAccept(string offerStatus, DateTimeOffset expiresAt, DateTimeOffset now) =>
        offerStatus == "OPEN" && expiresAt > now;
}
