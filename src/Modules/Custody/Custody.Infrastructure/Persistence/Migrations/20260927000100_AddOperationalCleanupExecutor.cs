using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Custody.Infrastructure.Persistence.Migrations;

/// <summary>
/// OPS-003 (owner decision OPS-003-CLEANUP-ROLE, ADR-034 pattern) installs the only cross-tenant
/// cleanup path: <c>security.purge_expired_idempotency_keys(timestamptz,integer,boolean)</c> and
/// <c>security.expire_proof_upload_sessions(integer)</c>, both owned by the dedicated
/// <c>paqueteria_cleanup_executor NOLOGIN BYPASSRLS</c> role and executable only by
/// <c>paqueteria_worker</c>. Each function discovers and mutates in one statement and returns only
/// a count, so no tenant, key or session identifier ever crosses the privilege boundary. AI-18
/// creates the role on fresh installations; populated installations predate it, so this lane creates
/// it idempotently and refuses any pre-existing role or table shape that differs from the contract.
/// Nothing is dropped and no row is rewritten by the migration itself.
/// </summary>
/// <remarks>
/// Extension point: the BFF session purge joins this role only when <c>identity.bff_sessions</c>
/// exists (BFF-SESSION-STORE-POSTGRESQL). It will be a new Custody-lane migration that adds one
/// function and its exact column grants, then widens <see cref="OwnedFunctions"/> and the
/// verification below; this migration must stay unchanged once applied.
/// </remarks>
[DbContext(typeof(CustodyDbContext))]
[Migration(MigrationId)]
public sealed class AddOperationalCleanupExecutor : Migration
{
    public const string MigrationId = "20260927000100_AddOperationalCleanupExecutor";
    public const string ExecutorRole = "paqueteria_cleanup_executor";
    public const string IdempotencyPurgeSignature =
        "security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)";
    public const string SessionExpirySignature = "security.expire_proof_upload_sessions(integer)";
    public const int MaximumIdempotencyBatchSize = 5_000;
    public const int MaximumSessionBatchSize = 1_000;

    /// <summary>OPS-003-OFFLINE-72H: no key younger than this is ever deleted, whatever the caller passes.</summary>
    public static readonly TimeSpan IdempotencyFloor = TimeSpan.FromHours(72);

    public static IReadOnlyList<string> OwnedFunctions { get; } =
        Array.AsReadOnly(new[] { IdempotencyPurgeSignature, SessionExpirySignature });

