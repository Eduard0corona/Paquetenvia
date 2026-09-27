using System.Globalization;
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

/// <summary>
/// E-002 v0.8 §8 (managed-service preflight), §14–§21 (Clean-baseline sequence) and v0.9 Amendment 3
/// (provider failure classification). Every step runs inside the caller's baseline transaction so that
/// SET LOCAL, SET ROLE and every temporary GRANT revert through PostgreSQL transaction semantics (§33).
/// </summary>
internal static class AzureOwnershipBridge
{
    private const string PhasePreflight = "platform-preflight";
    private const string PhasePrelude = "clean-prelude";
    private const string PhaseCleanup = "clean-cleanup";

    /// <summary>E-002 v0.8 §8: read-only platform preflight before any clean-baseline mutation.</summary>
    internal static async Task PlatformPreflightAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var versionNumber = await ScalarAsync<int>(connection, transaction,
            "SELECT current_setting('server_version_num')::integer", cancellationToken).ConfigureAwait(false);
        if (versionNumber / 10_000 != 18)
        {
            throw new E002GuardException("E002_PLATFORM_POSTGRES_VERSION", PhasePreflight,
                $"expected_major=18 observed_major={versionNumber / 10_000}");
        }

        var allowlist = await ScalarAsync<object>(connection, transaction,
            "SELECT current_setting('azure.extensions', true)", cancellationToken).ConfigureAwait(false) as string;
        var allowed = (allowlist ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);
        if (!allowed.Contains("POSTGIS") || !allowed.Contains("PGCRYPTO"))
        {
            throw new E002GuardException("E002_PLATFORM_EXTENSION_ALLOWLIST", PhasePreflight,
                $"required=POSTGIS,PGCRYPTO allowlisted_count={allowed.Count}");
        }

