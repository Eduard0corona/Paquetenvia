using Identity.Application.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Identity.Infrastructure.Notifications;

/// <summary>SCL-007: the Worker's one <c>paqueteria_worker</c> pool, shared with every other Worker module.</summary>
internal sealed class IdentityNotificationConnectionFactory(
    [FromKeyedServices(RuntimeDatabaseRole.Worker)] RuntimeDataSource pool)
{
    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        pool.OpenConnectionAsync(cancellationToken);
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
        services.AddRuntimeDataSource(configuration, RuntimeDatabaseRole.Worker);
        services.TryAddSingleton<IdentityNotificationConnectionFactory>();
        services.AddSingleton<IActiveNotificationRecipientReader, PostgreSqlActiveNotificationRecipientReader>();
        return services;
    }
}
