using Microsoft.EntityFrameworkCore;
using Paqueteria.Infrastructure.Tenancy;

namespace Routing.Infrastructure.Persistence;

public sealed class RoutingDbContext(
    DbContextOptions<RoutingDbContext> options,
    TenantDatabaseExecutionState tenantState) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.HasDefaultSchema("routes");

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureTenantContext();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureTenantContext();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnsureTenantContext()
    {
        if (!tenantState.IsApplied)
        {
            throw new Paqueteria.Application.Tenancy.TenantTransactionRequiredException(
                "Routing writes require an explicit tenant transaction.");
        }
    }
}
