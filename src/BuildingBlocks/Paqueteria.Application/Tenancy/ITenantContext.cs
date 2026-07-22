using System.Collections.Immutable;

namespace Paqueteria.Application.Tenancy;

public interface ITenantContext
{
    bool IsSelected { get; }

    Guid OrganizationId { get; }
}

public sealed record TenantDatabaseExecutionContext
{
    public TenantDatabaseExecutionContext(Guid userId, IEnumerable<Guid> organizationIds)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty user id is required.", nameof(userId));
        }

        ArgumentNullException.ThrowIfNull(organizationIds);
        UserId = userId;
        OrganizationIds = organizationIds
            .Where(value => value != Guid.Empty)
            .Distinct()
            .Order()
            .ToImmutableArray();
    }

    public Guid UserId { get; }

    public ImmutableArray<Guid> OrganizationIds { get; }
}

public sealed class TenantTransactionRequiredException(string message) : InvalidOperationException(message);
