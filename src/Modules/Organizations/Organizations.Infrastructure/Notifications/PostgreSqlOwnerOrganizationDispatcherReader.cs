using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;
using Organizations.Application.Notifications;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Organizations.Infrastructure.Notifications;

/// <summary>SCL-007: the Worker's one <c>paqueteria_worker</c> pool, shared with every other Worker module.</summary>
internal sealed class OrganizationsNotificationConnectionFactory(
    [FromKeyedServices(RuntimeDatabaseRole.Worker)] RuntimeDataSource pool)
{
    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        pool.OpenConnectionAsync(cancellationToken);
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
        services.AddRuntimeDataSource(configuration, RuntimeDatabaseRole.Worker);
        services.TryAddSingleton<OrganizationsNotificationConnectionFactory>();
        services.AddSingleton<IOwnerOrganizationDispatcherReader, PostgreSqlOwnerOrganizationDispatcherReader>();
        return services;
    }
}
