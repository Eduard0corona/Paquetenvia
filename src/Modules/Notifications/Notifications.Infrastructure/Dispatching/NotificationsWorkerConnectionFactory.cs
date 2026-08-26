using Npgsql;

namespace Notifications.Infrastructure.Dispatching;

internal sealed class NotificationsWorkerConnectionFactory(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(
        !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : "Host=127.0.0.1;Database=disabled;Username=disabled;Password=disabled");

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        _dataSource.OpenConnectionAsync(cancellationToken);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
