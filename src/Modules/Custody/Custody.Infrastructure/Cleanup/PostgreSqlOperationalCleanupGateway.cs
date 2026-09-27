using System.Globalization;
using Custody.Application.Cleanup;
using Npgsql;
using NpgsqlTypes;

namespace Custody.Infrastructure.Cleanup;

/// <summary>
/// Owns the Worker-login pool of the OPS-003 jobs, apart from the tenant-scoped data sources the
/// modules register. It is created on first use, so a disabled job never needs the connection string.
/// </summary>
internal sealed class OperationalCleanupDataSource(string connectionString) : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _dataSource = new(() => NpgsqlDataSource.Create(
        !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new InvalidOperationException(
                $"ConnectionStrings:{OperationalCleanupOptions.WorkerConnectionStringName} is required when OperationalCleanup is enabled.")));

    public NpgsqlDataSource Value => _dataSource.Value;

    public ValueTask DisposeAsync() =>
        _dataSource.IsValueCreated ? _dataSource.Value.DisposeAsync() : ValueTask.CompletedTask;
}

/// <summary>
/// Invokes the OPS-003-CLEANUP-ROLE functions (including the BFF session purge) as
/// <c>paqueteria_worker</c>, which holds only EXECUTE on them. No tenant context is set and no tenant,
/// key or session identifier is sent or read back: each
/// call is one short transaction whose only result is a count.
/// </summary>
public sealed class PostgreSqlOperationalCleanupGateway : IOperationalCleanupGateway
{
    private const string RuntimeRole = "paqueteria_worker";
    private readonly Func<NpgsqlDataSource> _dataSource;
    private readonly int _commandTimeoutSeconds;

    public PostgreSqlOperationalCleanupGateway(NpgsqlDataSource dataSource, int commandTimeoutSeconds)
        : this(() => dataSource, commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
    }

    internal PostgreSqlOperationalCleanupGateway(Func<NpgsqlDataSource> dataSource, int commandTimeoutSeconds)
    {
        _dataSource = dataSource;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    public Task<int> PurgeExpiredIdempotencyKeysAsync(
        DateTimeOffset expiredBefore,
        int batchSize,
        bool dryRun,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "SELECT security.purge_expired_idempotency_keys(@expired_before, @batch_size, @dry_run);",
            command =>
            {
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("expired_before", NpgsqlDbType.TimestampTz)
                {
                    TypedValue = expiredBefore.ToUniversalTime(),
                });
                command.Parameters.Add(new NpgsqlParameter<int>("batch_size", NpgsqlDbType.Integer) { TypedValue = batchSize });
                command.Parameters.Add(new NpgsqlParameter<bool>("dry_run", NpgsqlDbType.Boolean) { TypedValue = dryRun });
            },
            cancellationToken);

    public Task<int> ExpireProofUploadSessionsAsync(int batchSize, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "SELECT security.expire_proof_upload_sessions(@batch_size);",
            command => command.Parameters.Add(
                new NpgsqlParameter<int>("batch_size", NpgsqlDbType.Integer) { TypedValue = batchSize }),
            cancellationToken);

    public Task<int> PurgeBffSessionsAsync(int batchSize, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "SELECT security.purge_bff_sessions(@batch_size);",
            command => command.Parameters.Add(
                new NpgsqlParameter<int>("batch_size", NpgsqlDbType.Integer) { TypedValue = batchSize }),
            cancellationToken);

    private async Task<int> ExecuteAsync(
        string sql,
        Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource().OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var role = new NpgsqlCommand($"SET LOCAL ROLE {RuntimeRole};", connection, transaction))
        {
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeoutSeconds,
        };
        bind(command);
        var affected = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        // A batch whose statement completed is committed and reported exactly; cancellation is
        // honoured before the next batch starts.
        await transaction.CommitAsync(CancellationToken.None);
        return affected;
    }
}
