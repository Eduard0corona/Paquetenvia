using Npgsql;
using Paqueteria.Infrastructure.Database.Baseline;

internal static class AzureOwnershipBridgeDiagnostic
{
    internal static async Task RunAsync(string connectionString, AzureOwnershipBridgeSelection selection,
        CancellationToken cancellationToken)
    {
        selection.AssertAllowed();
        var suffix = Guid.NewGuid().ToString("N")[..16];
        var role = $"azr001_owner_probe_{suffix}";
        var schema = $"azr001_schema_probe_{suffix}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var stage = "prepare";

        try
        {
            var version = await ScalarAsync<int>(connection, transaction,
                "SELECT current_setting('server_version_num')::integer", cancellationToken);
            var superuser = await ScalarAsync<bool>(connection, transaction,
                "SELECT rolsuper FROM pg_roles WHERE rolname = current_user", cancellationToken);
            if (version / 10_000 != 18 || superuser)
            {
                throw new InvalidOperationException("E-002 diagnostic requires PostgreSQL 18 and a non-superuser.");
            }

            await ExecuteAsync(connection, transaction, $"CREATE ROLE {role} NOLOGIN NOBYPASSRLS", cancellationToken);
            await ExecuteAsync(connection, transaction, $"CREATE SCHEMA {schema}", cancellationToken);
            await ExecuteAsync(connection, transaction,
                $"CREATE FUNCTION {schema}.probe() RETURNS integer LANGUAGE sql AS 'SELECT 1'", cancellationToken);

            stage = "observe-create-requirement";
            await ExecuteAsync(connection, transaction, "SAVEPOINT ownership_probe", cancellationToken);
            var denied = false;
            try
            {
                await ExecuteAsync(connection, transaction,
                    $"ALTER FUNCTION {schema}.probe() OWNER TO {role}", cancellationToken);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                denied = true;
                await ExecuteAsync(connection, transaction, "ROLLBACK TO SAVEPOINT ownership_probe", cancellationToken);
            }

            if (!denied)
            {
                throw new InvalidOperationException("E-002 diagnostic did not observe the required CREATE denial.");
            }

            await ExecuteAsync(connection, transaction, "RELEASE SAVEPOINT ownership_probe", cancellationToken);
            stage = "grant-and-transfer";
            await ExecuteAsync(connection, transaction, $"GRANT CREATE ON SCHEMA {schema} TO {role}", cancellationToken);
            await ExecuteAsync(connection, transaction,
                $"ALTER FUNCTION {schema}.probe() OWNER TO {role}", cancellationToken);

            stage = "revoke-and-verify";
            await ExecuteAsync(connection, transaction, $"REVOKE CREATE ON SCHEMA {schema} FROM {role}", cancellationToken);
            await using var verification = new NpgsqlCommand($"""
                SELECT NOT has_schema_privilege('{role}', '{schema}', 'CREATE')
                   AND (SELECT pg_get_userbyid(p.proowner)
                        FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                        WHERE n.nspname = '{schema}' AND p.proname = 'probe') = '{role}'
                """, connection, transaction);
            if (await verification.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new InvalidOperationException("E-002 diagnostic privilege cleanup or ownership check failed.");
            }

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            stage = "rollback-verification";
            await using var absence = new NpgsqlCommand($"""
                SELECT NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{role}')
                   AND to_regnamespace('{schema}') IS NULL
                """, connection);
            if (await absence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new InvalidOperationException("E-002 diagnostic objects remained after rollback.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException($"E-002 disposable diagnostic failed at {stage}; STOP_FOR_CONTRACT_REVIEW.", exception);
        }
        finally
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return (T)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
