using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Organizations.Application.OrganizationContexts;
using Organizations.Infrastructure.Persistence;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;
using Paqueteria.Domain.Tenancy;

namespace Organizations.Infrastructure.OrganizationContexts;

public sealed class PostgreSqlOrganizationContextReader(
    TenantTransactionContext<OrganizationsDbContext> transactionContext,
    ILogger<PostgreSqlOrganizationContextReader> logger) : IOrganizationContextReader
{
    public async Task<IReadOnlyList<OrganizationContextResponse>> ReadAsync(
        Guid userId,
        IReadOnlyCollection<AuthorizedOrganizationMembership> memberships,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        if (memberships.Count == 0)
        {
            return [];
        }

        var membershipByOrganization = memberships
            .GroupBy(value => value.OrganizationId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.IsDefault).First());
        var executionContext = new TenantDatabaseExecutionContext(userId, membershipByOrganization.Keys);

        try
        {
            return await transactionContext.ExecuteAsync<IReadOnlyList<OrganizationContextResponse>>(
                executionContext,
                async (dbContext, token) =>
                {
                    var names = await dbContext.Organizations
                        .AsNoTracking()
                        .OrderBy(organization => organization.DisplayName)
                        .Select(organization => new { organization.Id, organization.DisplayName })
                        .ToListAsync(token);

                    return names.Select(organization =>
                    {
                        var membership = membershipByOrganization[organization.Id];
                        return new OrganizationContextResponse(
                            organization.Id,
                            organization.DisplayName,
                            membership.Role.ToContractValue(),
                            membership.IsDefault);
                    }).ToArray();
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Organization context lookup failed.");
            throw new OrganizationContextUnavailableException("Organization contexts are unavailable.", exception);
        }
    }
}
