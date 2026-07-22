namespace Paqueteria.Domain.Tenancy;

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

public static class OrganizationRoleExtensions
{
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
