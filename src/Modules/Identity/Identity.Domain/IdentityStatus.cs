namespace Identity.Domain;

public enum IdentityStatus
{
    Active,
    Suspended,
    Disabled,
}

public static class IdentityStatusExtensions
{
    public static string ToContractValue(this IdentityStatus status) => status switch
    {
        IdentityStatus.Active => "ACTIVE",
        IdentityStatus.Suspended => "SUSPENDED",
        IdentityStatus.Disabled => "DISABLED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown identity status."),
    };
}
