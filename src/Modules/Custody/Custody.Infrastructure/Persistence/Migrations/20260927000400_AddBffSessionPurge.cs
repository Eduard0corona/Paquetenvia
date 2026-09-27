using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Custody.Infrastructure.Persistence.Migrations;

/// <summary>
/// BFF-SESSION-TABLE-SHAPE purge through the OPS-003 cleanup role (OPS-003-CLEANUP-ROLE: the cleanup
/// executor owns "the BFF session purge"). It adds <c>security.purge_bff_sessions(integer)</c>, owned by
/// <c>paqueteria_cleanup_executor</c> and executable only by <c>paqueteria_worker</c>, together with the
/// executor's USAGE on schema <c>identity</c>, SELECT(session_key_hash,expires_at,revoked_at) and DELETE on
/// <c>identity.bff_sessions</c>, and SELECT(jti_hash,expires_at) and DELETE on
/// <c>identity.bff_logout_jtis</c> (BFF-LOGOUT-JTI-PERSISTENCE). The function deletes only revoked
/// sessions and sessions whose expiry is not after its own clock and, with what remains of the batch,
/// logout jtis past their retention; it returns only a count. It is the extension point the OPS-003
/// lane (<see cref="AddOperationalCleanupExecutor"/>) reserved: it runs after that migration, whose own
/// exact grant verification stays untouched, and after the Identity lane has created the table.
/// Nothing is dropped and no row is rewritten by the migration itself.
/// </summary>
[DbContext(typeof(CustodyDbContext))]
[Migration(MigrationId)]
public sealed class AddBffSessionPurge : Migration
{
    public const string MigrationId = "20260927000400_AddBffSessionPurge";
    public const string PurgeSignature = "security.purge_bff_sessions(integer)";
    public const string SearchPath = "search_path=pg_catalog, identity, pg_temp";
    public const int MaximumBatchSize = 1_000;

