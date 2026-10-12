using Organizations.Application.OrganizationRoles;

namespace Organizations.Infrastructure.OrganizationRoles;

public sealed class DisabledMyOrganizationRolesReader : IMyOrganizationRolesReader
{
    public Task<IReadOnlyList<string>> ReadAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken) =>
        throw new MyOrganizationRolesUnavailableException("PostgreSQL tenancy is disabled.");
}
