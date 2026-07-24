namespace Realtime.Application.Publishing;

public readonly record struct OperationsAudience
{
    private OperationsAudience(string groupName) => GroupName = groupName;

    internal string? GroupName { get; }

    public static OperationsAudience ForOrganization(Guid organizationId) =>
        new(RealtimeGroupNames.Organization(organizationId));

    public static OperationsAudience ForOrder(Guid orderId) =>
        new(RealtimeGroupNames.Order(orderId));
}

public readonly record struct DriverAudience
{
    private DriverAudience(string groupName) => GroupName = groupName;

    internal string? GroupName { get; }

    public static DriverAudience ForDriver(Guid driverId) =>
        new(RealtimeGroupNames.Driver(driverId));

    public static DriverAudience ForAssignment(Guid assignmentId) =>
        new(RealtimeGroupNames.Assignment(assignmentId));
}

public readonly record struct TrackingAudience
{
    private TrackingAudience(string groupName) => GroupName = groupName;

    internal string? GroupName { get; }

    public static TrackingAudience ForPublicOrder(string publicOrderId) =>
        new(RealtimeGroupNames.Tracking(publicOrderId));
}

internal static class RealtimeGroupNames
{
    internal static string Organization(Guid value) => Build("org", value);
    internal static string Order(Guid value) => Build("order", value);
    internal static string Driver(Guid value) => Build("driver", value);
    internal static string Assignment(Guid value) => Build("assignment", value);

    internal static string Tracking(string publicOrderId)
    {
        if (!Events.RealtimePublicOrderId.IsValid(publicOrderId))
        {
            throw new ArgumentException("A valid public order id is required.", nameof(publicOrderId));
        }

        return $"tracking:{publicOrderId}";
    }

    private static string Build(string prefix, Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A non-empty identifier is required.", nameof(value));
        }

        return $"{prefix}:{value:D}".ToLowerInvariant();
    }
}
