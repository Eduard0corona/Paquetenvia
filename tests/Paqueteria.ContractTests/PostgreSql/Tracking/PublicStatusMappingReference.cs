namespace Paqueteria.ContractTests.PostgreSql.Tracking;

internal sealed class PublicStatusMappingException(string status)
    : InvalidOperationException($"Internal order status '{status}' has no public mapping.");

internal static class PublicStatusMappingReference
{
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DRAFT"] = "CREATED",
        ["CONFIRMED"] = "CREATED",
        ["READY_FOR_PICKUP"] = "SCHEDULED",
        ["ASSIGNED"] = "SCHEDULED",
        ["AT_PICKUP"] = "SCHEDULED",
        ["PICKED_UP"] = "IN_TRANSIT",
        ["IN_TRANSIT"] = "IN_TRANSIT",
        ["DELIVERING"] = "OUT_FOR_DELIVERY",
        ["FAILED_ATTEMPT"] = "DELIVERY_EXCEPTION",
        ["RESCHEDULED"] = "SCHEDULED",
        ["RETURNING"] = "RETURNING",
        ["RETURNED"] = "RETURNED",
        ["DELIVERED"] = "DELIVERED",
        ["CLOSED"] = "DELIVERED",
        ["CLAIM_OPEN"] = "DELIVERED",
        ["CLAIM_RESOLVED"] = "DELIVERED",
        ["CANCELLED"] = "CANCELLED",
    };

    public static string Map(string status) => All.TryGetValue(status, out var publicStatus)
        ? publicStatus
        : throw new PublicStatusMappingException(status);
}
