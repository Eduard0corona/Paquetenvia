using Microsoft.EntityFrameworkCore;
using Paqueteria.Application.Tenancy;

namespace Paqueteria.Infrastructure.Tenancy;

/// <summary>
/// One explicit tenant transaction: <c>BEGIN</c>, then the parameterized transaction-local
/// <c>set_config(..., true)</c> of the user and the <c>uuid[]</c> organization context, then
/// <c>SET LOCAL ROLE</c> of the runtime role. <see cref="TenantTransactionContext{TDbContext}"/> runs as
/// <c>paqueteria_app</c> (API) and <see cref="WorkerTenantTransactionContext{TDbContext}"/> as
/// <c>paqueteria_worker</c> (Worker); both are <c>NOBYPASSRLS</c>, so FORCE RLS decides what is visible.
/// ORD-AUTO-CLOSE-2026-10-10 lets the ORD-002 transition run under either one without a second copy.
/// </summary>
public interface ITenantTransactionRunner<TDbContext>
    where TDbContext : DbContext
{
    Task<TResult> ExecuteAsync<TResult>(
        TenantDatabaseExecutionContext executionContext,
        Func<TDbContext, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);
}