    public const string UpSql =
        """
        RESET ROLE;

        DO $adoption$
        DECLARE
          key_columns text[];
          session_columns text[];
        BEGIN
          IF to_regclass('platform.idempotency_keys') IS NULL
             OR to_regclass('custody.proof_upload_sessions') IS NULL THEN
            RAISE EXCEPTION 'OPS-003 requires the canonical AI-06 idempotency and upload-session tables';
          END IF;

          SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY column_name)
          INTO key_columns
          FROM information_schema.columns
          WHERE table_schema='platform' AND table_name='idempotency_keys'
            AND column_name IN ('created_at','expires_at','idempotency_key','owner_org_id','scope');
          IF key_columns IS DISTINCT FROM ARRAY[
            'created_at:timestamp with time zone:NO',
            'expires_at:timestamp with time zone:NO',
            'idempotency_key:text:NO',
            'owner_org_id:uuid:NO',
            'scope:text:NO'
          ] THEN
            RAISE EXCEPTION 'platform.idempotency_keys columns do not match the canonical AI-06 contract';
          END IF;

          SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY column_name)
          INTO session_columns
          FROM information_schema.columns
          WHERE table_schema='custody' AND table_name='proof_upload_sessions'
            AND column_name IN ('expires_at','id','status','updated_at');
          IF session_columns IS DISTINCT FROM ARRAY[
            'expires_at:timestamp with time zone:NO',
            'id:uuid:NO',
            'status:text:NO',
            'updated_at:timestamp with time zone:NO'
          ] THEN
            RAISE EXCEPTION 'custody.proof_upload_sessions columns do not match the canonical AI-06 contract';
          END IF;

          IF NOT EXISTS (
               SELECT 1 FROM pg_class
               WHERE oid='platform.idempotency_keys'::regclass AND relrowsecurity AND relforcerowsecurity)
             OR NOT EXISTS (
               SELECT 1 FROM pg_class
               WHERE oid='custody.proof_upload_sessions'::regclass AND relrowsecurity AND relforcerowsecurity)
          THEN
            RAISE EXCEPTION 'OPS-003 requires FORCE ROW LEVEL SECURITY on both cleanup tables';
          END IF;

          IF to_regclass('platform.idempotency_expiry_idx') IS NULL
             OR to_regclass('custody.proof_upload_sessions_expiry_idx') IS NULL THEN
            RAISE EXCEPTION 'OPS-003 requires the canonical AI-06 expiry indexes';
          END IF;

          -- Cleanup deletes keys and writes status/updated_at only; a user trigger could widen that.
          IF EXISTS (
            SELECT 1 FROM pg_trigger
            WHERE tgrelid IN ('platform.idempotency_keys'::regclass, 'custody.proof_upload_sessions'::regclass)
              AND NOT tgisinternal
          ) THEN
            RAISE EXCEPTION 'OPS-003 refuses cleanup tables with user triggers: cleanup must not mutate other rows';
          END IF;
        END
        $adoption$;

        DO $role$
        BEGIN
          CREATE ROLE paqueteria_cleanup_executor NOLOGIN BYPASSRLS;
        EXCEPTION WHEN duplicate_object THEN NULL;
        END
        $role$;

        DO $existing_role$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM pg_roles
            WHERE rolname='paqueteria_cleanup_executor'
              AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication
                OR NOT rolbypassrls OR NOT rolinherit)
          ) THEN
            RAISE EXCEPTION 'paqueteria_cleanup_executor exists with attributes outside the OPS-003-CLEANUP-ROLE contract';
          END IF;
          IF EXISTS (
            SELECT 1 FROM pg_auth_members m
            WHERE m.member='paqueteria_cleanup_executor'::regrole
          ) THEN
            RAISE EXCEPTION 'paqueteria_cleanup_executor must not inherit any other role';
          END IF;
          IF EXISTS (SELECT 1 FROM pg_class WHERE relowner='paqueteria_cleanup_executor'::regrole)
             OR EXISTS (SELECT 1 FROM pg_namespace WHERE nspowner='paqueteria_cleanup_executor'::regrole)
             OR EXISTS (SELECT 1 FROM pg_type WHERE typowner='paqueteria_cleanup_executor'::regrole)
             OR EXISTS (
               SELECT 1 FROM pg_proc
               WHERE proowner='paqueteria_cleanup_executor'::regrole
                 AND oid IS DISTINCT FROM to_regprocedure('security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)')
                 AND oid IS DISTINCT FROM to_regprocedure('security.expire_proof_upload_sessions(integer)')
             ) THEN
            RAISE EXCEPTION 'paqueteria_cleanup_executor already owns objects outside the OPS-003-CLEANUP-ROLE contract';
          END IF;
        END
        $existing_role$;

        REVOKE paqueteria_cleanup_executor FROM paqueteria_app, paqueteria_worker;

        GRANT USAGE ON SCHEMA platform, custody TO paqueteria_cleanup_executor;
        GRANT SELECT (owner_org_id,scope,idempotency_key,created_at,expires_at) ON platform.idempotency_keys
          TO paqueteria_cleanup_executor;
        GRANT DELETE ON platform.idempotency_keys TO paqueteria_cleanup_executor;
        GRANT SELECT (id,status,expires_at) ON custody.proof_upload_sessions TO paqueteria_cleanup_executor;
        GRANT UPDATE (status,updated_at) ON custody.proof_upload_sessions TO paqueteria_cleanup_executor;

        CREATE OR REPLACE FUNCTION security.purge_expired_idempotency_keys(
          p_expired_before timestamptz,
          p_batch_size integer,
          p_dry_run boolean)
        RETURNS integer
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, platform, pg_temp
        AS $function$
        DECLARE
          v_now timestamptz := pg_catalog.clock_timestamp();
          -- OPS-003-OFFLINE-72H: a key younger than 72 hours may still guard a legitimate offline
          -- replay, so the floor is fixed here and no argument can move it.
          v_floor timestamptz := v_now - interval '72 hours';
          v_cutoff timestamptz;
          v_count integer;
        BEGIN
          IF p_expired_before IS NULL OR p_dry_run IS NULL
             OR p_batch_size IS NULL OR p_batch_size < 1 OR p_batch_size > 5000 THEN
            RAISE EXCEPTION USING
              ERRCODE = '22023',
              MESSAGE = 'OPS003_ARGUMENT_OUT_OF_RANGE';
          END IF;

          -- A key is purged only when it has expired and is older than the floor; a later or
          -- infinite p_expired_before cannot reach a key that is still live.
          v_cutoff := LEAST(p_expired_before, v_now);

          IF p_dry_run THEN
            SELECT count(*)::integer INTO v_count
            FROM (
              SELECT 1
              FROM platform.idempotency_keys k
              WHERE k.expires_at < v_cutoff
                AND k.created_at < v_floor
              ORDER BY k.expires_at
              LIMIT p_batch_size
            ) eligible;
            RETURN v_count;
          END IF;

          WITH candidates AS (
            SELECT k.owner_org_id, k.scope, k.idempotency_key
            FROM platform.idempotency_keys k
            WHERE k.expires_at < v_cutoff
              AND k.created_at < v_floor
            ORDER BY k.expires_at
            LIMIT p_batch_size
          )
          DELETE FROM platform.idempotency_keys k
          USING candidates c
          WHERE k.owner_org_id = c.owner_org_id
            AND k.scope = c.scope
            AND k.idempotency_key = c.idempotency_key
            AND k.expires_at < v_cutoff
            AND k.created_at < v_floor;

          GET DIAGNOSTICS v_count = ROW_COUNT;
          RETURN v_count;
        END
        $function$;

        CREATE OR REPLACE FUNCTION security.expire_proof_upload_sessions(p_batch_size integer)
        RETURNS integer
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, custody, pg_temp
        AS $function$
        DECLARE
          v_now timestamptz := pg_catalog.clock_timestamp();
          v_expired integer;
        BEGIN
          IF p_batch_size IS NULL OR p_batch_size < 1 OR p_batch_size > 1000 THEN
            RAISE EXCEPTION USING
              ERRCODE = '22023',
              MESSAGE = 'OPS003_BATCH_SIZE_OUT_OF_RANGE';
          END IF;

          -- Same rule the API and the validation processor apply lazily: a non-terminal session whose
          -- expires_at is not after now becomes EXPIRED. REJECTED, EXPIRED and CONSUMED never change.
          WITH candidates AS (
            SELECT s.id
            FROM custody.proof_upload_sessions s
            WHERE s.status IN ('CREATED','UPLOADED','VALIDATING','READY')
              AND s.expires_at <= v_now
            ORDER BY s.expires_at, s.id
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED
          )
          UPDATE custody.proof_upload_sessions s
          SET status = 'EXPIRED', updated_at = v_now
          FROM candidates c
          WHERE s.id = c.id
            AND s.status IN ('CREATED','UPLOADED','VALIDATING','READY')
            AND s.expires_at <= v_now;

          GET DIAGNOSTICS v_expired = ROW_COUNT;
          RETURN v_expired;
        END
        $function$;

        -- ACL first, while the deploying role still owns the functions: ALTER ... OWNER rewrites the
        -- grantor to the new owner, and a managed-service deployer then needs no inherited executor rights.
        REVOKE ALL ON FUNCTION security.purge_expired_idempotency_keys(timestamptz,integer,boolean) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.expire_proof_upload_sessions(integer) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION security.purge_expired_idempotency_keys(timestamptz,integer,boolean) TO paqueteria_worker;
        GRANT EXECUTE ON FUNCTION security.expire_proof_upload_sessions(integer) TO paqueteria_worker;
        ALTER FUNCTION security.purge_expired_idempotency_keys(timestamptz,integer,boolean) OWNER TO paqueteria_cleanup_executor;
        ALTER FUNCTION security.expire_proof_upload_sessions(integer) OWNER TO paqueteria_cleanup_executor;

        DO $verify$
        DECLARE
          purge oid := to_regprocedure('security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)');
          expire oid := to_regprocedure('security.expire_proof_upload_sessions(integer)');
        BEGIN
          IF purge IS NULL OR expire IS NULL
             OR EXISTS (
               SELECT 1 FROM pg_proc p
               WHERE p.oid IN (purge, expire)
                 AND (pg_get_userbyid(p.proowner) <> 'paqueteria_cleanup_executor'
                   OR NOT p.prosecdef
                   OR p.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
                   OR has_function_privilege('public', p.oid, 'EXECUTE')
                   OR has_function_privilege('paqueteria_app', p.oid, 'EXECUTE')
                   OR NOT has_function_privilege('paqueteria_worker', p.oid, 'EXECUTE')))
             OR NOT ('search_path=pg_catalog, platform, pg_temp' = ANY(
               COALESCE((SELECT proconfig FROM pg_proc WHERE oid=purge), ARRAY[]::text[])))
             OR NOT ('search_path=pg_catalog, custody, pg_temp' = ANY(
               COALESCE((SELECT proconfig FROM pg_proc WHERE oid=expire), ARRAY[]::text[])))
          THEN
            RAISE EXCEPTION 'OPS-003 cleanup function security verification failed';
          END IF;

          -- Exactly: SELECT on five key columns plus table DELETE on platform.idempotency_keys, and
          -- SELECT(id,status,expires_at) plus UPDATE(status,updated_at) on custody.proof_upload_sessions.
          IF (SELECT array_agg(table_schema || '.' || table_name || ':' || privilege_type
                ORDER BY table_schema, table_name, privilege_type)
              FROM information_schema.table_privileges
              WHERE grantee='paqueteria_cleanup_executor')
               IS DISTINCT FROM ARRAY['platform.idempotency_keys:DELETE']
             OR (SELECT array_agg(table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type
                   ORDER BY table_schema, table_name, privilege_type, column_name)
                 FROM information_schema.column_privileges
                 WHERE grantee='paqueteria_cleanup_executor')
               IS DISTINCT FROM ARRAY[
                 'custody.proof_upload_sessions.expires_at:SELECT',
                 'custody.proof_upload_sessions.id:SELECT',
                 'custody.proof_upload_sessions.status:SELECT',
                 'custody.proof_upload_sessions.status:UPDATE',
                 'custody.proof_upload_sessions.updated_at:UPDATE',
                 'platform.idempotency_keys.created_at:SELECT',
                 'platform.idempotency_keys.expires_at:SELECT',
                 'platform.idempotency_keys.idempotency_key:SELECT',
                 'platform.idempotency_keys.owner_org_id:SELECT',
                 'platform.idempotency_keys.scope:SELECT'
               ]
          THEN
            RAISE EXCEPTION 'paqueteria_cleanup_executor privileges differ from the OPS-003-CLEANUP-ROLE contract';
          END IF;

          -- CREATE on security is the one E-002 transaction-scoped grant the ownership transfer needs
          -- under the managed-service model; the Custody lane asserts it is gone after the bridge cleanup.
          IF EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN (
                'identity','organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
                'incidents','finance','allies','notifications','reporting','security','extensions')
              AND has_schema_privilege('paqueteria_cleanup_executor', n.oid, 'USAGE')
          ) OR EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN (
                'identity','organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
                'custody','incidents','finance','allies','notifications','reporting','platform','extensions')
              AND has_schema_privilege('paqueteria_cleanup_executor', n.oid, 'CREATE')
          ) OR NOT has_schema_privilege('paqueteria_cleanup_executor', 'platform', 'USAGE')
            OR NOT has_schema_privilege('paqueteria_cleanup_executor', 'custody', 'USAGE')
          THEN
            RAISE EXCEPTION 'paqueteria_cleanup_executor schema privileges differ from the OPS-003-CLEANUP-ROLE contract';
          END IF;

          IF pg_has_role('paqueteria_app', 'paqueteria_cleanup_executor', 'MEMBER')
             OR pg_has_role('paqueteria_worker', 'paqueteria_cleanup_executor', 'MEMBER')
             OR (SELECT count(*) FROM pg_proc WHERE proowner='paqueteria_cleanup_executor'::regrole) <> 2
          THEN
            RAISE EXCEPTION 'paqueteria_cleanup_executor ownership or membership differs from the OPS-003-CLEANUP-ROLE contract';
          END IF;
        END
        $verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """;

