using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Dispatch.Infrastructure.Lifecycle;

/// <summary>A row claimed from the DISPATCH outbox lane (D8-OUTBOX-LANE-DISPATCH).</summary>
public sealed record ClaimedDispatchOutboxMessage(
    Guid Id,
    Guid OwnerOrganizationId,
    string Topic,
    string AggregateType,
    Guid AggregateId,
    int? AggregateVersion,
    string Payload,
    int Attempts,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiresAt);

/// <summary>Connection source for the Worker credential; every scope runs as paqueteria_worker.</summary>
public sealed class DispatchWorkerConnectionFactory(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(
        !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : "Host=127.0.0.1;Database=disabled;Username=disabled;Password=disabled");

    public NpgsqlDataSource DataSource => _dataSource;

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        _dataSource.OpenConnectionAsync(cancellationToken);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    internal static async Task SetWorkerRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker;", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public interface IDispatchOutboxStore
{
    Task<IReadOnlyList<ClaimedDispatchOutboxMessage>> ClaimAsync(
        string workerId,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken);

    Task<int> RequeueStaleAsync(int batchSize, int maximumAttempts, CancellationToken cancellationToken);

    Task<bool> SettleAsync(
        Guid id,
        Guid leaseToken,
        string status,
        string? errorCode,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken);

    Task<string?> ResolveConsumerAsync(string topic, CancellationToken cancellationToken);
}

/// <summary>
/// The Worker reaches the lane only through the SECURITY DEFINER functions owned by
/// paqueteria_outbox_executor: <c>claim_dispatch_outbox</c>, <c>requeue_stale_dispatch_outbox</c> and the
/// shared <c>settle_outbox</c>. It never reads or mutates outbox rows directly.
/// </summary>
public sealed class PostgreSqlDispatchOutboxStore(DispatchWorkerConnectionFactory connections) : IDispatchOutboxStore
{
    public async Task<IReadOnlyList<ClaimedDispatchOutboxMessage>> ClaimAsync(
        string workerId,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await DispatchWorkerConnectionFactory.SetWorkerRoleAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT * FROM security.claim_dispatch_outbox(@worker_id,@batch_size,@lease);",
            connection,
            transaction);
        command.Parameters.Add(P("worker_id", NpgsqlDbType.Text, workerId));
        command.Parameters.Add(P("batch_size", NpgsqlDbType.Integer, batchSize));
        command.Parameters.Add(P("lease", NpgsqlDbType.Interval, lease));
        var result = new List<ClaimedDispatchOutboxMessage>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new(
                    reader.GetGuid(reader.GetOrdinal("id")),
                    reader.GetGuid(reader.GetOrdinal("owner_org_id")),
                    reader.GetString(reader.GetOrdinal("topic")),
                    reader.GetString(reader.GetOrdinal("aggregate_type")),
                    reader.GetGuid(reader.GetOrdinal("aggregate_id")),
                    reader.IsDBNull(reader.GetOrdinal("aggregate_version"))
                        ? null
                        : reader.GetInt32(reader.GetOrdinal("aggregate_version")),
                    reader.GetString(reader.GetOrdinal("payload")),
                    reader.GetInt32(reader.GetOrdinal("attempts")),
                    reader.GetGuid(reader.GetOrdinal("lease_token")),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("lease_expires_at"))));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<int> RequeueStaleAsync(
        int batchSize,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await DispatchWorkerConnectionFactory.SetWorkerRoleAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT security.requeue_stale_dispatch_outbox(interval '0 seconds',@batch_size,@maximum_attempts);",
            connection,
            transaction);
        command.Parameters.Add(P("batch_size", NpgsqlDbType.Integer, batchSize));
        command.Parameters.Add(P("maximum_attempts", NpgsqlDbType.Integer, maximumAttempts));
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<bool> SettleAsync(
        Guid id,
        Guid leaseToken,
        string status,
        string? errorCode,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await DispatchWorkerConnectionFactory.SetWorkerRoleAsync(connection, transaction, cancellationToken);
        var result = await SettleAsync(connection, transaction, id, leaseToken, status, errorCode, availableAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<string?> ResolveConsumerAsync(string topic, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await DispatchWorkerConnectionFactory.SetWorkerRoleAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT security.resolve_outbox_consumer(@topic);",
            connection,
            transaction);
        command.Parameters.Add(P("topic", NpgsqlDbType.Text, topic));
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal static async Task<bool> SettleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        Guid leaseToken,
        string status,
        string? errorCode,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT security.settle_outbox(@id,@lease_token,@status,@error_code,@available_at);",
            connection,
            transaction);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, id));
        command.Parameters.Add(P("lease_token", NpgsqlDbType.Uuid, leaseToken));
        command.Parameters.Add(P("status", NpgsqlDbType.Text, status));
        command.Parameters.Add(P("error_code", NpgsqlDbType.Text, errorCode));
        command.Parameters.Add(P("available_at", NpgsqlDbType.TimestampTz, availableAt));
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };
}
