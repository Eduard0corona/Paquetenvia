using Identity.Application.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Identity.Infrastructure.Notifications;

internal sealed class IdentityNotificationConnectionFactory(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(
        !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : "Host=127.0.0.1;Database=disabled;Username=disabled;Password=disabled");

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        _dataSource.OpenConnectionAsync(cancellationToken);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

internal sealed class PostgreSqlActiveNotificationRecipientReader(
    IdentityNotificationConnectionFactory connections) : IActiveNotificationRecipientReader
{
    public async Task<IReadOnlyList<Guid>> ReadActiveUserIdsAsync(
        IReadOnlyCollection<Guid> requestedUserIds,
        CancellationToken cancellationToken)
    {
        if (requestedUserIds.Count > 500 || requestedUserIds.Any(static id => id == Guid.Empty))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedUserIds));
        }

        if (requestedUserIds.Count == 0)
        {
            return [];
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker;", connection, transaction))
        {
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(
            "SELECT user_id FROM security.read_active_notification_users(@requested_user_ids);",
            connection,
            transaction);
        command.Parameters.Add(new("requested_user_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = requestedUserIds.Distinct().Order().ToArray(),
        });
        var result = new List<Guid>(requestedUserIds.Count);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(reader.GetGuid(0));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}

public static class NotificationAudienceDependencyInjection
{
    public static IServiceCollection AddIdentityNotificationAudienceReader(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(_ => new IdentityNotificationConnectionFactory(
            configuration.GetConnectionString("PaqueteriaWorker") ?? string.Empty));
        services.AddSingleton<IActiveNotificationRecipientReader, PostgreSqlActiveNotificationRecipientReader>();
        return services;
    }
}
