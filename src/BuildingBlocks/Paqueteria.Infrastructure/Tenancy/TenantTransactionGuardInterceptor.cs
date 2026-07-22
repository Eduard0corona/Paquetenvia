using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Paqueteria.Application.Tenancy;

namespace Paqueteria.Infrastructure.Tenancy;

public sealed class TenantTransactionGuardInterceptor(TenantDatabaseExecutionState state)
    : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        EnsureTenantTransaction(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        EnsureTenantTransaction(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        EnsureTenantTransaction(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        EnsureTenantTransaction(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        EnsureTenantTransaction(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
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
                "Tenant database operations require an explicit transaction with transaction-local context.");
        }
    }
}
