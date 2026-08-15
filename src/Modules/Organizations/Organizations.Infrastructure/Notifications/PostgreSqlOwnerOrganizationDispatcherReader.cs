using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Organizations.Application.Notifications;

namespace Organizations.Infrastructure.Notifications;

internal sealed class OrganizationsNotificationConnectionFactory(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(
        !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : "Host=127.0.0.1;Database=disabled;Username=disabled;Password=disabled");

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        _dataSource.OpenConnectionAsync(cancellationToken);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

internal sealed class PostgreSqlOwnerOrganizationDispatcherReader(
    OrganizationsNotificationConnectionFactory connections) : IOwnerOrganizationDispatcherReader
{
    public async Task<IReadOnlyList<Guid>> ReadActiveDispatcherUserIdsAsync(
        Guid ownerOrganizationId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 501);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker;", connection, transaction))
        {
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(
            "SELECT user_id FROM security.read_owner_dispatcher_ids(@owner_org_id,@limit);",
            connection,
            transaction);
        command.Parameters.Add(new("owner_org_id", NpgsqlDbType.Uuid) { Value = ownerOrganizationId });
        command.Parameters.Add(new("limit", NpgsqlDbType.Integer) { Value = limit });
        var result = new List<Guid>(limit);
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
    public static IServiceCollection AddOrganizationsNotificationAudienceReader(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(_ => new OrganizationsNotificationConnectionFactory(
            configuration.GetConnectionString("PaqueteriaWorker") ?? string.Empty));
        services.AddSingleton<IOwnerOrganizationDispatcherReader, PostgreSqlOwnerOrganizationDispatcherReader>();
        return services;
    }
}
