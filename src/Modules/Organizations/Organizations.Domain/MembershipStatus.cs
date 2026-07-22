namespace Organizations.Domain;

public enum MembershipStatus
{
    Active,
    Suspended,
    Revoked,
}

public static class MembershipStatusExtensions
{
    public static string ToContractValue(this MembershipStatus value) => value switch
    {
        MembershipStatus.Active => "ACTIVE",
        MembershipStatus.Suspended => "SUSPENDED",
        MembershipStatus.Revoked => "REVOKED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown membership status."),
    };
}
