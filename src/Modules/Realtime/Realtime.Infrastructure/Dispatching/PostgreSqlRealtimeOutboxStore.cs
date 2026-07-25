using Npgsql;
using NpgsqlTypes;
using Realtime.Application.Dispatching;

namespace Realtime.Infrastructure.Dispatching;

internal interface IRealtimeOutboxStore
{
    Task<IReadOnlyList<ClaimedBusinessOutboxMessage>> ClaimBusinessAsync(
        string workerId,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ClaimedLocationOutboxMessage>> ClaimLocationAsync(
        string workerId,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken);

    Task<bool> SettleBusinessAsync(
        Guid id,
        Guid leaseToken,
        string status,
        string? errorCode,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken);

    Task<bool> SettleLocationAsync(
        Guid id,
        Guid leaseToken,
        string status,
        string? errorCode,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken);

    Task<int> RequeueStaleBusinessAsync(
        int batchSize,
        int maximumAttempts,
        CancellationToken cancellationToken);

    Task<int> RequeueStaleLocationAsync(
        int batchSize,
        int maximumAttempts,
        CancellationToken cancellationToken);
}

internal sealed class PostgreSqlRealtimeOutboxStore(
    RealtimeWorkerConnectionFactory connections) : IRealtimeOutboxStore
{
    public async Task<IReadOnlyList<ClaimedBusinessOutboxMessage>> ClaimBusinessAsync(
        string workerId,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetWorkerRoleAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT * FROM security.claim_outbox(@worker_id,@batch_size,@lease);",
            connection,
            transaction);
        command.Parameters.Add(P("worker_id", NpgsqlDbType.Text, workerId));
        command.Parameters.Add(P("batch_size", NpgsqlDbType.Integer, batchSize));
        command.Parameters.Add(P("lease", NpgsqlDbType.Interval, lease));
        var messages = new List<ClaimedBusinessOutboxMessage>(batchSize);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                messages.Add(new(
                    reader.GetGuid(reader.GetOrdinal("id")),
                    reader.GetGuid(reader.GetOrdinal("owner_org_id")),
                    reader.GetString(reader.GetOrdinal("tenant_context")),
                    reader.GetString(reader.GetOrdinal("topic")),
                    reader.GetString(reader.GetOrdinal("aggregate_type")),
                    reader.GetGuid(reader.GetOrdinal("aggregate_id")),
                    reader.IsDBNull(reader.GetOrdinal("aggregate_version"))
                        ? null
                        : reader.GetInt32(reader.GetOrdinal("aggregate_version")),
                    reader.GetString(reader.GetOrdinal("payload")),
                    reader.GetInt32(reader.GetOrdinal("attempts")),
                    reader.GetGuid(reader.GetOrdinal("lease_token")),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("lease_expires_at")),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("available_at"))));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return messages;
    }

    public async Task<IReadOnlyList<ClaimedLocationOutboxMessage>> ClaimLocationAsync(
        string workerId,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetWorkerRoleAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT * FROM security.claim_location_outbox(@worker_id,@batch_size,@lease);",
            connection,
            transaction);
        command.Parameters.Add(P("worker_id", NpgsqlDbType.Text, workerId));
        command.Parameters.Add(P("batch_size", NpgsqlDbType.Integer, batchSize));
        command.Parameters.Add(P("lease", NpgsqlDbType.Interval, lease));
        var messages = new List<ClaimedLocationOutboxMessage>(batchSize);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                messages.Add(new(
                    reader.GetGuid(reader.GetOrdinal("id")),
                    reader.GetGuid(reader.GetOrdinal("owner_org_id")),
                    reader.GetGuid(reader.GetOrdinal("driver_position_id")),
                    reader.GetString(reader.GetOrdinal("topic")),
                    reader.GetString(reader.GetOrdinal("payload")),
                    reader.GetInt32(reader.GetOrdinal("attempts")),
                    reader.GetGuid(reader.GetOrdinal("lease_token")),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("lease_expires_at")),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("available_at"))));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return messages;
    }

    public Task<bool> SettleBusinessAsync(
        Guid id,
        Guid leaseToken,
        string status,
        string? errorCode,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken) =>
        SettleAsync(
            "security.settle_outbox",
            id,
            leaseToken,
            status,
            errorCode,
            availableAt,
            cancellationToken);

    public Task<bool> SettleLocationAsync(
        Guid id,
        Guid leaseToken,
        string status,
        string? errorCode,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken) =>
        SettleAsync(
            "security.settle_location_outbox",
            id,
            leaseToken,
            status,
            errorCode,
            availableAt,
            cancellationToken);

    public Task<int> RequeueStaleBusinessAsync(
        int batchSize,
        int maximumAttempts,
        CancellationToken cancellationToken) =>
        RequeueAsync(
            "security.requeue_stale_outbox",
            batchSize,
            maximumAttempts,
            cancellationToken);

    public Task<int> RequeueStaleLocationAsync(
        int batchSize,
        int maximumAttempts,
        CancellationToken cancellationToken) =>
        RequeueAsync(
            "security.requeue_stale_location_outbox",
            batchSize,
            maximumAttempts,
            cancellationToken);

    private async Task<bool> SettleAsync(
        string function,
        Guid id,
        Guid leaseToken,
        string status,
        string? errorCode,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetWorkerRoleAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT {function}(@id,@lease_token,@status,@error_code,@available_at);",
            connection,
            transaction);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, id));
        command.Parameters.Add(P("lease_token", NpgsqlDbType.Uuid, leaseToken));
        command.Parameters.Add(P("status", NpgsqlDbType.Text, status));
        command.Parameters.Add(P("error_code", NpgsqlDbType.Text, errorCode));
        command.Parameters.Add(P("available_at", NpgsqlDbType.TimestampTz, availableAt));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result is true;
    }

    private async Task<int> RequeueAsync(
        string function,
        int batchSize,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetWorkerRoleAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT {function}(interval '0 seconds',@batch_size,@maximum_attempts);",
            connection,
            transaction);
        command.Parameters.Add(P("batch_size", NpgsqlDbType.Integer, batchSize));
        command.Parameters.Add(P("maximum_attempts", NpgsqlDbType.Integer, maximumAttempts));
        var result = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal static async Task SetWorkerRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SET LOCAL ROLE paqueteria_worker;",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };
}
