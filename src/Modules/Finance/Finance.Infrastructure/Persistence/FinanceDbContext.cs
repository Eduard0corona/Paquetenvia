using Microsoft.EntityFrameworkCore;
using Paqueteria.Infrastructure.Tenancy;

namespace Finance.Infrastructure.Persistence;

public sealed class FinanceDbContext(
    DbContextOptions<FinanceDbContext> options,
    TenantDatabaseExecutionState tenantState) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.HasDefaultSchema("finance");

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
                "Finance writes require an explicit tenant transaction.");
        }
    }
}
