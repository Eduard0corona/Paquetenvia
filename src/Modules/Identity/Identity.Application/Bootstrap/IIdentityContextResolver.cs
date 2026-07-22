using System.Collections.Immutable;

namespace Identity.Application.Bootstrap;

public enum IdentityContextStatus
{
    Active,
}

public enum OrganizationRole
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

public sealed record IdentityContextMembership(
    Guid OrganizationId,
    OrganizationRole Role,
    bool IsDefault);

public sealed record ResolvedIdentityContext
{
    public ResolvedIdentityContext(
        Guid userId,
        IdentityContextStatus status,
        IEnumerable<IdentityContextMembership> memberships)
    {
        UserId = userId;
        Status = status;
        Memberships = memberships.ToImmutableArray();
    }

    public Guid UserId { get; }

    public IdentityContextStatus Status { get; }

    public ImmutableArray<IdentityContextMembership> Memberships { get; }
}

public sealed class IdentityContextResolution
{
    private IdentityContextResolution(ResolvedIdentityContext? context) => Context = context;

    public bool IsResolved => Context is not null;

    public ResolvedIdentityContext? Context { get; }

    public static IdentityContextResolution NoAuthorizedContext { get; } = new(null);

    public static IdentityContextResolution Resolved(ResolvedIdentityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new IdentityContextResolution(context);
    }
}

public interface IIdentityContextResolver
{
    ValueTask<IdentityContextResolution> ResolveAsync(
        string identitySubject,
        CancellationToken cancellationToken);
}

public sealed class IdentityContextInfrastructureException : Exception
{
    public IdentityContextInfrastructureException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public static class IdentityContextContractValues
{
    public static string ToContractValue(this IdentityContextStatus status) => status switch
    {
        IdentityContextStatus.Active => "ACTIVE",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown identity status."),
    };

    public static string ToContractValue(this OrganizationRole role) => role switch
    {
        OrganizationRole.PlatformAdmin => "PLATFORM_ADMIN",
        OrganizationRole.Dispatcher => "DISPATCHER",
        OrganizationRole.Finance => "FINANCE",
        OrganizationRole.AllyAdmin => "ALLY_ADMIN",
        OrganizationRole.AllyOperator => "ALLY_OPERATOR",
        OrganizationRole.BusinessAdmin => "BUSINESS_ADMIN",
        OrganizationRole.BusinessOperator => "BUSINESS_OPERATOR",
        OrganizationRole.Driver => "DRIVER",
        OrganizationRole.Viewer => "VIEWER",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown organization role."),
    };
}
