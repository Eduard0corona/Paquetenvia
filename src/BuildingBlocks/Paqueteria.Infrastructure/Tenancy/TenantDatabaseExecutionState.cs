using System.Collections.Immutable;

namespace Paqueteria.Infrastructure.Tenancy;

public sealed class TenantDatabaseExecutionState
{
    private Guid? userId;
    private ImmutableArray<Guid> organizationIds = [];

    public bool IsApplied { get; private set; }

    public Guid? UserId => userId;

    public IReadOnlyList<Guid> OrganizationIds => organizationIds;

    internal void Enter(Guid activeUserId, ImmutableArray<Guid> activeOrganizationIds)
    {
        if (IsApplied)
        {
            throw new InvalidOperationException("Nested tenant database contexts are not supported.");
        }

        userId = activeUserId;
        organizationIds = activeOrganizationIds;
        IsApplied = true;
    }

    internal void Exit()
    {
        IsApplied = false;
        userId = null;
        organizationIds = [];
    }
}
