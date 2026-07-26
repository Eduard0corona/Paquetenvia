using Npgsql;

namespace Realtime.Infrastructure.Dispatching;

internal sealed class RealtimeWorkerConnectionFactory : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _dataSource;

    public RealtimeWorkerConnectionFactory(string connectionString)
    {
        _dataSource = new(() => NpgsqlDataSource.Create(
            !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new InvalidOperationException(
                    "ConnectionStrings:PaqueteriaWorker is required when the realtime outbox dispatcher is enabled.")));
    }

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        _dataSource.Value.OpenConnectionAsync(cancellationToken);

    public ValueTask DisposeAsync() =>
        _dataSource.IsValueCreated ? _dataSource.Value.DisposeAsync() : ValueTask.CompletedTask;
}
