using Finance.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Finance.Infrastructure.Persistence;

/// <summary>
/// Runs every finance statement inside one explicit tenant transaction, so RLS on
/// <c>finance.cod_transactions</c>, <c>orders.orders</c>, <c>dispatch.assignments</c> and
/// <c>routes.routes</c> is always in force, and maps store failures onto the finance error contract.
/// </summary>
public sealed class FinanceTenantGateway(
    TenantTransactionContext<FinanceDbContext> transactionContext,
    IOptions<FinanceOptions> options)
{
    private const string AuthorizationSql =
        """
        SELECT u.status,m.role,m.status
        FROM identity.users u
        LEFT JOIN organizations.organization_memberships m
          ON m.user_id=u.id AND m.organization_id=@organization
        WHERE u.id=@actor
        ORDER BY CASE m.role
                   WHEN 'PLATFORM_ADMIN' THEN 0
                   WHEN 'DISPATCHER' THEN 1
                   WHEN 'DRIVER' THEN 2
                   ELSE 3 END
        LIMIT 1
        """;

    internal int CommandTimeoutSeconds => options.Value.CommandTimeoutSeconds;

    internal int IdempotencyLifetimeMinutes => options.Value.IdempotencyLifetimeMinutes;

    internal async Task<T> ExecuteAsync<T>(
        Guid actorId,
        Guid organizationId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(actorId, [organizationId]),
                async (dbContext, token) =>
                {
                    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                    var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!
                        .GetDbTransaction();
                    return await operation(connection, transaction, token);
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FinanceConflictException or FinanceForbiddenException or FinanceNotFoundException
                or FinanceUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (StoreFailure(exception) is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure
                    or PostgresErrorCodes.DeadlockDetected,
            })
        {
            throw FinanceSql.Conflict(FinanceConflictCode.ConcurrencyConflict, exception);
        }
        catch (Exception exception) when (
            exception is RetryLimitExceededException ||
            StoreFailure(exception) is NpgsqlException or TimeoutException)
        {
            throw new FinanceUnavailableException("The finance data store is unavailable.", exception);
        }
    }

    /// <summary>
    /// Identity, membership, role and MFA only. None of these is decided by dispatch, so none can go stale
    /// while a request waits for an order lock; a DRIVER's assignment is observed separately under that lock.
    /// </summary>
    internal async Task<FinanceAuthorizationContext> ReadAuthorizationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        bool mfaSatisfied,
        CancellationToken cancellationToken)
    {
        await using var command = FinanceSql.Create(
            connection,
            transaction,
            AuthorizationSql,
            CommandTimeoutSeconds);
        command.Parameters.Add(FinanceSql.P("actor", NpgsqlDbType.Uuid, actorId));
        command.Parameters.Add(FinanceSql.P("organization", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(0) == "ACTIVE",
                !reader.IsDBNull(2) && reader.GetString(2) == "ACTIVE",
                mfaSatisfied,
                false)
            : new(null, false, false, mfaSatisfied, false);
    }

    internal static async Task AcquireOrderLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0));",
            connection,
            transaction);
        command.Parameters.Add(FinanceSql.P("key", NpgsqlDbType.Text, $"finance-order:{orderId:D}"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// The store failure behind <paramref name="exception"/>. The EF execution strategy reports a transient
    /// failure it gave up retrying as <see cref="RetryLimitExceededException"/>, and without retries as an
    /// <see cref="InvalidOperationException"/>; both wrap the Npgsql exception that actually occurred.
    /// </summary>
    private static Exception StoreFailure(Exception exception) =>
        exception is RetryLimitExceededException or InvalidOperationException &&
        exception.InnerException is { } inner
            ? inner
            : exception;
}
