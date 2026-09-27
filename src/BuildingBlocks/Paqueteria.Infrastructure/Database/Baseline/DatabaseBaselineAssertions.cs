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
        "paqueteria_lifecycle_executor",
        "paqueteria_cleanup_executor",
        "paqueteria_registration_executor",
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
        "pilot operational and outbox purge indexes (AI06-PILOT-INDEXES)",
        "lifecycle executor boundary (ADR-034)",
        "cleanup executor boundary (OPS-003-CLEANUP-ROLE)",
        "registration executor boundary (REG-001)",
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
                  ('paqueteria_maintenance',true)),
                -- ADR-034: installations that predate LIF-001 have no lifecycle executor yet; once the
                -- role or its function exists it is held to the same NOLOGIN contract.
                lifecycle(name,bypass_rls) AS (
                  SELECT 'paqueteria_lifecycle_executor',true
                  WHERE pg_catalog.to_regrole('paqueteria_lifecycle_executor') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.finalize_expired_orders(integer)') IS NOT NULL),
                -- OPS-003-CLEANUP-ROLE: the same rule for installations that predate the cleanup executor.
                cleanup(name,bypass_rls) AS (
                  SELECT 'paqueteria_cleanup_executor',true
                  WHERE pg_catalog.to_regrole('paqueteria_cleanup_executor') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.expire_proof_upload_sessions(integer)') IS NOT NULL),
                -- REG-001: the same rule for installations that predate the registration executor.
                registration(name,bypass_rls) AS (
                  SELECT 'paqueteria_registration_executor',true
                  WHERE pg_catalog.to_regrole('paqueteria_registration_executor') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.register_identity_subject(text,uuid)') IS NOT NULL)
                SELECT 'role ' || expected.name || ' flags differ from least-privilege NOLOGIN contract'
                FROM (SELECT name,bypass_rls FROM expected UNION ALL SELECT name,bypass_rls FROM lifecycle
                      UNION ALL SELECT name,bypass_rls FROM cleanup
                      UNION ALL SELECT name,bypass_rls FROM registration) expected
                LEFT JOIN pg_catalog.pg_roles r ON r.rolname=expected.name
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

            await AssertPilotIndexesAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertLifecycleExecutorBoundaryAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertCleanupExecutorBoundaryAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertRegistrationExecutorBoundaryAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
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
                    '__ef_migrations_history_locations',
                    '__ef_migrations_history_drivers',
                    '__ef_migrations_history_pricing',
                    '__ef_migrations_history_orders',
                    '__ef_migrations_history_dispatch',
                    '__ef_migrations_history_custody',
                    '__ef_migrations_history_incidents',
                    '__ef_migrations_history_finance',
                    '__ef_migrations_history_notifications',
                    '__ef_migrations_history_platform',
                    '__ef_migrations_history_platform_evolution'
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
            WITH expected_all(signature,owner,ntf) AS (VALUES
              ('security.resolve_identity_context(text)','paqueteria_bootstrap',false),
              ('security.get_public_tracking_projection(text)','paqueteria_bootstrap',false),
              ('security.claim_outbox(text,integer,interval)','paqueteria_outbox_executor',false),
              ('security.settle_outbox(uuid,uuid,text,text,timestamp with time zone)','paqueteria_outbox_executor',false),
              ('security.requeue_stale_outbox(interval,integer,integer)','paqueteria_outbox_executor',false),
              ('security.claim_location_outbox(text,integer,interval)','paqueteria_outbox_executor',false),
              ('security.settle_location_outbox(uuid,uuid,text,text,timestamp with time zone)','paqueteria_outbox_executor',false),
              ('security.requeue_stale_location_outbox(interval,integer,integer)','paqueteria_outbox_executor',false),
              ('security.purge_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)','paqueteria_maintenance',false),
              ('security.purge_location_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)','paqueteria_maintenance',false),
              ('security.resolve_outbox_consumer(text)','paqueteria_outbox_executor',true),
              ('security.claim_realtime_outbox(text,integer,interval)','paqueteria_outbox_executor',true),
              ('security.claim_notifications_outbox(text,integer,interval)','paqueteria_outbox_executor',true),
              ('security.claim_unowned_outbox(text,integer,interval)','paqueteria_outbox_executor',true),
              ('security.requeue_stale_realtime_outbox(interval,integer,integer)','paqueteria_outbox_executor',true),
              ('security.recover_stale_notifications_outbox(text,integer,integer,interval)','paqueteria_outbox_executor',true),
              ('security.requeue_stale_unowned_outbox(interval,integer,integer)','paqueteria_outbox_executor',true),
              ('security.read_owner_dispatcher_ids(uuid,integer)','paqueteria_outbox_executor',true),
              ('security.read_active_notification_users(uuid[])','paqueteria_outbox_executor',true),
              ('security.emit_notification_status_changed(uuid,uuid,integer,text,text,integer,timestamp with time zone)','paqueteria_outbox_executor',true),
              ('security.expand_order_created_notifications(uuid,uuid,uuid,text,text,timestamp with time zone,uuid[])','paqueteria_outbox_executor',true),
              ('security.read_notification_delivery(uuid,uuid)','paqueteria_outbox_executor',true),
              ('security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamp with time zone,timestamp with time zone)','paqueteria_outbox_executor',true),
              ('security.finalize_notification_max_attempts(uuid,uuid,uuid,integer,timestamp with time zone)','paqueteria_outbox_executor',true),
              ('notifications.provision_default_templates()','paqueteria_outbox_executor',true)),
            expected(signature,owner) AS (
              SELECT signature,owner FROM expected_all
              WHERE NOT ntf OR to_regprocedure('security.resolve_outbox_consumer(text)') IS NOT NULL
              UNION ALL
              -- ADR-034: installed by the Orders LIF-001 lane after the baseline.
              SELECT 'security.finalize_expired_orders(integer)','paqueteria_lifecycle_executor'
              WHERE to_regprocedure('security.finalize_expired_orders(integer)') IS NOT NULL
              UNION ALL
              -- D8-OUTBOX-LANE-DISPATCH: installed by the Notifications lane after NTF-001.
              SELECT signature,'paqueteria_outbox_executor'
              FROM (VALUES
                ('security.claim_dispatch_outbox(text,integer,interval)'),
                ('security.requeue_stale_dispatch_outbox(interval,integer,integer)')) dispatch_lane(signature)
              WHERE to_regprocedure('security.claim_dispatch_outbox(text,integer,interval)') IS NOT NULL
                 OR to_regprocedure('security.requeue_stale_dispatch_outbox(interval,integer,integer)') IS NOT NULL
              UNION ALL
              -- OPS-003-CLEANUP-ROLE: installed by the Custody OPS-003 lane after the baseline.
              SELECT signature,'paqueteria_cleanup_executor'
              FROM (VALUES
                ('security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)'),
                ('security.expire_proof_upload_sessions(integer)')) cleanup(signature)
              WHERE to_regprocedure(signature) IS NOT NULL
              UNION ALL
              -- REG-001: installed by the Organizations lane after the baseline.
              SELECT signature,'paqueteria_registration_executor'
              FROM (VALUES
                ('security.register_identity_subject(text,uuid)'),
                ('security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text)'),
                ('security.list_own_organization_applications(uuid)'),
                ('security.list_pending_ally_organizations(uuid,uuid,integer)'),
                ('security.decide_ally_organization(uuid,uuid,uuid,boolean,text)')) registration(signature)
              WHERE to_regprocedure(signature) IS NOT NULL
            )
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
            WHERE owner.rolname IN ('paqueteria_bootstrap','paqueteria_outbox_executor','paqueteria_maintenance',
                'paqueteria_lifecycle_executor','paqueteria_cleanup_executor','paqueteria_registration_executor')
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

    /// <summary>
    /// AI06-PILOT-INDEXES: the four operational indexes and one partial index per terminal purge arm
    /// of each outbox, exactly as AI-06 declares them and valid.
    /// </summary>
    private static async Task AssertPilotIndexesAsync(
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
            WITH expected(name,definition) AS (VALUES
              ('orders.orders_updated_at_idx',
               'CREATE INDEX orders_updated_at_idx ON orders.orders USING btree (updated_at)'),
              ('dispatch.assignments_driver_idx',
               'CREATE INDEX assignments_driver_idx ON dispatch.assignments USING btree (driver_id)'),
              ('dispatch.assignments_route_idx',
               'CREATE INDEX assignments_route_idx ON dispatch.assignments USING btree (route_id) WHERE (route_id IS NOT NULL)'),
              ('routes.route_stops_order_idx',
               'CREATE INDEX route_stops_order_idx ON routes.route_stops USING btree (order_id)'),
              ('platform.outbox_purge_processed_idx',
               'CREATE INDEX outbox_purge_processed_idx ON platform.outbox_events USING btree (processed_at) WHERE (status = ''PROCESSED''::text)'),
              ('platform.outbox_purge_dead_idx',
               'CREATE INDEX outbox_purge_dead_idx ON platform.outbox_events USING btree (COALESCE(processed_at, created_at)) WHERE (status = ''DEAD''::text)'),
              ('platform.location_outbox_purge_processed_idx',
               'CREATE INDEX location_outbox_purge_processed_idx ON platform.location_outbox_events USING btree (processed_at) WHERE (status = ''PROCESSED''::text)'),
              ('platform.location_outbox_purge_dead_idx',
               'CREATE INDEX location_outbox_purge_dead_idx ON platform.location_outbox_events USING btree (COALESCE(processed_at, created_at)) WHERE (status = ''DEAD''::text)'))
            SELECT 'pilot index missing, invalid or different: ' || e.name
            FROM expected e
            LEFT JOIN pg_catalog.pg_index i ON i.indexrelid=pg_catalog.to_regclass(e.name)
            WHERE i.indexrelid IS NULL
               OR NOT i.indisvalid
               OR pg_catalog.pg_get_indexdef(i.indexrelid)<>e.definition
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
              ('organizations','organizations','id'),
              ('organizations','organizations','status'),
              ('orders','public_tracking_tokens','id'),
              ('orders','public_tracking_tokens','order_id'),
              ('orders','public_tracking_tokens','token_hash'),
              ('orders','public_tracking_tokens','expires_at'),
              ('orders','public_tracking_tokens','revoked_at'),
              ('orders','orders','id'),
              ('orders','orders','public_id'),
              ('orders','orders','status'),
              ('orders','orders','version'),
              ('orders','order_events','order_id'),
              ('orders','order_events','aggregate_version'),
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

    /// <summary>
    /// ADR-034 lane contract: after the Orders LIF-001 migration (and after any E-002 temporary grant
    /// is revoked) the role and its function must exist and satisfy the exact executor boundary,
    /// including no CREATE on any application or shared schema.
    /// </summary>
    public static async Task AssertLifecycleExecutorInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var violations = new List<string>();
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            SELECT 'lifecycle executor role is missing'
            WHERE pg_catalog.to_regrole('paqueteria_lifecycle_executor') IS NULL
            UNION ALL
            SELECT 'lifecycle finalization function is missing: security.finalize_expired_orders(integer)'
            WHERE pg_catalog.to_regprocedure('security.finalize_expired_orders(integer)') IS NULL
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertLifecycleExecutorBoundaryAsync(connection, transaction, violations, cancellationToken)
            .ConfigureAwait(false);
        if (violations.Count != 0)
        {
            throw new DatabaseAssertionException(violations.AsReadOnly());
        }
    }

    /// <summary>
    /// ADR-034: once the lifecycle executor exists it holds exactly USAGE on <c>orders</c> plus
    /// SELECT(id,status,claim_window_ends_at,finalized_at) and UPDATE(finalized_at) on
    /// <c>orders.orders</c>, inherits nothing, and its single function is a pinned SECURITY DEFINER
    /// executable by <c>paqueteria_worker</c> only.
    /// </summary>
    private static async Task AssertLifecycleExecutorBoundaryAsync(
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
            WITH executor AS (
              SELECT oid FROM pg_catalog.pg_roles WHERE rolname='paqueteria_lifecycle_executor'
            ),
            expected(table_schema,table_name,column_name,privilege_type) AS (VALUES
              ('orders','orders','id','SELECT'),
              ('orders','orders','status','SELECT'),
              ('orders','orders','claim_window_ends_at','SELECT'),
              ('orders','orders','finalized_at','SELECT'),
              ('orders','orders','finalized_at','UPDATE')),
            actual AS (
              SELECT table_schema,table_name,column_name,privilege_type
              FROM information_schema.column_privileges
              WHERE grantee='paqueteria_lifecycle_executor'),
            fn AS (
              SELECT p.oid,p.prosecdef,p.proconfig,p.prosrc
              FROM pg_catalog.pg_proc p
              WHERE p.oid=pg_catalog.to_regprocedure('security.finalize_expired_orders(integer)'))
            SELECT 'missing lifecycle executor column grant: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name || ':' || e.privilege_type
            FROM expected e CROSS JOIN executor
            LEFT JOIN actual a USING(table_schema,table_name,column_name,privilege_type)
            WHERE a.column_name IS NULL
            UNION ALL
            SELECT 'unexpected lifecycle executor column grant: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name || ':' || a.privilege_type
            FROM actual a LEFT JOIN expected e USING(table_schema,table_name,column_name,privilege_type)
            WHERE e.column_name IS NULL
            UNION ALL
            SELECT 'unexpected lifecycle executor table grant: ' || table_schema || '.' || table_name || ':' || privilege_type
            FROM information_schema.table_privileges
            WHERE grantee='paqueteria_lifecycle_executor'
            UNION ALL
            SELECT 'lifecycle executor schema privilege differs: ' || n.nspname
            FROM pg_catalog.pg_namespace n CROSS JOIN executor
            WHERE n.nspname=ANY(@schemas::text[])
              AND (has_schema_privilege(executor.oid,n.oid,'CREATE')
                OR has_schema_privilege(executor.oid,n.oid,'USAGE') IS DISTINCT FROM (n.nspname='orders'))
            UNION ALL
            SELECT 'lifecycle executor inherits role ' || pg_catalog.pg_get_userbyid(m.roleid)
            FROM pg_catalog.pg_auth_members m JOIN executor ON m.member=executor.oid
            UNION ALL
            SELECT 'lifecycle executor owns a relation, schema or type'
            FROM executor
            WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE relowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_type WHERE typowner=executor.oid)
            UNION ALL
            SELECT 'lifecycle finalization function is unsafe: security.finalize_expired_orders(integer)'
            FROM fn
            WHERE NOT fn.prosecdef
               OR NOT ('search_path=pg_catalog, orders, pg_temp'=ANY(COALESCE(fn.proconfig,ARRAY[]::text[])))
               OR fn.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
               OR has_function_privilege('public',fn.oid,'EXECUTE')
               OR has_function_privilege('paqueteria_app',fn.oid,'EXECUTE')
               OR NOT has_function_privilege('paqueteria_worker',fn.oid,'EXECUTE')
            UNION ALL
            SELECT 'lifecycle finalization function exists without the lifecycle executor role'
            FROM fn WHERE NOT EXISTS (SELECT 1 FROM executor)
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
    }

    /// <summary>
    /// OPS-003-CLEANUP-ROLE lane contract: after the Custody OPS-003 migration (and after any E-002
    /// temporary grant is revoked) the role and both functions must exist and satisfy the exact
    /// executor boundary, including no CREATE on any application or shared schema.
    /// </summary>
    public static async Task AssertCleanupExecutorInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var violations = new List<string>();
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            SELECT 'cleanup executor role is missing'
            WHERE pg_catalog.to_regrole('paqueteria_cleanup_executor') IS NULL
            UNION ALL
            SELECT 'cleanup function is missing: ' || signature
            FROM (VALUES
              ('security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)'),
              ('security.expire_proof_upload_sessions(integer)')) expected(signature)
            WHERE pg_catalog.to_regprocedure(signature) IS NULL
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertCleanupExecutorBoundaryAsync(connection, transaction, violations, cancellationToken)
            .ConfigureAwait(false);
        if (violations.Count != 0)
        {
            throw new DatabaseAssertionException(violations.AsReadOnly());
        }
    }

    /// <summary>
    /// OPS-003-CLEANUP-ROLE: once the cleanup executor exists it holds exactly USAGE on
    /// <c>platform</c> and <c>custody</c>, SELECT on five key columns plus DELETE on
    /// <c>platform.idempotency_keys</c>, SELECT(id,status,expires_at) plus UPDATE(status,updated_at) on
    /// <c>custody.proof_upload_sessions</c>, inherits nothing, and its two functions are pinned
    /// SECURITY DEFINERs executable by <c>paqueteria_worker</c> only.
    /// </summary>
    private static async Task AssertCleanupExecutorBoundaryAsync(
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
            WITH executor AS (
              SELECT oid FROM pg_catalog.pg_roles WHERE rolname='paqueteria_cleanup_executor'
            ),
            expected(table_schema,table_name,column_name,privilege_type) AS (VALUES
              ('platform','idempotency_keys','owner_org_id','SELECT'),
              ('platform','idempotency_keys','scope','SELECT'),
              ('platform','idempotency_keys','idempotency_key','SELECT'),
              ('platform','idempotency_keys','created_at','SELECT'),
              ('platform','idempotency_keys','expires_at','SELECT'),
              ('custody','proof_upload_sessions','id','SELECT'),
              ('custody','proof_upload_sessions','status','SELECT'),
              ('custody','proof_upload_sessions','expires_at','SELECT'),
              ('custody','proof_upload_sessions','status','UPDATE'),
              ('custody','proof_upload_sessions','updated_at','UPDATE')),
            actual AS (
              SELECT table_schema,table_name,column_name,privilege_type
              FROM information_schema.column_privileges
              WHERE grantee='paqueteria_cleanup_executor'),
            expected_tables(table_schema,table_name,privilege_type) AS (VALUES
              ('platform','idempotency_keys','DELETE')),
            actual_tables AS (
              SELECT table_schema,table_name,privilege_type
              FROM information_schema.table_privileges
              WHERE grantee='paqueteria_cleanup_executor'),
            fn(signature,search_path) AS (VALUES
              ('security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)',
               'search_path=pg_catalog, platform, pg_temp'),
              ('security.expire_proof_upload_sessions(integer)',
               'search_path=pg_catalog, custody, pg_temp')),
            installed AS (
              SELECT fn.signature,fn.search_path,p.oid,p.prosecdef,p.proconfig,p.prosrc
              FROM fn JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(fn.signature))
            SELECT 'missing cleanup executor column grant: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name || ':' || e.privilege_type
            FROM expected e CROSS JOIN executor
            LEFT JOIN actual a USING(table_schema,table_name,column_name,privilege_type)
            WHERE a.column_name IS NULL
            UNION ALL
            SELECT 'unexpected cleanup executor column grant: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name || ':' || a.privilege_type
            FROM actual a LEFT JOIN expected e USING(table_schema,table_name,column_name,privilege_type)
            WHERE e.column_name IS NULL
            UNION ALL
            SELECT 'missing cleanup executor table grant: ' || e.table_schema || '.' || e.table_name || ':' || e.privilege_type
            FROM expected_tables e CROSS JOIN executor
            LEFT JOIN actual_tables a USING(table_schema,table_name,privilege_type)
            WHERE a.table_name IS NULL
            UNION ALL
            SELECT 'unexpected cleanup executor table grant: ' || a.table_schema || '.' || a.table_name || ':' || a.privilege_type
            FROM actual_tables a LEFT JOIN expected_tables e USING(table_schema,table_name,privilege_type)
            WHERE e.table_name IS NULL
            UNION ALL
            SELECT 'cleanup executor schema privilege differs: ' || n.nspname
            FROM pg_catalog.pg_namespace n CROSS JOIN executor
            WHERE n.nspname=ANY(@schemas::text[])
              AND (has_schema_privilege(executor.oid,n.oid,'CREATE')
                OR has_schema_privilege(executor.oid,n.oid,'USAGE') IS DISTINCT FROM (n.nspname IN ('platform','custody')))
            UNION ALL
            SELECT 'cleanup executor inherits role ' || pg_catalog.pg_get_userbyid(m.roleid)
            FROM pg_catalog.pg_auth_members m JOIN executor ON m.member=executor.oid
            UNION ALL
            SELECT 'cleanup executor owns a relation, schema or type'
            FROM executor
            WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE relowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_type WHERE typowner=executor.oid)
            UNION ALL
            SELECT 'cleanup function is unsafe: ' || installed.signature
            FROM installed
            WHERE NOT installed.prosecdef
               OR NOT (installed.search_path=ANY(COALESCE(installed.proconfig,ARRAY[]::text[])))
               OR installed.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
               OR has_function_privilege('public',installed.oid,'EXECUTE')
               OR has_function_privilege('paqueteria_app',installed.oid,'EXECUTE')
               OR NOT has_function_privilege('paqueteria_worker',installed.oid,'EXECUTE')
            UNION ALL
            SELECT 'cleanup function exists without the cleanup executor role: ' || installed.signature
            FROM installed WHERE NOT EXISTS (SELECT 1 FROM executor)
            UNION ALL
            SELECT 'cleanup functions are only partially installed'
            FROM (SELECT count(*) AS present FROM installed) c
            WHERE c.present=1
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
    }

    /// <summary>
    /// REG-001 lane contract: after the Organizations REG-001 migration (and after any E-002 temporary
    /// grant is revoked) the role and its five functions must exist and satisfy the exact executor boundary.
    /// </summary>
    public static async Task AssertRegistrationExecutorInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var violations = new List<string>();
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            SELECT 'registration executor role is missing'
            WHERE pg_catalog.to_regrole('paqueteria_registration_executor') IS NULL
            UNION ALL
            SELECT 'registration function is missing: ' || signature
            FROM unnest(@signatures::text[]) expected(signature)
            WHERE pg_catalog.to_regprocedure(signature) IS NULL
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("signatures", RegistrationFunctions.Select(item => item.Signature).ToArray()))
            .ConfigureAwait(false);
        await AssertRegistrationExecutorBoundaryAsync(connection, transaction, violations, cancellationToken)
            .ConfigureAwait(false);
        if (violations.Count != 0)
        {
            throw new DatabaseAssertionException(violations.AsReadOnly());
        }
    }

    private static readonly (string Signature, string SearchPath)[] RegistrationFunctions =
    [
        ("security.register_identity_subject(text,uuid)", "search_path=pg_catalog, identity, pg_temp"),
        ("security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text)",
            "search_path=pg_catalog, identity, organizations, platform, pg_temp"),
        ("security.list_own_organization_applications(uuid)", "search_path=pg_catalog, identity, organizations, pg_temp"),
        ("security.list_pending_ally_organizations(uuid,uuid,integer)",
            "search_path=pg_catalog, identity, organizations, pg_temp"),
        ("security.decide_ally_organization(uuid,uuid,uuid,boolean,text)",
            "search_path=pg_catalog, identity, organizations, platform, pg_temp"),
    ];

    /// <summary>
    /// REG-001: once the registration executor exists it holds exactly USAGE on <c>identity</c>,
    /// <c>organizations</c> and <c>platform</c> and the AI-18 column grants, no table-wide grant, inherits
    /// nothing, owns no relation, and its functions are pinned SECURITY DEFINERs executable by
    /// <c>paqueteria_app</c> only. The role may exist without functions (fresh AI-18 before the lane, or
    /// after the lane is rolled back); a function without the role, or a partial set, is a violation.
    /// </summary>
    private static async Task AssertRegistrationExecutorBoundaryAsync(
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
            WITH executor AS (
              SELECT oid FROM pg_catalog.pg_roles WHERE rolname='paqueteria_registration_executor'
            ),
            expected(table_schema,table_name,column_name,privilege_type) AS (VALUES
              ('identity','users','id','SELECT'),
              ('identity','users','identity_subject','SELECT'),
              ('identity','users','status','SELECT'),
              ('identity','users','id','INSERT'),
              ('identity','users','identity_subject','INSERT'),
              ('identity','users','status','INSERT'),
              ('identity','users','created_at','INSERT'),
              ('organizations','organizations','id','SELECT'),
              ('organizations','organizations','organization_type','SELECT'),
              ('organizations','organizations','legal_name','SELECT'),
              ('organizations','organizations','display_name','SELECT'),
              ('organizations','organizations','status','SELECT'),
              ('organizations','organizations','self_service_creator_user_id','SELECT'),
              ('organizations','organizations','created_at','SELECT'),
              ('organizations','organizations','id','INSERT'),
              ('organizations','organizations','organization_type','INSERT'),
              ('organizations','organizations','legal_name','INSERT'),
              ('organizations','organizations','display_name','INSERT'),
              ('organizations','organizations','status','INSERT'),
              ('organizations','organizations','self_service_creator_user_id','INSERT'),
              ('organizations','organizations','created_at','INSERT'),
              ('organizations','organizations','status','UPDATE'),
              ('organizations','organization_memberships','user_id','SELECT'),
              ('organizations','organization_memberships','organization_id','SELECT'),
              ('organizations','organization_memberships','role','SELECT'),
              ('organizations','organization_memberships','status','SELECT'),
              ('organizations','organization_memberships','is_default','SELECT'),
              ('organizations','organization_memberships','id','INSERT'),
              ('organizations','organization_memberships','user_id','INSERT'),
              ('organizations','organization_memberships','organization_id','INSERT'),
              ('organizations','organization_memberships','role','INSERT'),
              ('organizations','organization_memberships','status','INSERT'),
              ('organizations','organization_memberships','is_default','INSERT'),
              ('organizations','organization_memberships','granted_at','INSERT'),
              ('organizations','organization_memberships','is_default','UPDATE'),
              ('platform','audit_logs','id','INSERT'),
              ('platform','audit_logs','org_id','INSERT'),
              ('platform','audit_logs','actor_id','INSERT'),
              ('platform','audit_logs','action','INSERT'),
              ('platform','audit_logs','entity_type','INSERT'),
              ('platform','audit_logs','entity_id','INSERT'),
              ('platform','audit_logs','request_id','INSERT'),
              ('platform','audit_logs','payload_redacted','INSERT'),
              ('platform','audit_logs','occurred_at','INSERT')),
            actual AS (
              SELECT table_schema,table_name,column_name,privilege_type
              FROM information_schema.column_privileges
              WHERE grantee='paqueteria_registration_executor'),
            actual_tables AS (
              SELECT table_schema,table_name,privilege_type
              FROM information_schema.table_privileges
              WHERE grantee='paqueteria_registration_executor'),
            fn AS (
              SELECT signature,search_path
              FROM unnest(@signatures::text[],@search_paths::text[]) f(signature,search_path)),
            installed AS (
              SELECT fn.signature,fn.search_path,p.oid,p.prosecdef,p.proconfig,p.prosrc
              FROM fn JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(fn.signature))
            SELECT 'missing registration executor column grant: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name || ':' || e.privilege_type
            FROM expected e CROSS JOIN executor
            LEFT JOIN actual a USING(table_schema,table_name,column_name,privilege_type)
            WHERE a.column_name IS NULL
            UNION ALL
            SELECT 'unexpected registration executor column grant: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name || ':' || a.privilege_type
            FROM actual a LEFT JOIN expected e USING(table_schema,table_name,column_name,privilege_type)
            WHERE e.column_name IS NULL
            UNION ALL
            SELECT 'unexpected registration executor table grant: ' || a.table_schema || '.' || a.table_name || ':' || a.privilege_type
            FROM actual_tables a
            UNION ALL
            SELECT 'registration executor schema privilege differs: ' || n.nspname
            FROM pg_catalog.pg_namespace n CROSS JOIN executor
            WHERE n.nspname=ANY(@schemas::text[])
              AND (has_schema_privilege(executor.oid,n.oid,'CREATE')
                OR has_schema_privilege(executor.oid,n.oid,'USAGE') IS DISTINCT FROM
                   (n.nspname IN ('identity','organizations','platform')))
            UNION ALL
            SELECT 'registration executor inherits role ' || pg_catalog.pg_get_userbyid(m.roleid)
            FROM pg_catalog.pg_auth_members m JOIN executor ON m.member=executor.oid
            UNION ALL
            SELECT 'registration executor owns a relation, schema or type'
            FROM executor
            WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE relowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_type WHERE typowner=executor.oid)
            UNION ALL
            SELECT 'registration function is unsafe: ' || installed.signature
            FROM installed
            WHERE NOT installed.prosecdef
               OR NOT (installed.search_path=ANY(COALESCE(installed.proconfig,ARRAY[]::text[])))
               OR installed.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
               OR has_function_privilege('public',installed.oid,'EXECUTE')
               OR has_function_privilege('paqueteria_worker',installed.oid,'EXECUTE')
               OR NOT has_function_privilege('paqueteria_app',installed.oid,'EXECUTE')
            UNION ALL
            SELECT 'registration function exists without the registration executor role: ' || installed.signature
            FROM installed WHERE NOT EXISTS (SELECT 1 FROM executor)
            UNION ALL
            SELECT 'registration functions are only partially installed'
            FROM (SELECT count(*) AS present FROM installed) c
            WHERE c.present BETWEEN 1 AND 4
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray()),
            new NpgsqlParameter<string[]>("signatures", RegistrationFunctions.Select(item => item.Signature).ToArray()),
            new NpgsqlParameter<string[]>("search_paths", RegistrationFunctions.Select(item => item.SearchPath).ToArray()))
            .ConfigureAwait(false);
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

        var ntfInstalled = await ScalarAsync<bool>(
            connection,
            transaction,
            "SELECT to_regprocedure('security.resolve_outbox_consumer(text)') IS NOT NULL",
            cancellationToken).ConfigureAwait(false);
        var workerFunctions = ntfInstalled
            ? new[]
            {
                "security.settle_outbox(uuid,uuid,text,text,timestamp with time zone)",
                "security.purge_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)",
                "security.claim_location_outbox(text,integer,interval)",
                "security.settle_location_outbox(uuid,uuid,text,text,timestamp with time zone)",
                "security.requeue_stale_location_outbox(interval,integer,integer)",
                "security.purge_location_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)",
                "security.resolve_outbox_consumer(text)",
                "security.claim_realtime_outbox(text,integer,interval)",
                "security.claim_notifications_outbox(text,integer,interval)",
                "security.claim_unowned_outbox(text,integer,interval)",
                "security.requeue_stale_realtime_outbox(interval,integer,integer)",
                "security.recover_stale_notifications_outbox(text,integer,integer,interval)",
                "security.requeue_stale_unowned_outbox(interval,integer,integer)",
                "security.read_owner_dispatcher_ids(uuid,integer)",
                "security.read_active_notification_users(uuid[])",
                "security.expand_order_created_notifications(uuid,uuid,uuid,text,text,timestamp with time zone,uuid[])",
                "security.read_notification_delivery(uuid,uuid)",
                "security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamp with time zone,timestamp with time zone)",
                "security.finalize_notification_max_attempts(uuid,uuid,uuid,integer,timestamp with time zone)",
            }
            : SensitiveFunctions.Skip(3).ToArray();
        var dispatchLaneInstalled = await ScalarAsync<bool>(
            connection,
            transaction,
            """
            SELECT to_regprocedure('security.claim_dispatch_outbox(text,integer,interval)') IS NOT NULL
                OR to_regprocedure('security.requeue_stale_dispatch_outbox(interval,integer,integer)') IS NOT NULL
            """,
            cancellationToken).ConfigureAwait(false);
        if (dispatchLaneInstalled)
        {
            // D8-OUTBOX-LANE-DISPATCH: EXECUTE for paqueteria_worker only.
            workerFunctions =
            [
                .. workerFunctions,
                "security.claim_dispatch_outbox(text,integer,interval)",
                "security.requeue_stale_dispatch_outbox(interval,integer,integer)",
            ];
        }

        foreach (var signature in workerFunctions)
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

        if (ntfInstalled)
        {
            foreach (var obsolete in new[]
            {
                "security.claim_outbox(text,integer,interval)",
                "security.requeue_stale_outbox(interval,integer,integer)",
            })
            {
                if (await HasFunctionPrivilegeAsync(connection, transaction, "paqueteria_worker", obsolete, cancellationToken).ConfigureAwait(false))
                {
                    violations.Add($"paqueteria_worker retains forbidden EXECUTE on {obsolete} after NTF-001 cutover.");
                }
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
