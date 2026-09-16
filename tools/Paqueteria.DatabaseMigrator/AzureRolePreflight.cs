using System.Data;
using System.Security.Cryptography;
using Npgsql;

internal static class AzureRolePreflight
{
    internal static async Task<AzureRolePreflightResult> RunAsync(
        string privilegedConnectionString,
        CancellationToken cancellationToken)
    {
        var suffix = Guid.NewGuid().ToString("N")[..16];
        var role = $"azr001_preflight_role_{suffix}";
        var login = $"azr001_preflight_login_{suffix}";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var stage = "connect";
        await using var admin = new NpgsqlConnection(privilegedConnectionString);

        try
        {
            await admin.OpenAsync(cancellationToken).ConfigureAwait(false);
            stage = "server-version";
            var version = Convert.ToInt32(await ScalarAsync(
                admin, "SELECT current_setting('server_version_num')::integer", cancellationToken));
            if (version / 10_000 != 18)
            {
                throw new InvalidOperationException();
            }

            stage = "self-grant-setting";
            var selfGrant = Convert.ToString(await ScalarAsync(
                admin, "SELECT current_setting('createrole_self_grant', true)", cancellationToken)) ?? string.Empty;

            stage = "create-role";
            await NonQueryAsync(admin, $"CREATE ROLE {role} NOLOGIN NOBYPASSRLS", cancellationToken);
            await NonQueryAsync(admin,
                $"CREATE ROLE {login} LOGIN NOINHERIT NOBYPASSRLS PASSWORD '{password}'",
                cancellationToken);

            stage = "admin-set-role";
            await NonQueryAsync(admin, $"SET ROLE {role}", cancellationToken);
            var adminRole = Convert.ToString(await ScalarAsync(admin, "SELECT current_role", cancellationToken));
            await NonQueryAsync(admin, "RESET ROLE", cancellationToken);
            if (!string.Equals(adminRole, role, StringComparison.Ordinal))
            {
                throw new InvalidOperationException();
            }

            stage = "runtime-grant";
            await NonQueryAsync(admin, $"GRANT {role} TO {login}", cancellationToken);
            var runtimeBuilder = new NpgsqlConnectionStringBuilder(privilegedConnectionString)
            {
                Username = login,
                Password = password,
                Pooling = false,
                MaxPoolSize = 1,
            };

            stage = "runtime-connect";
            await using var runtime = new NpgsqlConnection(runtimeBuilder.ConnectionString);
            await runtime.OpenAsync(cancellationToken).ConfigureAwait(false);
            stage = "runtime-set-role";
            await using (var transaction = await runtime.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                await NonQueryAsync(runtime, $"SET LOCAL ROLE {role}", cancellationToken, transaction);
                var effectiveRole = Convert.ToString(await ScalarAsync(
                    runtime, "SELECT current_role", cancellationToken, transaction));
                if (!string.Equals(effectiveRole, role, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException();
                }

                var bypass = await ScalarAsync(runtime,
                    $"SELECT rolbypassrls FROM pg_roles WHERE rolname = '{role}'",
                    cancellationToken, transaction);
                if (bypass is not false)
                {
                    throw new InvalidOperationException();
                }

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            stage = "transaction-reset";
            var resetRole = Convert.ToString(await ScalarAsync(runtime, "SELECT current_role", cancellationToken));
            if (!string.Equals(resetRole, login, StringComparison.Ordinal))
            {
                throw new InvalidOperationException();
            }

            return new AzureRolePreflightResult(selfGrant, true, true, true);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AzureRolePreflightException(stage);
        }
        finally
        {
            if (admin.State == ConnectionState.Open)
            {
                try
                {
                    await NonQueryAsync(admin, "RESET ROLE", CancellationToken.None);
                    await NonQueryAsync(admin, $"DROP ROLE IF EXISTS {login}", CancellationToken.None);
                    await NonQueryAsync(admin, $"DROP ROLE IF EXISTS {role}", CancellationToken.None);
                }
                catch (Exception)
                {
                    throw new AzureRolePreflightException("cleanup");
                }
            }
        }
    }

    private static async Task<object?> ScalarAsync(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task NonQueryAsync(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record AzureRolePreflightResult(
    string CreateroleSelfGrant,
    bool AdminSetRole,
    bool RuntimeSetRole,
    bool RuntimeNoBypassRls);

internal sealed class AzureRolePreflightException(string stage) : Exception(
    $"PG18 role-switch preflight failed at {stage}; STOP_FOR_CONTRACT_REVIEW.");
