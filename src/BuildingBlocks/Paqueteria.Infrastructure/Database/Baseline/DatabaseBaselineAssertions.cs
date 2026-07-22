using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

public sealed record DatabaseAssertionReport(
    string PostgreSqlVersion,
    string PostGisVersion,
    int Checks,
    TimeSpan Duration);

public sealed class DatabaseBaselineAssertions
{
    private static readonly string[] RuntimeRoles = ["paqueteria_app", "paqueteria_worker"];
    private static readonly string[] PrivilegedRoles =
    [
        "paqueteria_migrator",
        "paqueteria_bootstrap",
        "paqueteria_outbox_executor",
        "paqueteria_maintenance",
    ];

    private static readonly string[] SensitiveFunctions =
    [
        "security.resolve_identity_context(text)",
        "security.get_public_tracking_projection(text)",
        "security.map_public_order_status(text)",
        "security.claim_outbox(text,integer,interval)",
        "security.settle_outbox(uuid,uuid,text,text,timestamp with time zone)",
        "security.requeue_stale_outbox(interval,integer,integer)",
        "security.purge_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)",
        "security.claim_location_outbox(text,integer,interval)",
        "security.settle_location_outbox(uuid,uuid,text,text,timestamp with time zone)",
        "security.requeue_stale_location_outbox(interval,integer,integer)",
        "security.purge_location_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)",
    ];

    public static IReadOnlyList<string> AssertionNames { get; } = Array.AsReadOnly(
    new[]
    {
        "PostgreSQL 18 and PostGIS 3.6",
        "module/shared schemas and extension placement",
        "NOLOGIN role flags and runtime separation",
        "schema/table/sequence/function ownership",
        "per-module table and sequence default privileges",
        "runtime and PUBLIC schema restrictions",
        "append-only triggers",
        "outbox direct grants and lifecycle function grants",
        "forced RLS and sensitive-function PUBLIC revocation",
        "bootstrap function security and column-level grants",
        "real default-privilege inheritance probes",
    });

    public async Task<DatabaseAssertionReport> AssertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var ownTransaction = transaction is null;
        await using var localTransaction = ownTransaction
            ? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var activeTransaction = transaction ?? localTransaction!;
        var stopwatch = Stopwatch.StartNew();
        var violations = new List<string>();
        var checks = 0;

        try
        {
            var versions = await QuerySingleRowAsync(
                connection,
                activeTransaction,
                "SELECT current_setting('server_version'), public.PostGIS_Version()",
                cancellationToken).ConfigureAwait(false);
            var postgresVersion = versions[0];
            var postGisVersion = versions[1];
            checks += 2;
            if (!postgresVersion.StartsWith("18.", StringComparison.Ordinal))
            {
                violations.Add($"PostgreSQL major version must be 18, actual {postgresVersion}.");
            }

            if (!postGisVersion.StartsWith("3.6", StringComparison.Ordinal))
            {
                violations.Add($"PostGIS version must be 3.6, actual {postGisVersion}.");
            }

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                SELECT 'missing schema ' || expected.name
                FROM unnest(@schemas::text[]) expected(name)
                WHERE pg_catalog.to_regnamespace(expected.name) IS NULL
                UNION ALL
                SELECT 'schema owner ' || n.nspname || ' is ' || owner.rolname || ', expected paqueteria_migrator'
                FROM pg_catalog.pg_namespace n
                JOIN pg_catalog.pg_roles owner ON owner.oid=n.nspowner
                WHERE n.nspname=ANY(@schemas::text[]) AND owner.rolname<>'paqueteria_migrator'
                ORDER BY 1
                """,
                cancellationToken,
                new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
            checks++;

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                SELECT 'extension ' || expected.name || ' is missing or installed in ' || COALESCE(n.nspname,'<missing>')
                FROM (VALUES ('postgis','public'),('pgcrypto','extensions')) expected(name,schema_name)
                LEFT JOIN pg_catalog.pg_extension e ON e.extname=expected.name
                LEFT JOIN pg_catalog.pg_namespace n ON n.oid=e.extnamespace
                WHERE n.nspname IS DISTINCT FROM expected.schema_name
                """,
                cancellationToken).ConfigureAwait(false);
            checks++;

