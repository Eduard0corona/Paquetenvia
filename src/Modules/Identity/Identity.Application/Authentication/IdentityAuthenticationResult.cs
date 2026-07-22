using System.Collections.Immutable;

namespace Identity.Application.Authentication;

public sealed class NormalizedIdentity
{
    public NormalizedIdentity(
        string subject,
        NormalizedIdentityStatus status,
        bool mfaSatisfied,
        IEnumerable<NormalizedOrganizationMembership> memberships)
    {
        Subject = subject;
        Status = status;
        MfaSatisfied = mfaSatisfied;
        Memberships = memberships.ToImmutableArray();
    }

    public string Subject { get; }

    public NormalizedIdentityStatus Status { get; }

    public bool MfaSatisfied { get; }

    public ImmutableArray<NormalizedOrganizationMembership> Memberships { get; }
}

public enum NormalizedIdentityStatus
{
    Active,
    Suspended,
    Disabled,
}

public enum NormalizedMembershipStatus
{
    Active,
    Suspended,
    Revoked,
}

public enum NormalizedOrganizationRole
{
    PlatformAdmin,
    Dispatcher,
    Finance,
    AllyAdmin,
    AllyOperator,
    BusinessAdmin,
    BusinessOperator,
    Driver,
    Viewer,
}

public sealed record NormalizedOrganizationMembership(
    Guid OrganizationId,
    NormalizedOrganizationRole Role,
    NormalizedMembershipStatus Status);

public static class NormalizedIdentityContractValues
{
    public static string ToContractValue(this NormalizedIdentityStatus status) => status switch
    {
        NormalizedIdentityStatus.Active => "ACTIVE",
        NormalizedIdentityStatus.Suspended => "SUSPENDED",
        NormalizedIdentityStatus.Disabled => "DISABLED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown identity status."),
    };

    public static string ToContractValue(this NormalizedMembershipStatus status) => status switch
    {
        NormalizedMembershipStatus.Active => "ACTIVE",
        NormalizedMembershipStatus.Suspended => "SUSPENDED",
        NormalizedMembershipStatus.Revoked => "REVOKED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown membership status."),
    };

    public static string ToContractValue(this NormalizedOrganizationRole role) => role switch
    {
        NormalizedOrganizationRole.PlatformAdmin => "PLATFORM_ADMIN",
        NormalizedOrganizationRole.Dispatcher => "DISPATCHER",
        NormalizedOrganizationRole.Finance => "FINANCE",
        NormalizedOrganizationRole.AllyAdmin => "ALLY_ADMIN",
        NormalizedOrganizationRole.AllyOperator => "ALLY_OPERATOR",
        NormalizedOrganizationRole.BusinessAdmin => "BUSINESS_ADMIN",
        NormalizedOrganizationRole.BusinessOperator => "BUSINESS_OPERATOR",
        NormalizedOrganizationRole.Driver => "DRIVER",
        NormalizedOrganizationRole.Viewer => "VIEWER",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown organization role."),
    };
}

public sealed class IdentityAuthenticationResult
{
    private IdentityAuthenticationResult(bool isValid, NormalizedIdentity? identity)
    {
        IsValid = isValid;
        Identity = identity;
    }

    public bool IsValid { get; }

    public NormalizedIdentity? Identity { get; }

    public static IdentityAuthenticationResult Invalid { get; } = new(false, null);

    public static IdentityAuthenticationResult Success(NormalizedIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new IdentityAuthenticationResult(true, identity);
    }
}