    public const string UpSql =
        """
        RESET ROLE;

        DO $adoption$
        DECLARE
          session_columns text[];
        BEGIN
          IF to_regclass('identity.bff_sessions') IS NULL THEN
            RAISE EXCEPTION 'The BFF session purge requires identity.bff_sessions from the Identity lane';
          END IF;

          SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY column_name)
          INTO session_columns
          FROM information_schema.columns
          WHERE table_schema='identity' AND table_name='bff_sessions'
            AND column_name IN ('expires_at','revoked_at','session_key_hash');
          IF session_columns IS DISTINCT FROM ARRAY[
            'expires_at:timestamp with time zone:NO',
            'revoked_at:timestamp with time zone:YES',
            'session_key_hash:bytea:NO'
          ] THEN
            RAISE EXCEPTION 'identity.bff_sessions columns do not match the canonical AI-06 contract';
          END IF;

          IF to_regclass('identity.bff_sessions_expiry_idx') IS NULL
             OR to_regclass('identity.bff_sessions_revoked_idx') IS NULL THEN
            RAISE EXCEPTION 'The BFF session purge requires the canonical AI-06 expiry and revocation indexes';
          END IF;

          IF to_regclass('identity.bff_logout_jtis') IS NULL
             OR to_regclass('identity.bff_logout_jtis_expiry_idx') IS NULL
             OR (SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY column_name)
                 FROM information_schema.columns
                 WHERE table_schema='identity' AND table_name='bff_logout_jtis'
                   AND column_name IN ('expires_at','jti_hash'))
                IS DISTINCT FROM ARRAY['expires_at:timestamp with time zone:NO', 'jti_hash:bytea:NO'] THEN
            RAISE EXCEPTION 'The BFF session purge requires the canonical AI-06 identity.bff_logout_jtis and its expiry index';
          END IF;

          -- The purge deletes rows and nothing else; a user trigger could widen that.
          IF EXISTS (
            SELECT 1 FROM pg_trigger
            WHERE tgrelid IN ('identity.bff_sessions'::regclass, 'identity.bff_logout_jtis'::regclass)
              AND NOT tgisinternal
          ) THEN
            RAISE EXCEPTION 'The BFF session purge refuses identity.bff_sessions or identity.bff_logout_jtis with user triggers';
          END IF;

          -- The OPS-003 lane created and verified the executor; it must own nothing but its functions.
          IF to_regrole('paqueteria_cleanup_executor') IS NULL
             OR to_regprocedure('security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)') IS NULL
             OR to_regprocedure('security.expire_proof_upload_sessions(integer)') IS NULL THEN
            RAISE EXCEPTION 'The BFF session purge requires the OPS-003 cleanup executor lane';
          END IF;
          IF EXISTS (
            SELECT 1 FROM pg_proc
            WHERE proowner='paqueteria_cleanup_executor'::regrole
              AND oid IS DISTINCT FROM to_regprocedure('security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)')
              AND oid IS DISTINCT FROM to_regprocedure('security.expire_proof_upload_sessions(integer)')
              AND oid IS DISTINCT FROM to_regprocedure('security.purge_bff_sessions(integer)')
          ) OR EXISTS (SELECT 1 FROM pg_class WHERE relowner='paqueteria_cleanup_executor'::regrole)
            OR EXISTS (SELECT 1 FROM pg_auth_members WHERE member='paqueteria_cleanup_executor'::regrole) THEN
            RAISE EXCEPTION 'paqueteria_cleanup_executor already owns objects outside the OPS-003-CLEANUP-ROLE contract';
          END IF;
        END
        $adoption$;

        GRANT USAGE ON SCHEMA identity TO paqueteria_cleanup_executor;
        GRANT SELECT (session_key_hash,expires_at,revoked_at) ON identity.bff_sessions TO paqueteria_cleanup_executor;
        GRANT DELETE ON identity.bff_sessions TO paqueteria_cleanup_executor;
        GRANT SELECT (jti_hash,expires_at) ON identity.bff_logout_jtis TO paqueteria_cleanup_executor;
        GRANT DELETE ON identity.bff_logout_jtis TO paqueteria_cleanup_executor;

        CREATE OR REPLACE FUNCTION security.purge_bff_sessions(p_batch_size integer)
        RETURNS integer
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, pg_temp
        AS $function$
        DECLARE
          v_now timestamptz := pg_catalog.clock_timestamp();
          v_count integer;
          v_jtis integer := 0;
        BEGIN
          IF p_batch_size IS NULL OR p_batch_size < 1 OR p_batch_size > 1000 THEN
            RAISE EXCEPTION USING
              ERRCODE = '22023',
              MESSAGE = 'OPS003_BATCH_SIZE_OUT_OF_RANGE';
          END IF;

          -- A live session (not revoked, expiry after now) is never a candidate, and the DELETE target
          -- rechecks the same predicate, so a concurrent create or resolve is never affected.
          WITH candidates AS (
            SELECT s.session_key_hash
            FROM identity.bff_sessions s
            WHERE s.revoked_at IS NOT NULL OR s.expires_at <= v_now
            ORDER BY s.expires_at
            LIMIT p_batch_size
          )
          DELETE FROM identity.bff_sessions s
          USING candidates c
          WHERE s.session_key_hash = c.session_key_hash
            AND (s.revoked_at IS NOT NULL OR s.expires_at <= v_now);

          GET DIAGNOSTICS v_count = ROW_COUNT;

          -- BFF-LOGOUT-JTI-PERSISTENCE: the rest of the batch removes logout jtis past their retention, so
          -- the total never exceeds the batch and a short batch still means both tables are drained.
          IF v_count < p_batch_size THEN
            WITH candidates AS (
              SELECT j.jti_hash
              FROM identity.bff_logout_jtis j
              WHERE j.expires_at <= v_now
              ORDER BY j.expires_at
              LIMIT p_batch_size - v_count
            )
            DELETE FROM identity.bff_logout_jtis j
            USING candidates c
            WHERE j.jti_hash = c.jti_hash
              AND j.expires_at <= v_now;
            GET DIAGNOSTICS v_jtis = ROW_COUNT;
          END IF;

          RETURN v_count + v_jtis;
        END
        $function$;

        -- ACL first, while the deploying role still owns the function (see the OPS-003 lane).
        REVOKE ALL ON FUNCTION security.purge_bff_sessions(integer) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION security.purge_bff_sessions(integer) TO paqueteria_worker;
        ALTER FUNCTION security.purge_bff_sessions(integer) OWNER TO paqueteria_cleanup_executor;

        DO $verify$
        DECLARE
          purge oid := to_regprocedure('security.purge_bff_sessions(integer)');
        BEGIN
          IF purge IS NULL
             OR EXISTS (
               SELECT 1 FROM pg_proc p
               WHERE p.oid = purge
                 AND (pg_get_userbyid(p.proowner) <> 'paqueteria_cleanup_executor'
                   OR NOT p.prosecdef
                   OR p.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
                   OR NOT ('search_path=pg_catalog, identity, pg_temp' = ANY(COALESCE(p.proconfig, ARRAY[]::text[])))
                   OR has_function_privilege('public', p.oid, 'EXECUTE')
                   OR has_function_privilege('paqueteria_app', p.oid, 'EXECUTE')
                   OR NOT has_function_privilege('paqueteria_worker', p.oid, 'EXECUTE')))
          THEN
            RAISE EXCEPTION 'BFF session purge function security verification failed';
          END IF;

          -- The whole OPS-003 executor boundary, widened exactly by the BFF session purge grants.
          IF (SELECT array_agg(table_schema || '.' || table_name || ':' || privilege_type
                ORDER BY table_schema, table_name, privilege_type)
              FROM information_schema.table_privileges
              WHERE grantee='paqueteria_cleanup_executor')
               IS DISTINCT FROM ARRAY[
                 'identity.bff_logout_jtis:DELETE', 'identity.bff_sessions:DELETE', 'platform.idempotency_keys:DELETE']
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
                 'identity.bff_logout_jtis.expires_at:SELECT',
                 'identity.bff_logout_jtis.jti_hash:SELECT',
                 'identity.bff_sessions.expires_at:SELECT',
                 'identity.bff_sessions.revoked_at:SELECT',
                 'identity.bff_sessions.session_key_hash:SELECT',
                 'platform.idempotency_keys.created_at:SELECT',
                 'platform.idempotency_keys.expires_at:SELECT',
                 'platform.idempotency_keys.idempotency_key:SELECT',
                 'platform.idempotency_keys.owner_org_id:SELECT',
                 'platform.idempotency_keys.scope:SELECT'
               ]
          THEN
            RAISE EXCEPTION 'paqueteria_cleanup_executor privileges differ from the OPS-003-CLEANUP-ROLE contract';
          END IF;

          IF EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN (
                'organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
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
            OR NOT has_schema_privilege('paqueteria_cleanup_executor', 'identity', 'USAGE')
          THEN
            RAISE EXCEPTION 'paqueteria_cleanup_executor schema privileges differ from the OPS-003-CLEANUP-ROLE contract';
          END IF;

          IF pg_has_role('paqueteria_app', 'paqueteria_cleanup_executor', 'MEMBER')
             OR pg_has_role('paqueteria_worker', 'paqueteria_cleanup_executor', 'MEMBER')
             OR (SELECT count(*) FROM pg_proc WHERE proowner='paqueteria_cleanup_executor'::regrole) <> 3
          THEN
            RAISE EXCEPTION 'paqueteria_cleanup_executor ownership or membership differs from the OPS-003-CLEANUP-ROLE contract';
          END IF;
        END
        $verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """;

    /// <summary>
    /// Rollback: every purged row was already revoked or expired, so nothing is ever restored and the
    /// schema is not downgraded. Disable the job with <c>OperationalCleanup:BffSessions:Enabled=false</c>
    /// and, when the database itself has to stop purging, apply <see cref="OperationalRollbackSql"/>.
    /// </summary>
    public const string SchemaDowngradeNotSupportedSql =
        """
        DO $schema_downgrade$
        BEGIN
          RAISE EXCEPTION USING
            MESSAGE = 'OPS003_BFF_SESSION_PURGE_DOWNGRADE_NOT_SUPPORTED',
            DETAIL = 'BFF session purge rollback blocked: disable OperationalCleanup:BffSessions and revoke worker EXECUTE; purged sessions are never restored.',
            ERRCODE = 'P0001';
        END
        $schema_downgrade$;
        """;

    public const string OperationalRollbackSql =
        """
        REVOKE EXECUTE ON FUNCTION security.purge_bff_sessions(integer) FROM paqueteria_worker;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaDowngradeNotSupportedSql);
}
