using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Tenancy;

namespace Paqueteria.Infrastructure.Tenancy;

public sealed class WorkerTenantTransactionContext<TDbContext>(
    TDbContext dbContext,
    TenantDatabaseExecutionState state) : ITenantTransactionRunner<TDbContext>
    where TDbContext : DbContext
{
    private const string SetUserSql =
        "SELECT set_config('app.current_user_id', @user_id::uuid::text, true);";
    private const string SetOrganizationsSql =
        "SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true);";
    private const string SetRoleSql = "SET LOCAL ROLE paqueteria_worker;";

    public Task<TResult> ExecuteAsync<TResult>(
        TenantDatabaseExecutionContext executionContext,
        Func<TDbContext, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(operation);

        var strategy = dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var entered = false;
            try
            {
                var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();
                await using (var userCommand = new NpgsqlCommand(SetUserSql, connection, npgsqlTransaction))
                {
                    userCommand.Parameters.Add(new NpgsqlParameter<Guid>("user_id", NpgsqlDbType.Uuid)
                    {
                        TypedValue = executionContext.UserId,
                    });
                    await userCommand.ExecuteScalarAsync(cancellationToken);
                }

                await using (var organizationsCommand = new NpgsqlCommand(
                    SetOrganizationsSql,
                    connection,
                    npgsqlTransaction))
                {
                    organizationsCommand.Parameters.Add(
                        new NpgsqlParameter<Guid[]>(
                            "organization_ids",
                            NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                        {
                            TypedValue = executionContext.OrganizationIds.ToArray(),
                        });
                    await organizationsCommand.ExecuteScalarAsync(cancellationToken);
                }

                await using (var roleCommand = new NpgsqlCommand(
                    SetRoleSql,
                    connection,
                    npgsqlTransaction))
                {
                    await roleCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                state.Enter(executionContext.UserId, executionContext.OrganizationIds);
                entered = true;
                var result = await operation(dbContext, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
            finally
            {
                if (entered)
                {
                    state.Exit();
                }
            }
        });
    }
}
