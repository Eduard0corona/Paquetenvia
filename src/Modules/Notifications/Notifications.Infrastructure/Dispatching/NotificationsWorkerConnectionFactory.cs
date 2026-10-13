using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Notifications.Infrastructure.Dispatching;

/// <summary>SCL-007: the Worker's one <c>paqueteria_worker</c> pool, shared with every other Worker module.</summary>
internal sealed class NotificationsWorkerConnectionFactory(
    [FromKeyedServices(RuntimeDatabaseRole.Worker)] RuntimeDataSource pool)
{
    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        pool.OpenConnectionAsync(cancellationToken);
}
