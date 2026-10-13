using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Lifecycle;

namespace Orders.Infrastructure.Lifecycle;

/// <summary>
/// Invokes the ADR-034 function as <c>paqueteria_worker</c>, which holds only EXECUTE on it. No
/// tenant context is set and no tenant or order identifier is sent: discovery, locking and the
/// finalized_at write all happen inside the function's single statement.
/// </summary>
public sealed class PostgreSqlExpiredClaimWindowFinalizer : IExpiredClaimWindowFinalizer
{
    private const int CommandTimeoutSeconds = 30;
    private readonly Func<NpgsqlDataSource> _dataSource;

    public PostgreSqlExpiredClaimWindowFinalizer(NpgsqlDataSource dataSource)
        : this(() => dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
    }

    /// <summary>SCL-007: the shared Worker pool, resolved on first use so a disabled job never needs it.</summary>
    internal PostgreSqlExpiredClaimWindowFinalizer(Func<NpgsqlDataSource> dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<int> FinalizeExpiredBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource().OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker;", connection, transaction))
        {
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(
            "SELECT security.finalize_expired_orders(@batch_size);",
            connection,
            transaction)
        {
            CommandTimeout = CommandTimeoutSeconds,
        };
        command.Parameters.Add(new NpgsqlParameter<int>("batch_size", NpgsqlDbType.Integer) { TypedValue = batchSize });
        var finalized = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken);
        return finalized;
    }
}
