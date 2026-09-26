using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Persistence.Migrations;

/// <summary>
/// LIF-001 / ADR-034 installs the only cross-tenant path that finalizes expired claim windows:
/// <c>security.finalize_expired_orders(integer)</c>, owned by the dedicated
/// <c>paqueteria_lifecycle_executor NOLOGIN BYPASSRLS</c> role and executable only by
/// <c>paqueteria_worker</c>. The function discovers, locks and finalizes in one statement, so no
/// tenant or order identifier ever crosses the privilege boundary. AI-18 creates the role on fresh
/// installations; populated installations predate it, so this lane creates it idempotently and
/// refuses any pre-existing role or table shape that differs from the contract. Nothing is dropped
/// and no row is rewritten.
/// </summary>
[DbContext(typeof(OrdersDbContext))]
[Migration(MigrationId)]
public sealed class AddOrderLifecycleFinalizationExecutor : Migration
{
    public const string MigrationId = "20260925020000_AddOrderLifecycleFinalizationExecutor";
    public const string FunctionSignature = "security.finalize_expired_orders(integer)";
    public const string ExecutorRole = "paqueteria_lifecycle_executor";
    public const int MaximumBatchSize = 1_000;

    public const string UpSql =
        """
        RESET ROLE;

        DO $adoption$
        DECLARE
          order_columns text[];
        BEGIN
          IF to_regclass('orders.orders') IS NULL THEN
            RAISE EXCEPTION 'LIF-001 requires the canonical AI-06 orders.orders table';
          END IF;

          SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY column_name)
          INTO order_columns
          FROM information_schema.columns
          WHERE table_schema='orders' AND table_name='orders'
            AND column_name IN ('archived_at','claim_window_ends_at','finalized_at','id','status');
          IF order_columns IS DISTINCT FROM ARRAY[
            'archived_at:timestamp with time zone:YES',
            'claim_window_ends_at:timestamp with time zone:YES',
            'finalized_at:timestamp with time zone:YES',
            'id:uuid:NO',
            'status:text:NO'
          ] THEN
            RAISE EXCEPTION 'orders.orders lifecycle columns do not match the canonical AI-06 contract';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='orders.orders'::regclass AND relrowsecurity AND relforcerowsecurity
          ) THEN
            RAISE EXCEPTION 'LIF-001 requires FORCE ROW LEVEL SECURITY on orders.orders';
          END IF;

          IF to_regclass('orders.orders_claim_window_idx') IS NULL THEN
            RAISE EXCEPTION 'LIF-001 requires the canonical AI-06 orders_claim_window_idx';
          END IF;

          -- Finalization writes finalized_at and nothing else; a user trigger could widen that.
          IF EXISTS (
            SELECT 1 FROM pg_trigger
            WHERE tgrelid='orders.orders'::regclass AND NOT tgisinternal
          ) THEN
            RAISE EXCEPTION 'LIF-001 refuses orders.orders with user triggers: finalization must not mutate other columns';
          END IF;
        END
        $adoption$;

        DO $role$
        BEGIN
          CREATE ROLE paqueteria_lifecycle_executor NOLOGIN BYPASSRLS;
        EXCEPTION WHEN duplicate_object THEN NULL;
        END
        $role$;

        DO $existing_role$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM pg_roles
            WHERE rolname='paqueteria_lifecycle_executor'
              AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication
                OR NOT rolbypassrls OR NOT rolinherit)
          ) THEN
            RAISE EXCEPTION 'paqueteria_lifecycle_executor exists with attributes outside the ADR-034 contract';
          END IF;
          IF EXISTS (
            SELECT 1 FROM pg_auth_members m
            WHERE m.member='paqueteria_lifecycle_executor'::regrole
          ) THEN
            RAISE EXCEPTION 'paqueteria_lifecycle_executor must not inherit any other role';
          END IF;
          IF EXISTS (SELECT 1 FROM pg_class WHERE relowner='paqueteria_lifecycle_executor'::regrole)
             OR EXISTS (SELECT 1 FROM pg_namespace WHERE nspowner='paqueteria_lifecycle_executor'::regrole)
             OR EXISTS (
               SELECT 1 FROM pg_proc
               WHERE proowner='paqueteria_lifecycle_executor'::regrole
                 AND oid IS DISTINCT FROM to_regprocedure('security.finalize_expired_orders(integer)')
             ) THEN
            RAISE EXCEPTION 'paqueteria_lifecycle_executor already owns objects outside the ADR-034 contract';
          END IF;
        END
        $existing_role$;

        REVOKE paqueteria_lifecycle_executor FROM paqueteria_app, paqueteria_worker;

        GRANT USAGE ON SCHEMA orders TO paqueteria_lifecycle_executor;
        GRANT SELECT (id,status,claim_window_ends_at,finalized_at) ON orders.orders
          TO paqueteria_lifecycle_executor;
        GRANT UPDATE (finalized_at) ON orders.orders TO paqueteria_lifecycle_executor;

        CREATE OR REPLACE FUNCTION security.finalize_expired_orders(p_batch_size integer)
        RETURNS integer
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, orders, pg_temp
        AS $function$
        DECLARE
          v_now timestamptz := pg_catalog.clock_timestamp();
          v_finalized integer;
        BEGIN
          IF p_batch_size IS NULL OR p_batch_size < 1 OR p_batch_size > 1000 THEN
            RAISE EXCEPTION USING
              ERRCODE = '22023',
              MESSAGE = 'LIF001_BATCH_SIZE_OUT_OF_RANGE';
          END IF;

          -- ADR-024/AI-04: a claim is admissible while now <= claim_window_ends_at, so only a
          -- strictly elapsed window is final. One captured instant drives eligibility and value.
          WITH candidates AS (
            SELECT o.id
            FROM orders.orders o
            WHERE o.status = 'CLOSED'
              AND o.finalized_at IS NULL
              AND o.claim_window_ends_at IS NOT NULL
              AND o.claim_window_ends_at < v_now
            ORDER BY o.claim_window_ends_at, o.id
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED
          )
          UPDATE orders.orders o
          SET finalized_at = v_now
          FROM candidates c
          WHERE o.id = c.id
            AND o.status = 'CLOSED'
            AND o.finalized_at IS NULL
            AND o.claim_window_ends_at IS NOT NULL
            AND o.claim_window_ends_at < v_now;

          GET DIAGNOSTICS v_finalized = ROW_COUNT;
          RETURN v_finalized;
        END
        $function$;

        ALTER FUNCTION security.finalize_expired_orders(integer) OWNER TO paqueteria_lifecycle_executor;
        REVOKE ALL ON FUNCTION security.finalize_expired_orders(integer) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION security.finalize_expired_orders(integer) TO paqueteria_worker;

        DO $verify$
        DECLARE
          fn oid := to_regprocedure('security.finalize_expired_orders(integer)');
        BEGIN
          IF fn IS NULL
             OR pg_get_userbyid((SELECT proowner FROM pg_proc WHERE oid=fn)) <> 'paqueteria_lifecycle_executor'
             OR NOT (SELECT prosecdef FROM pg_proc WHERE oid=fn)
             OR NOT ('search_path=pg_catalog, orders, pg_temp' = ANY(
               COALESCE((SELECT proconfig FROM pg_proc WHERE oid=fn), ARRAY[]::text[])))
             OR (SELECT prosrc FROM pg_proc WHERE oid=fn) ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
             OR has_function_privilege('public', fn, 'EXECUTE')
             OR has_function_privilege('paqueteria_app', fn, 'EXECUTE')
             OR NOT has_function_privilege('paqueteria_worker', fn, 'EXECUTE')
          THEN
            RAISE EXCEPTION 'LIF-001 finalization function security verification failed';
          END IF;

          -- Exactly USAGE on orders plus column SELECT(id,status,claim_window_ends_at,finalized_at)
          -- and UPDATE(finalized_at) on orders.orders; nothing table-wide, nothing elsewhere.
          IF (SELECT count(*) FROM information_schema.table_privileges
              WHERE grantee='paqueteria_lifecycle_executor') <> 0
             OR (SELECT array_agg(table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type
                   ORDER BY table_schema, table_name, privilege_type, column_name)
                 FROM information_schema.column_privileges
                 WHERE grantee='paqueteria_lifecycle_executor')
               IS DISTINCT FROM ARRAY[
                 'orders.orders.claim_window_ends_at:SELECT',
                 'orders.orders.finalized_at:SELECT',
                 'orders.orders.id:SELECT',
                 'orders.orders.status:SELECT',
                 'orders.orders.finalized_at:UPDATE'
               ]
          THEN
            RAISE EXCEPTION 'paqueteria_lifecycle_executor privileges differ from the ADR-034 contract';
          END IF;

          IF EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN (
                'identity','organizations','clients','locations','pricing','dispatch','drivers','routes',
                'custody','incidents','finance','allies','notifications','reporting','platform','security','extensions')
              AND (has_schema_privilege('paqueteria_lifecycle_executor', n.oid, 'USAGE')
                OR has_schema_privilege('paqueteria_lifecycle_executor', n.oid, 'CREATE'))
          ) OR has_schema_privilege('paqueteria_lifecycle_executor', 'orders', 'CREATE')
            OR NOT has_schema_privilege('paqueteria_lifecycle_executor', 'orders', 'USAGE')
          THEN
            RAISE EXCEPTION 'paqueteria_lifecycle_executor schema privileges differ from the ADR-034 contract';
          END IF;

          IF pg_has_role('paqueteria_app', 'paqueteria_lifecycle_executor', 'MEMBER')
             OR pg_has_role('paqueteria_worker', 'paqueteria_lifecycle_executor', 'MEMBER')
             OR (SELECT count(*) FROM pg_proc WHERE proowner='paqueteria_lifecycle_executor'::regrole) <> 1
          THEN
            RAISE EXCEPTION 'paqueteria_lifecycle_executor ownership or membership differs from the ADR-034 contract';
          END IF;
        END
        $verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """;

    /// <summary>
    /// ADR-034 rollback: the schema is never downgraded, because every finalized_at already written
    /// is a legitimate ADR-024 lifecycle fact. Disable the job with
    /// <c>Orders:ClaimWindowFinalization:Enabled=false</c> and, when the database itself has to stop
    /// finalizing, apply <see cref="OperationalRollbackSql"/> with the deployment credential.
    /// </summary>
    public const string SchemaDowngradeNotSupportedSql =
        """
        DO $schema_downgrade$
        BEGIN
          RAISE EXCEPTION USING
            MESSAGE = 'LIF001_SCHEMA_DOWNGRADE_NOT_SUPPORTED',
            DETAIL = 'LIF-001 rollback blocked: disable Orders:ClaimWindowFinalization and revoke worker EXECUTE; finalized_at values are never reverted.',
            ERRCODE = 'P0001';
        END
        $schema_downgrade$;
        """;

    public const string OperationalRollbackSql =
        """
        REVOKE EXECUTE ON FUNCTION security.finalize_expired_orders(integer) FROM paqueteria_worker;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaDowngradeNotSupportedSql);
}
