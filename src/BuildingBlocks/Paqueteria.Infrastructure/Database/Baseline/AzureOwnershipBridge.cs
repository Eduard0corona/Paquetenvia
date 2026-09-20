using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

/// <summary>
/// E-002: transaction-scoped ownership-transfer compatibility for Azure PostgreSQL 18.
/// The canonical AI-06 and AI-18 files are still executed without alteration.
/// </summary>
public sealed record AzureOwnershipBridgeSelection(
    string EnvironmentName,
    string DeploymentClass,
    string DeploymentProvider)
{
    public void AssertAllowed()
    {
        if (!string.Equals(EnvironmentName, "DevSynthetic", StringComparison.Ordinal) ||
            !string.Equals(DeploymentClass, "DEV_SYNTHETIC", StringComparison.Ordinal) ||
            !string.Equals(DeploymentProvider, "AZURE_POSTGRESQL_FLEXIBLE_SERVER", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("E-002 ownership bridge requires Azure DEV_SYNTHETIC classification.");
        }
    }
}

internal static class AzureOwnershipBridge
{
    private const string PrecreateRoles = """
        DO $$ BEGIN CREATE ROLE paqueteria_migrator NOLOGIN NOBYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
        DO $$ BEGIN CREATE ROLE paqueteria_app NOLOGIN NOBYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
        DO $$ BEGIN CREATE ROLE paqueteria_worker NOLOGIN NOBYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
        DO $$ BEGIN CREATE ROLE paqueteria_bootstrap NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
        DO $$ BEGIN CREATE ROLE paqueteria_outbox_executor NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
        DO $$ BEGIN CREATE ROLE paqueteria_maintenance NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
        """;

    private const string GrantDatabaseCreate = """
        DO $$ BEGIN
          EXECUTE format('GRANT CREATE ON DATABASE %I TO paqueteria_migrator', current_database());
        END $$;
        """;

    private const string RevokeDatabaseCreate = """
        DO $$ BEGIN
          EXECUTE format('REVOKE CREATE ON DATABASE %I FROM paqueteria_migrator', current_database());
        END $$;
        """;

    internal static async Task PreludeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "SET LOCAL createrole_self_grant = ''", cancellationToken)
            .ConfigureAwait(false);
        await using (var selfGrant = new NpgsqlCommand(
            "SELECT current_setting('createrole_self_grant')", connection, transaction))
        {
            if (await selfGrant.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string value ||
                value.Length != 0)
            {
                throw new InvalidOperationException("E002_PLATFORM_SELF_GRANT_NOT_SETTABLE; STOP_FOR_CONTRACT_REVIEW");
            }
        }

        await using (var attributes = new NpgsqlCommand(
            "SELECT rolsuper, rolcreaterole FROM pg_roles WHERE rolname = current_user", connection, transaction))
        await using (var reader = await attributes.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetBoolean(0) || !reader.GetBoolean(1))
            {
                throw new InvalidOperationException("E-002 requires a non-superuser deployment role with CREATEROLE.");
            }
        }

        await ExecuteAsync(connection, transaction, PrecreateRoles, cancellationToken).ConfigureAwait(false);
        foreach (var role in new[] { "paqueteria_migrator", "paqueteria_bootstrap",
                     "paqueteria_outbox_executor", "paqueteria_maintenance" })
        {
            await using var capability = new NpgsqlCommand("""
                SELECT pg_catalog.pg_has_role(session_user,@role,'SET'),
                       pg_catalog.pg_has_role(session_user,@role,'USAGE')
                """, connection, transaction);
            capability.Parameters.AddWithValue("role", role);
            await using var reader = await capability.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!reader.GetBoolean(0) || (role == "paqueteria_migrator" && !reader.GetBoolean(1)))
            {
                throw new InvalidOperationException(
                    $"E002_PLATFORM_ROLE_ADMIN; effective capability missing for {role}; STOP_FOR_CONTRACT_REVIEW");
            }
        }

        await ExecuteAsync(connection, transaction, GrantDatabaseCreate, cancellationToken).ConfigureAwait(false);
    }

    internal static Task GrantSecurityCreateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken) => ExecuteAsync(connection, transaction, """
            GRANT CREATE ON SCHEMA security
            TO paqueteria_bootstrap, paqueteria_outbox_executor, paqueteria_maintenance;
            """, cancellationToken);

    internal static async Task CleanupAndAssertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var stage = "set-schema-owner-role";
        try
        {
            // AI-18 has transferred the schema to this role. Revoke as its current owner.
            await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_migrator", cancellationToken).ConfigureAwait(false);
            stage = "revoke-security-create";
            await ExecuteAsync(connection, transaction, """
                REVOKE CREATE ON SCHEMA security
                FROM paqueteria_bootstrap, paqueteria_outbox_executor, paqueteria_maintenance;
                """, cancellationToken).ConfigureAwait(false);
            stage = "reset-schema-owner-role";
            await ExecuteAsync(connection, transaction, "RESET ROLE", cancellationToken).ConfigureAwait(false);
            stage = "revoke-database-create";
            await ExecuteAsync(connection, transaction, RevokeDatabaseCreate, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            throw new InvalidOperationException(
                $"E-002 cleanup failed at {stage} (SQLSTATE {exception.SqlState}).", exception);
        }

        await using var assertion = new NpgsqlCommand("""
            SELECT NOT has_schema_privilege('paqueteria_bootstrap', 'security', 'CREATE')
               AND NOT has_schema_privilege('paqueteria_outbox_executor', 'security', 'CREATE')
               AND NOT has_schema_privilege('paqueteria_maintenance', 'security', 'CREATE')
               AND NOT has_database_privilege('paqueteria_migrator', current_database(), 'CREATE')
            """, connection, transaction);
        if (await assertion.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new InvalidOperationException("E-002 temporary CREATE privileges remain after cleanup.");
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
