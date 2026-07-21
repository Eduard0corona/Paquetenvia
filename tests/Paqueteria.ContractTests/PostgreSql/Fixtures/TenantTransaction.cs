using Npgsql;
using NpgsqlTypes;

namespace Paqueteria.ContractTests.PostgreSql.Fixtures;

internal sealed class TenantTransaction : IAsyncDisposable
{
    private TenantTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }

    public static async Task<TenantTransaction> BeginAsync(
        NpgsqlDataSource dataSource,
        string runtimeRole,
        Guid userId,
        Guid[] organizationIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationIds);

        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (var roleCommand = new NpgsqlCommand($"SET LOCAL ROLE {runtimeRole}", connection, transaction))
                {
                    await roleCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                const string contextSql = """
                    SELECT set_config('app.current_user_id', @user_id::uuid::text, true),
                           set_config('app.current_org_ids', @organization_ids::uuid[]::text, true)
                    """;
                await using var contextCommand = new NpgsqlCommand(contextSql, connection, transaction);
                contextCommand.Parameters.Add(new NpgsqlParameter<Guid>("user_id", NpgsqlDbType.Uuid) { TypedValue = userId });
                contextCommand.Parameters.Add(
                    new NpgsqlParameter<Guid[]>("organization_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                    {
                        TypedValue = organizationIds,
                    });
                await contextCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return new TenantTransaction(connection, transaction);
            }
            catch
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default) =>
        await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

    public async Task RollbackAsync(CancellationToken cancellationToken = default) =>
        await Transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }
}
