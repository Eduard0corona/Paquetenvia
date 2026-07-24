namespace Dispatch.Domain;

public enum DriverStopType
{
    Pickup,
    Delivery,
    Return,
}

public sealed record DriverStopProjection(
    bool Included,
    DriverStopType StopType,
    bool UseOriginAddress)
{
    public static DriverStopProjection Excluded { get; } = new(false, default, false);
}

public static class DriverStopPolicy
{
    public static DriverStopProjection Project(string status, bool custodyAcquired) => status switch
    {
        "ASSIGNED" or "AT_PICKUP" => Pickup(),
        "PICKED_UP" or "IN_TRANSIT" or "DELIVERING" => Delivery(),
        "RETURNING" => Return(),
        "FAILED_ATTEMPT" or "RESCHEDULED" => custodyAcquired ? Delivery() : Pickup(),
        _ => DriverStopProjection.Excluded,
    };

    public static string ToContractValue(this DriverStopType value) => value switch
    {
        DriverStopType.Pickup => "PICKUP",
        DriverStopType.Delivery => "DELIVERY",
        DriverStopType.Return => "RETURN",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static DriverStopProjection Pickup() => new(true, DriverStopType.Pickup, true);
    private static DriverStopProjection Delivery() => new(true, DriverStopType.Delivery, false);
    private static DriverStopProjection Return() => new(true, DriverStopType.Return, true);
}