            var digest = await ScalarAsync<byte[]>(
                connection,
                activeTransaction,
                "SELECT extensions.digest(pg_catalog.convert_to('dba-001','UTF8'),'sha256')",
                cancellationToken).ConfigureAwait(false);
            checks++;
            if (digest.Length != 32)
            {
                violations.Add("extensions.digest(bytea,text) did not return a 32-byte SHA-256 value.");
            }

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                WITH expected(name,bypass_rls) AS (VALUES
                  ('paqueteria_migrator',false),
                  ('paqueteria_app',false),
                  ('paqueteria_worker',false),
                  ('paqueteria_bootstrap',true),
                  ('paqueteria_outbox_executor',true),
                  ('paqueteria_maintenance',true))
                SELECT 'role ' || expected.name || ' flags differ from least-privilege NOLOGIN contract'
                FROM expected LEFT JOIN pg_catalog.pg_roles r ON r.rolname=expected.name
                WHERE r.oid IS NULL OR r.rolcanlogin OR r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication
                  OR r.rolbypassrls IS DISTINCT FROM expected.bypass_rls
                """,
                cancellationToken).ConfigureAwait(false);
            checks++;

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                SELECT 'runtime role ' || member.rolname || ' can assume privileged role ' || granted.rolname
                FROM pg_catalog.pg_auth_members membership
                JOIN pg_catalog.pg_roles member ON member.oid=membership.member
                JOIN pg_catalog.pg_roles granted ON granted.oid=membership.roleid
                WHERE member.rolname=ANY(@runtime::text[]) AND granted.rolname=ANY(@privileged::text[])
                """,
                cancellationToken,
                new NpgsqlParameter<string[]>("runtime", RuntimeRoles),
                new NpgsqlParameter<string[]>("privileged", PrivilegedRoles)).ConfigureAwait(false);
            checks++;

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                SELECT 'object owner ' || n.nspname || '.' || c.relname || ' is ' || owner.rolname || ', expected paqueteria_migrator'
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                JOIN pg_catalog.pg_roles owner ON owner.oid=c.relowner
                WHERE n.nspname=ANY(@schemas::text[]) AND c.relkind IN ('r','p','S') AND owner.rolname<>'paqueteria_migrator'
                ORDER BY 1
                """,
                cancellationToken,
                new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
            checks++;

            await AssertFunctionOwnersAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertBootstrapContractsAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertDefaultAclCatalogAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            foreach (var role in RuntimeRoles)
            {
                checks += 2;
                if (!await ScalarAsync<bool>(connection, activeTransaction, "SELECT has_schema_privilege(@role,'public','USAGE')", cancellationToken, new NpgsqlParameter<string>("role", role)).ConfigureAwait(false))
                {
                    violations.Add($"{role} is missing USAGE on public.");
                }

                if (await ScalarAsync<bool>(connection, activeTransaction, "SELECT has_schema_privilege(@role,'public','CREATE')", cancellationToken, new NpgsqlParameter<string>("role", role)).ConfigureAwait(false))
                {
                    violations.Add($"{role} has forbidden CREATE on public.");
                }
            }

            checks++;
            if (await ScalarAsync<bool>(connection, activeTransaction, "SELECT has_schema_privilege('public','public','CREATE')", cancellationToken).ConfigureAwait(false))
            {
                violations.Add("PUBLIC has forbidden CREATE on public.");
            }

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                SELECT 'PUBLIC has CREATE on application schema ' || name
                FROM unnest(@schemas::text[]) name
                WHERE has_schema_privilege('public',name,'CREATE')
                """,
                cancellationToken,
                new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
            checks++;

            await AssertOutboxPrivilegesAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                SELECT 'sensitive function is executable by PUBLIC: ' || name
                FROM unnest(@functions::text[]) name
                WHERE has_function_privilege('public',name,'EXECUTE')
                """,
                cancellationToken,
                new NpgsqlParameter<string[]>("functions", SensitiveFunctions)).ConfigureAwait(false);
            checks++;

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                WITH expected(name) AS (VALUES
                  ('order_events_append_only'),('order_acceptances_append_only'),('proofs_append_only'),('audit_logs_append_only'),
                  ('outbox_content_immutable'),('location_outbox_content_immutable'))
                SELECT 'append-only trigger missing or disabled: ' || expected.name
                FROM expected
                LEFT JOIN pg_catalog.pg_trigger t ON t.tgname=expected.name AND NOT t.tgisinternal AND t.tgenabled<>'D'
                WHERE t.oid IS NULL
                """,
                cancellationToken).ConfigureAwait(false);
            checks++;

            var unprotectedTables = await ScalarAsync<int>(
                connection,
                activeTransaction,
                """
                SELECT count(*)::integer
                FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname=ANY(@schemas::text[]) AND c.relkind IN ('r','p')
                  AND c.relname NOT IN (
                    'cities',
                    '__ef_migrations_history_identity',
                    '__ef_migrations_history_organizations',
                    '__ef_migrations_history_locations'
                  )
                  AND (NOT c.relrowsecurity OR NOT c.relforcerowsecurity)
                """,
                cancellationToken,
                new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
            checks++;
            if (unprotectedTables != 0)
            {
                violations.Add($"{unprotectedTables} tenant tables are missing ENABLE/FORCE RLS.");
            }

            await ProbeDefaultPrivilegesAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            if (violations.Count != 0)
            {
                throw new DatabaseAssertionException(violations.AsReadOnly());
            }

            stopwatch.Stop();
            if (ownTransaction)
            {
                await activeTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            return new DatabaseAssertionReport(postgresVersion, postGisVersion, checks, stopwatch.Elapsed);
        }
        catch
        {
            if (ownTransaction && activeTransaction.Connection is not null)
            {
                await activeTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task AssertFunctionOwnersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH expected(signature,owner) AS (VALUES
              ('security.resolve_identity_context(text)','paqueteria_bootstrap'),
              ('security.get_public_tracking_projection(text)','paqueteria_bootstrap'),
              ('security.claim_outbox(text,integer,interval)','paqueteria_outbox_executor'),
              ('security.settle_outbox(uuid,uuid,text,text,timestamp with time zone)','paqueteria_outbox_executor'),
              ('security.requeue_stale_outbox(interval,integer,integer)','paqueteria_outbox_executor'),
              ('security.claim_location_outbox(text,integer,interval)','paqueteria_outbox_executor'),
              ('security.settle_location_outbox(uuid,uuid,text,text,timestamp with time zone)','paqueteria_outbox_executor'),
              ('security.requeue_stale_location_outbox(interval,integer,integer)','paqueteria_outbox_executor'),
              ('security.purge_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)','paqueteria_maintenance'),
              ('security.purge_location_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)','paqueteria_maintenance'))
            SELECT 'function owner mismatch for ' || expected.signature || ', expected ' || expected.owner
            FROM expected
            LEFT JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(expected.signature)
            LEFT JOIN pg_catalog.pg_roles owner ON owner.oid=p.proowner
            WHERE owner.rolname IS DISTINCT FROM expected.owner
            UNION ALL
            SELECT 'privileged role owns unapproved function ' || n.nspname || '.' || p.proname
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid=p.proowner
            WHERE owner.rolname IN ('paqueteria_bootstrap','paqueteria_outbox_executor','paqueteria_maintenance')
              AND NOT EXISTS (SELECT 1 FROM expected WHERE pg_catalog.to_regprocedure(expected.signature)=p.oid AND expected.owner=owner.rolname)
            UNION ALL
            SELECT 'general function owner mismatch for ' || n.nspname || '.' || p.proname || ', actual ' || owner.rolname
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid=p.proowner
            WHERE n.nspname IN ('platform','security') AND owner.rolname<>'paqueteria_migrator'
              AND NOT EXISTS (SELECT 1 FROM expected WHERE pg_catalog.to_regprocedure(expected.signature)=p.oid)
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task AssertBootstrapContractsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH expected(signature,search_path) AS (VALUES
              ('security.resolve_identity_context(text)','search_path=pg_catalog, identity, organizations, security, pg_temp'),
              ('security.get_public_tracking_projection(text)','search_path=pg_catalog, extensions, orders, security, pg_temp'))
            SELECT 'bootstrap function missing or not SECURITY DEFINER: ' || expected.signature
            FROM expected
            LEFT JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(expected.signature)
            WHERE p.oid IS NULL OR NOT p.prosecdef
            UNION ALL
            SELECT 'bootstrap function search_path mismatch: ' || expected.signature
            FROM expected
            JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(expected.signature)
            WHERE NOT (expected.search_path=ANY(COALESCE(p.proconfig,ARRAY[]::text[])))
            UNION ALL
            SELECT 'bootstrap function contains dynamic SQL EXECUTE: ' || expected.signature
            FROM expected
            JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(expected.signature)
            WHERE p.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
            UNION ALL
            SELECT 'paqueteria_app cannot execute bootstrap function: ' || expected.signature
            FROM expected
            WHERE NOT has_function_privilege('paqueteria_app',expected.signature,'EXECUTE')
            UNION ALL
            SELECT 'paqueteria_worker can execute forbidden bootstrap function: ' || expected.signature
            FROM expected
            WHERE has_function_privilege('paqueteria_worker',expected.signature,'EXECUTE')
            """,
            cancellationToken).ConfigureAwait(false);

        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH expected(table_schema,table_name,column_name) AS (VALUES
              ('identity','users','id'),
              ('identity','users','identity_subject'),
              ('identity','users','status'),
              ('organizations','organization_memberships','id'),
              ('organizations','organization_memberships','user_id'),
              ('organizations','organization_memberships','organization_id'),
              ('organizations','organization_memberships','role'),
              ('organizations','organization_memberships','status'),
              ('organizations','organization_memberships','is_default'),
              ('orders','public_tracking_tokens','id'),
              ('orders','public_tracking_tokens','order_id'),
              ('orders','public_tracking_tokens','token_hash'),
              ('orders','public_tracking_tokens','expires_at'),
              ('orders','public_tracking_tokens','revoked_at'),
              ('orders','orders','id'),
              ('orders','orders','public_id'),
              ('orders','orders','status'),
              ('orders','order_events','order_id'),
              ('orders','order_events','public_event_code'),
              ('orders','order_events','occurred_at')),
            actual AS (
              SELECT table_schema,table_name,column_name
              FROM information_schema.column_privileges
              WHERE grantee='paqueteria_bootstrap' AND privilege_type='SELECT'
                AND table_schema IN ('identity','organizations','orders'))
            SELECT 'missing bootstrap SELECT column grant: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name
            FROM expected e LEFT JOIN actual a USING(table_schema,table_name,column_name)
            WHERE a.column_name IS NULL
            UNION ALL
            SELECT 'unexpected bootstrap SELECT column grant: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name
            FROM actual a LEFT JOIN expected e USING(table_schema,table_name,column_name)
            WHERE e.column_name IS NULL
            UNION ALL
            SELECT 'bootstrap has non-SELECT column privilege: ' || table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type
            FROM information_schema.column_privileges
            WHERE grantee='paqueteria_bootstrap' AND privilege_type<>'SELECT'
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task AssertDefaultAclCatalogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH expected AS (
              SELECT schema_name,role_name,object_type,privilege
              FROM unnest(@schemas::text[]) schema_name
              CROSS JOIN unnest(@roles::text[]) role_name
              CROSS JOIN (VALUES
                ('r'::"char",'SELECT'),('r'::"char",'INSERT'),('r'::"char",'UPDATE'),('r'::"char",'DELETE'),
                ('S'::"char",'USAGE'),('S'::"char",'SELECT')) rights(object_type,privilege)
            )
            SELECT 'missing default privilege ' || schema_name || ':' || role_name || ':' || object_type::text || ':' || privilege
            FROM expected
            WHERE NOT EXISTS (
              SELECT 1
              FROM pg_catalog.pg_default_acl d
              JOIN pg_catalog.pg_namespace n ON n.oid=d.defaclnamespace
              JOIN pg_catalog.pg_roles owner ON owner.oid=d.defaclrole
              CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) acl
              JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
              WHERE n.nspname=expected.schema_name AND owner.rolname='paqueteria_migrator'
                AND d.defaclobjtype=expected.object_type AND grantee.rolname=expected.role_name
                AND acl.privilege_type=expected.privilege)
            ORDER BY 1
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.Modules.Select(item => item.Schema).ToArray()),
            new NpgsqlParameter<string[]>("roles", RuntimeRoles)).ConfigureAwait(false);
    }

    private static async Task AssertOutboxPrivilegesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        foreach (var lane in new[] { "platform.outbox_events", "platform.location_outbox_events" })
        {
            foreach (var role in RuntimeRoles)
            {
                if (!await HasTablePrivilegeAsync(connection, transaction, role, lane, "INSERT", cancellationToken).ConfigureAwait(false))
                {
                    violations.Add($"{role} is missing INSERT on {lane}.");
                }

                foreach (var privilege in new[] { "SELECT", "UPDATE", "DELETE" })
                {
                    if (await HasTablePrivilegeAsync(connection, transaction, role, lane, privilege, cancellationToken).ConfigureAwait(false))
                    {
                        violations.Add($"{role} has forbidden {privilege} on {lane}.");
                    }
                }
            }

            foreach (var privilege in new[] { "SELECT", "UPDATE" })
            {
                if (!await HasTablePrivilegeAsync(connection, transaction, "paqueteria_outbox_executor", lane, privilege, cancellationToken).ConfigureAwait(false))
                {
                    violations.Add($"paqueteria_outbox_executor is missing {privilege} on {lane}.");
                }
            }

            if (await HasTablePrivilegeAsync(connection, transaction, "paqueteria_outbox_executor", lane, "DELETE", cancellationToken).ConfigureAwait(false))
            {
                violations.Add($"paqueteria_outbox_executor has forbidden DELETE on {lane}.");
            }

            foreach (var privilege in new[] { "SELECT", "DELETE" })
            {
                if (!await HasTablePrivilegeAsync(connection, transaction, "paqueteria_maintenance", lane, privilege, cancellationToken).ConfigureAwait(false))
                {
                    violations.Add($"paqueteria_maintenance is missing {privilege} on {lane}.");
                }
            }

            if (await HasTablePrivilegeAsync(connection, transaction, "paqueteria_maintenance", lane, "UPDATE", cancellationToken).ConfigureAwait(false))
            {
                violations.Add($"paqueteria_maintenance has forbidden UPDATE on {lane}.");
            }
        }

        foreach (var signature in SensitiveFunctions.Skip(3))
        {
            if (!await HasFunctionPrivilegeAsync(connection, transaction, "paqueteria_worker", signature, cancellationToken).ConfigureAwait(false))
            {
                violations.Add($"paqueteria_worker is missing EXECUTE on {signature}.");
            }

            if (await HasFunctionPrivilegeAsync(connection, transaction, "paqueteria_app", signature, cancellationToken).ConfigureAwait(false))
            {
                violations.Add($"paqueteria_app has forbidden EXECUTE on {signature}.");
            }
        }
    }

    private static async Task ProbeDefaultPrivilegesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var table = $"dba001_privilege_probe_{suffix}";
        const string savepoint = "dba001_default_privileges";
        await ExecuteAsync(connection, transaction, $"SAVEPOINT {savepoint}", cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_migrator", cancellationToken).ConfigureAwait(false);
            foreach (var schema in DatabaseSchemaCatalog.Modules.Select(item => item.Schema))
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"CREATE TABLE \"{schema}\".\"{table}\" (id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY)",
                    cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(connection, transaction, "RESET ROLE", cancellationToken).ConfigureAwait(false);
            foreach (var schema in DatabaseSchemaCatalog.Modules.Select(item => item.Schema))
            {
                foreach (var role in RuntimeRoles)
                {
                    foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
                    {
                        if (!await HasTablePrivilegeAsync(connection, transaction, role, $"{schema}.{table}", privilege, cancellationToken).ConfigureAwait(false))
                        {
                            violations.Add($"Default table privilege {privilege} was not inherited by {role} in {schema}.");
                        }
                    }

                    var sequence = $"{schema}.{table}_id_seq";
                    foreach (var privilege in new[] { "USAGE", "SELECT" })
                    {
                        if (!await HasSequencePrivilegeAsync(connection, transaction, role, sequence, privilege, cancellationToken).ConfigureAwait(false))
                        {
                            violations.Add($"Default sequence privilege {privilege} was not inherited by {role} in {schema}.");
                        }
                    }
                }
            }
        }
        finally
        {
            await ExecuteAsync(connection, transaction, $"ROLLBACK TO SAVEPOINT {savepoint}", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, $"RELEASE SAVEPOINT {savepoint}", cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<bool> HasTablePrivilegeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string role,
        string table,
        string privilege,
        CancellationToken cancellationToken) =>
        ScalarAsync<bool>(
            connection,
            transaction,
            "SELECT has_table_privilege(@role,@object,@privilege)",
            cancellationToken,
            new NpgsqlParameter<string>("role", role),
            new NpgsqlParameter<string>("object", table),
            new NpgsqlParameter<string>("privilege", privilege));

    private static Task<bool> HasSequencePrivilegeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string role,
        string sequence,
        string privilege,
        CancellationToken cancellationToken) =>
        ScalarAsync<bool>(
            connection,
            transaction,
            "SELECT has_sequence_privilege(@role,@object,@privilege)",
            cancellationToken,
            new NpgsqlParameter<string>("role", role),
            new NpgsqlParameter<string>("object", sequence),
            new NpgsqlParameter<string>("privilege", privilege));

    private static Task<bool> HasFunctionPrivilegeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string role,
        string function,
        CancellationToken cancellationToken) =>
        ScalarAsync<bool>(
            connection,
            transaction,
            "SELECT has_function_privilege(@role,@object,'EXECUTE')",
            cancellationToken,
            new NpgsqlParameter<string>("role", role),
            new NpgsqlParameter<string>("object", function));

    private static async Task AddRowsAsync(
        ICollection<string> destination,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            destination.Add(reader.GetString(0));
        }
    }

    private static async Task<IReadOnlyList<string>> QuerySingleRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Database version query returned no row.");
        }

        return Array.AsReadOnly(new[] { reader.GetString(0), reader.GetString(1) });
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is T typed ? typed : (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
