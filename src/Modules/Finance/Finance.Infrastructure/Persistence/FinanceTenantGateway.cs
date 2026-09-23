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
        SELECT u.status,m.role,m.status,
               CASE WHEN @order::uuid IS NULL THEN false ELSE EXISTS (
                 SELECT 1
                 FROM dispatch.assignments a
                 JOIN drivers.driver_profiles p ON p.id=a.driver_id
                 WHERE a.order_id=@order AND a.status IN ('ACCEPTED','ACTIVE')
                   AND p.user_id=u.id AND p.org_id=@organization) END
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
            exception is FinanceConflictException or FinanceForbiddenException or FinanceNotFoundException)
        {
            throw;
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UniqueViolation or
                PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
        {
            throw FinanceSql.Conflict(FinanceConflictCode.ConcurrencyConflict, exception);
        }
        catch (NpgsqlException exception)
        {
            throw new FinanceUnavailableException("The finance data store is unavailable.", exception);
        }
    }

    internal async Task<FinanceAuthorizationContext> ReadAuthorizationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        Guid? orderId,
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
        command.Parameters.Add(FinanceSql.P("order", NpgsqlDbType.Uuid, orderId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(0) == "ACTIVE",
                !reader.IsDBNull(2) && reader.GetString(2) == "ACTIVE",
                mfaSatisfied,
                !reader.IsDBNull(3) && reader.GetBoolean(3))
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
}
