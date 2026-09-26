using Npgsql;
using NpgsqlTypes;

namespace Paqueteria.Infrastructure.Database.Outbox.Retention;

public sealed record OutboxPurgeRequest(
    OutboxRetentionLane Lane,
    DateTimeOffset ProcessedBefore,
    DateTimeOffset DeadBefore,
    int BatchSize,
    bool DryRun);

/// <summary>
/// The only way the retention job reaches the outbox: one call to the lane's approved AI-06
/// purge function. Dry-run returns the bounded eligible count; otherwise the deleted count.
/// </summary>
internal interface IOutboxPurgeGateway
{
    Task<int> PurgeAsync(OutboxPurgeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Dry-run probe of the DEAD rows alone: the same approved function with
    /// <c>p_processed_before='-infinity'</c>, so no PROCESSED row can qualify. The count is bounded
    /// by the lane's maximum batch size and never mutates anything.
    /// </summary>
    Task<int> CountDeadEligibleAsync(OutboxRetentionLane lane, DateTimeOffset deadBefore, CancellationToken cancellationToken);
}

/// <summary>
/// Owns the Worker-login pool used by the retention job, apart from the tenant-scoped data
/// sources registered by the modules.
/// </summary>
internal sealed class OutboxRetentionDataSource(string connectionString) : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _dataSource = new(() => NpgsqlDataSource.Create(
        !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new InvalidOperationException(
                $"ConnectionStrings:{OutboxRetentionOptions.ConnectionStringName} is required when OutboxRetention:Enabled=true.")));

    public NpgsqlDataSource Value => _dataSource.Value;

    public ValueTask DisposeAsync() =>
        _dataSource.IsValueCreated ? _dataSource.Value.DisposeAsync() : ValueTask.CompletedTask;
}

internal sealed class PostgreSqlOutboxPurgeGateway(
    OutboxRetentionDataSource dataSource,
    int commandTimeoutSeconds) : IOutboxPurgeGateway
{
    // The Worker login is NOINHERIT; EXECUTE on the purge functions belongs to this role alone.
    private const string RuntimeRole = "paqueteria_worker";

    public Task<int> PurgeAsync(OutboxPurgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            request.Lane,
            "@processed_before",
            command =>
            {
                command.Parameters.Add(new NpgsqlParameter("processed_before", NpgsqlDbType.TimestampTz) { Value = request.ProcessedBefore });
                command.Parameters.Add(new NpgsqlParameter("dead_before", NpgsqlDbType.TimestampTz) { Value = request.DeadBefore });
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = request.BatchSize });
                command.Parameters.Add(new NpgsqlParameter("dry_run", NpgsqlDbType.Boolean) { Value = request.DryRun });
            },
            cancellationToken);
    }

    public Task<int> CountDeadEligibleAsync(OutboxRetentionLane lane, DateTimeOffset deadBefore, CancellationToken cancellationToken) =>
        ExecuteAsync(
            lane,
            "'-infinity'::timestamptz",
            command =>
            {
                command.Parameters.Add(new NpgsqlParameter("dead_before", NpgsqlDbType.TimestampTz) { Value = deadBefore });
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = OutboxRetentionLaneContract.For(lane).MaximumBatchSize });
                command.Parameters.Add(new NpgsqlParameter("dry_run", NpgsqlDbType.Boolean) { Value = true });
            },
            cancellationToken);

    private async Task<int> ExecuteAsync(
        OutboxRetentionLane lane,
        string processedBefore,
        Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        var function = OutboxRetentionLaneContract.For(lane).PurgeFunction;

        // One short transaction per batch: row locks are released between batches, and a batch
        // interrupted by cancellation or failure rolls back as a unit.
        await using var connection = await dataSource.Value.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var role = new NpgsqlCommand($"SET LOCAL ROLE {RuntimeRole}", connection, transaction))
        {
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(
            $"SELECT {function}({processedBefore}, @dead_before, @batch_size, @dry_run)",
            connection,
            transaction)
        {
            CommandTimeout = commandTimeoutSeconds,
        };
        bind(command);
        var affected = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);

        // A batch whose statement completed is committed and reported exactly; cancellation is
        // honoured before the next batch starts.
        await transaction.CommitAsync(CancellationToken.None);
        return affected;
    }
}
