namespace Paqueteria.Domain.Tenancy;

public static class OperationsRolePolicy
{
    public static bool IsAllowed(OrganizationRole role, bool mfaSatisfied) => role switch
    {
        OrganizationRole.PlatformAdmin => mfaSatisfied,
        OrganizationRole.Dispatcher => true,
        _ => false,
    };
}
