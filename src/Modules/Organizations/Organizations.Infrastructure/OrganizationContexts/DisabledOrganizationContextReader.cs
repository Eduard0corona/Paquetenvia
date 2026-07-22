using Organizations.Application.OrganizationContexts;

namespace Organizations.Infrastructure.OrganizationContexts;

public sealed class DisabledOrganizationContextReader : IOrganizationContextReader
{
    public Task<IReadOnlyList<OrganizationContextResponse>> ReadAsync(
        Guid userId,
        IReadOnlyCollection<AuthorizedOrganizationMembership> memberships,
        CancellationToken cancellationToken) =>
        throw new OrganizationContextUnavailableException("PostgreSQL tenancy is disabled.");
}
