using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Realtime.Infrastructure.Dispatching;

/// <summary>
/// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the dedicated database credential of the in-process realtime outbox
/// dispatchers (<c>ConnectionStrings:PaqueteriaRealtime</c>). Its login is a member of <c>paqueteria_realtime</c> only,
/// which every transaction assumes with <c>SET LOCAL ROLE</c>; it is never the Worker credential. SCL-007: this is the
/// API's one <c>paqueteria_realtime</c> pool, built on first use and shared by both lanes, the evidence reader and the
/// readiness check.
/// </summary>
internal sealed class RealtimeDispatcherConnectionFactory(
    [FromKeyedServices(RuntimeDatabaseRole.Realtime)] RuntimeDataSource pool)
{
    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        pool.OpenConnectionAsync(cancellationToken);
}
