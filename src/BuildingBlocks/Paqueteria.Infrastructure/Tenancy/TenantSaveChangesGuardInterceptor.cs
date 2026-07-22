using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Paqueteria.Application.Tenancy;

namespace Paqueteria.Infrastructure.Tenancy;

public sealed class TenantSaveChangesGuardInterceptor(TenantDatabaseExecutionState state)
    : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        EnsureTenantTransaction(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        EnsureTenantTransaction(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void EnsureTenantTransaction(DbContext? context)
    {
        if (context?.Database.CurrentTransaction is null || !state.IsApplied)
        {
            throw new TenantTransactionRequiredException(
                "SaveChanges requires an explicit tenant transaction with transaction-local context.");
        }
    }
}
