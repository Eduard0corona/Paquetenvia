using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Organizations.Application.OrganizationRoles;
using Organizations.Domain;
using Organizations.Infrastructure.Persistence;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Organizations.Infrastructure.OrganizationRoles;

/// <summary>
/// ORG-MY-ROLES-2026-10-12: reads the requesting person's own roles in one organization inside one explicit tenant
/// transaction (current user and that organization only, RLS as paqueteria_app), without writing anything. RLS also
/// lets the tenant read the organization's other members, so the query is filtered to the requesting user: no other
/// person's role is ever returned.
/// </summary>
public sealed class PostgreSqlMyOrganizationRolesReader(
    TenantTransactionContext<OrganizationsDbContext> transactionContext,
    ILogger<PostgreSqlMyOrganizationRolesReader> logger) : IMyOrganizationRolesReader
{
    public async Task<IReadOnlyList<string>> ReadAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty organization id is required.", nameof(organizationId));
        }

        var executionContext = new TenantDatabaseExecutionContext(userId, [organizationId]);
        try
        {
            return await transactionContext.ExecuteAsync(
                executionContext,
                async (dbContext, token) =>
                {
                    var roles = await dbContext.Memberships
                        .AsNoTracking()
                        .Where(membership =>
                            membership.UserId == userId &&
                            membership.OrganizationId == organizationId &&
                            membership.Status == MembershipStatus.Active &&
                            dbContext.Organizations.Any(organization =>
                                organization.Id == organizationId &&
                                organization.Status == OrganizationStatus.Active))
                        .Select(membership => membership.Role)
                        .ToListAsync(token);
                    return MyOrganizationRoles.FromRoles(roles);
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Organization roles lookup failed.");
            throw new MyOrganizationRolesUnavailableException("Organization roles are unavailable.", exception);
        }
    }
}
