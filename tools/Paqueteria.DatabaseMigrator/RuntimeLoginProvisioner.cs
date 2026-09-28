using System.Text.RegularExpressions;
using Npgsql;

/// <summary>
/// ENV-001: creates or re-keys the two least-privilege runtime LOGIN roles of the pilot database. Each login is
/// <c>LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS</c> and a member of exactly one
/// canonical runtime role (<c>paqueteria_app</c> for the API, <c>paqueteria_worker</c> for the Worker).
/// </summary>
/// <remarks>
/// Only SCRAM-SHA-256 verifiers are accepted, never a plaintext password: the deployment workflow derives the
/// verifier from the password it stores in Key Vault, so no plaintext runtime password reaches this process,
/// PostgreSQL statement text or server logs. The canonical roles must already exist (Applied baseline).
/// </remarks>
internal static partial class RuntimeLoginProvisioner
{
    internal const string ApiVerifierVariable = "PAQUETERIA_API_LOGIN_VERIFIER";
    internal const string WorkerVerifierVariable = "PAQUETERIA_WORKER_LOGIN_VERIFIER";
    internal const string ApiLogin = "pv_pilot_api";
    internal const string WorkerLogin = "pv_pilot_worker";

    internal static readonly (string Login, string RuntimeRole, string VerifierVariable)[] Logins =
    [
        (ApiLogin, "paqueteria_app", ApiVerifierVariable),
        (WorkerLogin, "paqueteria_worker", WorkerVerifierVariable),
    ];

    internal static bool IsScramVerifier(string? value) => value is not null && ScramVerifier().IsMatch(value);

    /// <param name="readSetting">
    /// Resolves a setting by name. The migrator passes the same lookup it uses for its connection, so with
    /// the ADP-001 Key Vault secrets source on (PILOT-KEYVAULT-PRIVATE-APP-READ) the verifiers come from the
    /// mapped Key Vault secrets rather than from environment variables.
    /// </param>
    internal static async Task RunAsync(string connectionString, Func<string, string?> readSetting,
        CancellationToken cancellationToken)
    {
        var verifiers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (login, _, variable) in Logins)
        {
            var verifier = readSetting(variable);
            if (!IsScramVerifier(verifier))
            {
                throw new RuntimeLoginException($"{variable} must be a SCRAM-SHA-256 verifier (4096 iterations).");
            }

            verifiers[login] = verifier!;
        }

        var stage = "connect";
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (login, runtimeRole, _) in Logins)
            {
                stage = $"role-exists:{runtimeRole}";
                if (!await RoleExistsAsync(connection, transaction, runtimeRole, cancellationToken).ConfigureAwait(false))
                {
                    throw new RuntimeLoginException($"canonical role {runtimeRole} is missing; apply the baseline first.");
                }

                stage = $"upsert:{login}";
                var verb = await RoleExistsAsync(connection, transaction, login, cancellationToken).ConfigureAwait(false)
                    ? "ALTER ROLE {0} WITH"
                    : "CREATE ROLE {0}";
                // The login name is a compile-time constant and the verifier matched the strict SCRAM pattern
                // (base64 alphabet, '$' and ':' only), so neither can close the literal.
                await ExecuteAsync(connection, transaction,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture, verb, login) +
                    " LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '" +
                    verifiers[login] + "'",
                    cancellationToken).ConfigureAwait(false);

                stage = $"memberships:{login}";
                await using (var others = new NpgsqlCommand("""
                    SELECT r.rolname FROM pg_catalog.pg_auth_members m
                    JOIN pg_catalog.pg_roles r ON r.oid = m.roleid
                    JOIN pg_catalog.pg_roles u ON u.oid = m.member
                    WHERE u.rolname = @login AND r.rolname <> @runtime
                    """, connection, transaction))
                {
                    others.Parameters.AddWithValue("login", login);
                    others.Parameters.AddWithValue("runtime", runtimeRole);
                    var stale = new List<string>();
                    await using (var reader = await others.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            stale.Add(reader.GetString(0));
                        }
                    }

                    foreach (var role in stale)
                    {
                        await ExecuteAsync(connection, transaction,
                            $"REVOKE {QuoteIdentifier(role)} FROM {login}", cancellationToken).ConfigureAwait(false);
                    }
                }

                await ExecuteAsync(connection, transaction, $"GRANT {runtimeRole} TO {login}", cancellationToken)
                    .ConfigureAwait(false);

                stage = $"assert:{login}";
                await AssertLoginAsync(connection, transaction, login, runtimeRole, cancellationToken).ConfigureAwait(false);
            }

            stage = "commit";
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            // Only the SQLSTATE is reported: statement text could carry the verifier.
            throw new RuntimeLoginException($"runtime login provisioning failed at {stage} (SQLSTATE {exception.SqlState}).");
        }
    }

    private static async Task AssertLoginAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string login, string runtimeRole, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT u.rolcanlogin AND NOT u.rolinherit AND NOT u.rolsuper AND NOT u.rolcreatedb
                   AND NOT u.rolcreaterole AND NOT u.rolreplication AND NOT u.rolbypassrls
                   AND (SELECT array_agg(r.rolname ORDER BY r.rolname) FROM pg_catalog.pg_auth_members m
                        JOIN pg_catalog.pg_roles r ON r.oid = m.roleid WHERE m.member = u.oid) = ARRAY[@runtime]::name[]
            FROM pg_catalog.pg_roles u WHERE u.rolname = @login
            """, connection, transaction);
        command.Parameters.AddWithValue("login", login);
        command.Parameters.AddWithValue("runtime", runtimeRole);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new RuntimeLoginException($"{login} does not have the exact least-privilege runtime shape.");
        }
    }

    private static async Task<bool> RoleExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string role, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = @role)", connection, transaction);
        command.Parameters.AddWithValue("role", role);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // SCRAM-SHA-256$<iterations>:<base64 16-byte salt>$<base64 StoredKey>:<base64 ServerKey>
    [GeneratedRegex(@"^SCRAM-SHA-256\$4096:[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{43}=:[A-Za-z0-9+/]{43}=$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ScramVerifier();
}

internal sealed class RuntimeLoginException(string message) : Exception(message);
