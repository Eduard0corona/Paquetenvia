namespace Identity.Domain;

public enum MembershipStatus
{
    Active,
    Suspended,
    Revoked,
}

public static class MembershipStatusExtensions
{
    public static string ToContractValue(this MembershipStatus status) => status switch
    {
        MembershipStatus.Active => "ACTIVE",
        MembershipStatus.Suspended => "SUSPENDED",
        MembershipStatus.Revoked => "REVOKED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown membership status."),
    };
}
