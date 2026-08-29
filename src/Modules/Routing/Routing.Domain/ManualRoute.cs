namespace Routing.Domain;

public enum RouteStatus
{
    Draft,
    Planned,
    Active,
    Completed,
    Cancelled,
}

public enum RouteStopType
{
    Pickup,
    Delivery,
    Return,
}

public enum RouteStopStatus
{
    Pending,
    Arrived,
    Completed,
    Failed,
    Skipped,
}

public static class RoutingContractValues
{
    public static string ToContractValue(this RouteStatus value) => value switch
    {
        RouteStatus.Draft => "DRAFT",
        RouteStatus.Planned => "PLANNED",
        RouteStatus.Active => "ACTIVE",
        RouteStatus.Completed => "COMPLETED",
        RouteStatus.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this RouteStopType value) => value switch
    {
        RouteStopType.Pickup => "PICKUP",
        RouteStopType.Delivery => "DELIVERY",
        RouteStopType.Return => "RETURN",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this RouteStopStatus value) => value switch
    {
        RouteStopStatus.Pending => "PENDING",
        RouteStopStatus.Arrived => "ARRIVED",
        RouteStopStatus.Completed => "COMPLETED",
        RouteStopStatus.Failed => "FAILED",
        RouteStopStatus.Skipped => "SKIPPED",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}

public sealed record ManualRoute(
    Guid Id,
    Guid OrganizationId,
    Guid DriverId,
    Guid CityId,
    Guid? ServiceAreaId,
    RouteStatus Status,
    int Version,
    DateOnly? ScheduledFor)
{
    public bool IsDraftAt(int expectedVersion) =>
        Status == RouteStatus.Draft && Version == expectedVersion;
}

public sealed record ManualRouteStop(
    Guid Id,
    Guid RouteId,
    Guid OrderId,
    int Sequence,
    RouteStopType StopType,
    RouteStopStatus Status);

public static class ManualRoutePolicy
{
    public static bool CanCreate(
        Guid organizationId,
        Guid driverId,
        Guid cityId,
        Guid? serviceAreaId) =>
        organizationId != Guid.Empty &&
        driverId != Guid.Empty &&
        cityId != Guid.Empty &&
        serviceAreaId != Guid.Empty;

    public static bool CanLinkAssignment(
        string assignmentType,
        string assignmentStatus,
        Guid assignmentDriverId,
        Guid routeDriverId,
        Guid? currentRouteId) =>
        assignmentType == "OWN" &&
        assignmentStatus is "ACCEPTED" or "ACTIVE" &&
        assignmentDriverId == routeDriverId &&
        currentRouteId is null;
}