        await using (var extensions = new NpgsqlCommand("""
            SELECT a.name, a.default_version, a.installed_version, n.nspname
            FROM pg_catalog.pg_available_extensions a
            LEFT JOIN pg_catalog.pg_extension e ON e.extname=a.name
            LEFT JOIN pg_catalog.pg_namespace n ON n.oid=e.extnamespace
            WHERE a.name IN ('postgis','pgcrypto')
            """, connection, transaction))
        await using (var reader = await extensions.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            var available = new Dictionary<string, (string Version, string? Schema)>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var version = reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2);
                available[reader.GetString(0)] = (version, reader.IsDBNull(3) ? null : reader.GetString(3));
            }

            if (!available.TryGetValue("postgis", out var postgis) ||
                !postgis.Version.StartsWith("3.6", StringComparison.Ordinal) ||
                !available.TryGetValue("pgcrypto", out var pgcrypto))
            {
                throw new E002GuardException("E002_PLATFORM_EXTENSION_UNAVAILABLE", PhasePreflight,
                    "required=postgis 3.6.x,pgcrypto");
            }

            if ((postgis.Schema is not null && postgis.Schema != "public") ||
                (pgcrypto.Schema is not null && pgcrypto.Schema != "extensions"))
            {
                throw new E002GuardException("E002_PLATFORM_EXTENSION_UNAVAILABLE", PhasePreflight,
                    "installed extension placement differs from postgis=public,pgcrypto=extensions");
            }
        }

        await using (var attributes = new NpgsqlCommand(
            "SELECT rolsuper, rolcreaterole, rolbypassrls FROM pg_catalog.pg_roles WHERE rolname = current_user",
            connection, transaction))
        await using (var reader = await attributes.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetBoolean(0) || !reader.GetBoolean(1) || !reader.GetBoolean(2))
            {
                throw new E002GuardException("E002_PLATFORM_DEPLOYMENT_ROLE_ATTRIBUTES", PhasePreflight,
                    "expected SUPERUSER=false CREATEROLE=true BYPASSRLS=true");
            }
        }

        var azureAdmin = await ScalarAsync<bool>(connection, transaction, """
            SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='azure_pg_admin')
               AND pg_catalog.pg_has_role(current_user,'azure_pg_admin','MEMBER')
            """, cancellationToken).ConfigureAwait(false);
        if (!azureAdmin)
        {
            throw new E002GuardException("E002_PLATFORM_AZURE_ADMIN_CAPABILITY", PhasePreflight,
                "expected azure_pg_admin member=true");
        }
    }

    /// <summary>
    /// E-002 v0.8 §18 steps 2–9, executed after canonical AI-06 (§14): SET LOCAL createrole_self_grant,
    /// deterministic canonical-role pre-creation, exact attributes, effective capabilities, database ACL snapshot,
    /// CREATE preconditions and the two temporary CREATE grants.
    /// </summary>
    internal static async Task<E002AclSnapshot> PreludeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Action<string>? stageObserver, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(connection, transaction, "SET LOCAL createrole_self_grant = ''", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            throw new E002GuardException("E002_PLATFORM_SELF_GRANT_NOT_SETTABLE", PhasePrelude,
                $"SQLSTATE={exception.SqlState}");
        }

        var selfGrant = await ScalarAsync<string>(connection, transaction,
            "SELECT current_setting('createrole_self_grant')", cancellationToken).ConfigureAwait(false);
        if (selfGrant.Length != 0)
        {
            throw new E002GuardException("E002_SELF_GRANT_SETTING_MISMATCH", PhasePrelude,
                "expected='' observed=non-empty");
        }

        stageObserver?.Invoke("e002-role-precreation");
        foreach (var (role, bypassRls) in E002Guards.CanonicalRoles)
        {
            var attributes = bypassRls ? "NOLOGIN BYPASSRLS" : "NOLOGIN NOBYPASSRLS";
            try
            {
                await ExecuteAsync(connection, transaction,
                    $"DO $$ BEGIN CREATE ROLE {role} {attributes}; EXCEPTION WHEN duplicate_object THEN NULL; END $$;",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (bypassRls)
            {
                throw new E002GuardException("E002_PLATFORM_BYPASSRLS_ROLE_CREATE_UNSUPPORTED", PhasePrelude,
                    $"target_role_class=specialized SQLSTATE={exception.SqlState}");
            }
        }

        var attributeMismatches = await E002Guards.RoleAttributeMismatchesAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (attributeMismatches.Count != 0)
        {
            throw new E002GuardException("E002_ROLE_ATTRIBUTE_MISMATCH", PhasePrelude,
                $"roles={string.Join(",", attributeMismatches)}");
        }

        stageObserver?.Invoke("e002-capability-gate");
        var capabilityGaps = await E002Guards.EffectiveCapabilityGapsAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (capabilityGaps.Count != 0)
        {
            throw new E002GuardException("E002_EFFECTIVE_ROLE_CAPABILITY_MISSING", PhasePrelude,
                $"roles={string.Join(",", capabilityGaps)}");
        }

        var databaseAcl = await E002AclSnapshot.ReadDatabaseAsync(connection, transaction, PhasePrelude, cancellationToken)
            .ConfigureAwait(false);

        var prestate = await ScalarAsync<bool>(connection, transaction, """
            SELECT pg_catalog.has_database_privilege('paqueteria_migrator', current_database(), 'CREATE')
                OR pg_catalog.has_schema_privilege('paqueteria_bootstrap', 'security', 'CREATE')
                OR pg_catalog.has_schema_privilege('paqueteria_outbox_executor', 'security', 'CREATE')
                OR pg_catalog.has_schema_privilege('paqueteria_maintenance', 'security', 'CREATE')
                OR pg_catalog.has_schema_privilege('paqueteria_lifecycle_executor', 'security', 'CREATE')
                OR pg_catalog.has_schema_privilege('paqueteria_cleanup_executor', 'security', 'CREATE')
            """, cancellationToken).ConfigureAwait(false);
        if (prestate)
        {
            throw new E002GuardException("E002_CREATE_PRESTATE_PRESENT", PhasePrelude,
                "temporary CREATE privilege already present before E-002 grant");
        }

        stageObserver?.Invoke("e002-grant-database-create");
        await ExecuteAsync(connection, transaction, """
            DO $$ BEGIN
              EXECUTE format('GRANT CREATE ON DATABASE %I TO paqueteria_migrator', current_database());
            END $$;
            """, cancellationToken).ConfigureAwait(false);
        stageObserver?.Invoke("e002-grant-security-create");
        await ExecuteAsync(connection, transaction, """
            GRANT CREATE ON SCHEMA security
            TO paqueteria_bootstrap, paqueteria_outbox_executor, paqueteria_maintenance, paqueteria_lifecycle_executor,
               paqueteria_cleanup_executor;
            """, cancellationToken).ConfigureAwait(false);
        return databaseAcl;
    }

    /// <summary>
    /// E-002 v0.8 §18 steps 11–15 and §19–§20: revoke the database CREATE as the (unchanged) database owner,
    /// then revoke the schema CREATE under the new schema owner, then assert normalized database ACL restoration.
    /// </summary>
    internal static async Task CleanupAndAssertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        E002AclSnapshot databaseAclBefore, Action<string>? stageObserver, CancellationToken cancellationToken)
    {
        var stage = "revoke-database-create";
        try
        {
            stageObserver?.Invoke("e002-revoke-database-create");
            await ExecuteAsync(connection, transaction, """
                DO $$ BEGIN
                  EXECUTE format('REVOKE CREATE ON DATABASE %I FROM paqueteria_migrator', current_database());
                END $$;
                """, cancellationToken).ConfigureAwait(false);
            // AI-18 has transferred the schema to this role. Revoke as its current owner (§19, load-bearing).
            stage = "set-schema-owner-role";
            stageObserver?.Invoke("e002-set-role-migrator");
            await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_migrator", cancellationToken).ConfigureAwait(false);
            stage = "revoke-security-create";
            stageObserver?.Invoke("e002-revoke-security-create");
            await ExecuteAsync(connection, transaction, """
                REVOKE CREATE ON SCHEMA security
                FROM paqueteria_bootstrap, paqueteria_outbox_executor, paqueteria_maintenance, paqueteria_lifecycle_executor,
                     paqueteria_cleanup_executor;
                """, cancellationToken).ConfigureAwait(false);
            stage = "reset-schema-owner-role";
            stageObserver?.Invoke("e002-reset-role");
            await ExecuteAsync(connection, transaction, "RESET ROLE", cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            throw new InvalidOperationException(
                $"E-002 cleanup failed at {stage} (SQLSTATE {exception.SqlState}).", exception);
        }

        stageObserver?.Invoke("e002-database-acl-restoration");
        var databaseAclAfter = await E002AclSnapshot.ReadDatabaseAsync(connection, transaction, PhaseCleanup, cancellationToken)
            .ConfigureAwait(false);
        E002AclSnapshot.AssertDatabaseRestored(databaseAclBefore, databaseAclAfter, PhaseCleanup);

        await using var assertion = new NpgsqlCommand("""
            SELECT NOT has_schema_privilege('paqueteria_bootstrap', 'security', 'CREATE')
               AND NOT has_schema_privilege('paqueteria_outbox_executor', 'security', 'CREATE')
               AND NOT has_schema_privilege('paqueteria_maintenance', 'security', 'CREATE')
               AND NOT has_schema_privilege('paqueteria_lifecycle_executor', 'security', 'CREATE')
               AND NOT has_schema_privilege('paqueteria_cleanup_executor', 'security', 'CREATE')
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

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is T typed ? typed : (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
    }
}
