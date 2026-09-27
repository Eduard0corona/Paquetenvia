namespace Organizations.Domain;

public enum OrganizationStatus
{
    Active,

    /// <summary>A self-registered ALLY waiting for a PLATFORM_ADMIN decision (REG-ALLY-APPROVAL-PATH); grants no access.</summary>
    PendingApproval,
    Suspended,
    Closed,
}

public static class OrganizationStatusExtensions
{
    public static string ToContractValue(this OrganizationStatus value) => value switch
    {
        OrganizationStatus.Active => "ACTIVE",
        OrganizationStatus.PendingApproval => "PENDING_APPROVAL",
        OrganizationStatus.Suspended => "SUSPENDED",
        OrganizationStatus.Closed => "CLOSED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown organization status."),
    };
}
