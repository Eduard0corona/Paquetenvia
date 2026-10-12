using Npgsql;
using Realtime.Application.Configuration;

namespace Realtime.Infrastructure.Dispatching;

/// <summary>
/// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the dedicated database credential of the in-process realtime outbox
/// dispatchers (<c>ConnectionStrings:PaqueteriaRealtime</c>). Its login is a member of <c>paqueteria_realtime</c> only,
/// which every transaction assumes with <c>SET LOCAL ROLE</c>; it is never the Worker credential.
/// </summary>
internal sealed class RealtimeDispatcherConnectionFactory : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _dataSource;

    public RealtimeDispatcherConnectionFactory(string connectionString)
    {
        _dataSource = new(() => NpgsqlDataSource.Create(
            !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new InvalidOperationException(
                    $"ConnectionStrings:{OutboxDispatcherOptions.ConnectionStringName} is required when the realtime outbox dispatcher is enabled.")));
    }

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        _dataSource.Value.OpenConnectionAsync(cancellationToken);

    public ValueTask DisposeAsync() =>
        _dataSource.IsValueCreated ? _dataSource.Value.DisposeAsync() : ValueTask.CompletedTask;
}
