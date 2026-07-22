namespace Organizations.Domain;

public enum OrganizationStatus
{
    Active,
    Suspended,
    Closed,
}

public static class OrganizationStatusExtensions
{
    public static string ToContractValue(this OrganizationStatus value) => value switch
    {
        OrganizationStatus.Active => "ACTIVE",
        OrganizationStatus.Suspended => "SUSPENDED",
        OrganizationStatus.Closed => "CLOSED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown organization status."),
    };
}