    /// <summary>
    /// OPS-003 rollback: the schema is never downgraded, because every deleted key and every EXPIRED
    /// session is a legitimate lifecycle fact. Disable the jobs with
    /// <c>OperationalCleanup:IdempotencyKeys:Enabled=false</c> and
    /// <c>OperationalCleanup:ProofUploadSessions:Enabled=false</c> and, when the database itself has
    /// to stop cleaning, apply <see cref="OperationalRollbackSql"/> with the deployment credential.
    /// </summary>
    public const string SchemaDowngradeNotSupportedSql =
        """
        DO $schema_downgrade$
        BEGIN
          RAISE EXCEPTION USING
            MESSAGE = 'OPS003_SCHEMA_DOWNGRADE_NOT_SUPPORTED',
            DETAIL = 'OPS-003 rollback blocked: disable OperationalCleanup and revoke worker EXECUTE; purged keys and expired sessions are never restored.',
            ERRCODE = 'P0001';
        END
        $schema_downgrade$;
        """;

    public const string OperationalRollbackSql =
        """
        REVOKE EXECUTE ON FUNCTION security.purge_expired_idempotency_keys(timestamptz,integer,boolean) FROM paqueteria_worker;
        REVOKE EXECUTE ON FUNCTION security.expire_proof_upload_sessions(integer) FROM paqueteria_worker;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaDowngradeNotSupportedSql);
}
