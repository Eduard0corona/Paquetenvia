using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Persistence.Migrations;

/// <summary>
/// ORD-AUTO-CLOSE-2026-10-10 (project owner: "Sí, que se cierre sola"). A Worker job closes every DELIVERED order whose
/// CLOSED guards hold, through the ORD-002 transition in the owner organization's own tenant transaction. The Worker
/// is NOBYPASSRLS and <c>orders.orders</c> forces RLS, so the job cannot know which organizations to visit. This lane
/// installs the only cross-tenant step: <c>security.list_auto_close_owner_organizations(uuid, integer)</c>, owned by
/// the dedicated <c>paqueteria_auto_close_executor NOLOGIN BYPASSRLS</c> role and executable only by
/// <c>paqueteria_worker</c>. It returns owner organization identifiers that hold at least one DELIVERED order and
/// nothing else: no order identifier, status or guard input crosses the privilege boundary, it writes nothing and it
/// decides nothing (the guards run in C#, in the transition). AI-18 creates the role on fresh installations;
/// populated installations predate it, so this lane creates it idempotently and refuses any pre-existing role or
/// table shape that differs from the contract.
/// </summary>
/// <remarks>
/// Rollback drops only the function: the role and its two column grants are declared by AI-18 and stay inert without
/// it, and every order the job already closed is a legitimate ORD-002 transition (append-only event and audit) that
/// is never reverted. Operationally the job stops with <c>Orders:AutoClose:Enabled=false</c>.
/// </remarks>
[DbContext(typeof(OrdersDbContext))]
[Migration(MigrationId)]
public sealed class AddOrderAutoCloseDiscovery : Migration
{
    public const string MigrationId = "20261010000100_AddOrderAutoCloseDiscovery";
    public const string DecisionId = "ORD-AUTO-CLOSE-2026-10-10";
    public const string FunctionSignature = "security.list_auto_close_owner_organizations(uuid,integer)";
    public const string ExecutorRole = "paqueteria_auto_close_executor";
    public const string SearchPath = "search_path=pg_catalog, orders, pg_temp";
    public const int MaximumLimit = 1_000;

    /// <summary>The executor's exact column grants (schema.table.column:privilege), ordered as the catalog check sorts them.</summary>
    public static IReadOnlyList<string> ExecutorColumnGrants { get; } = Array.AsReadOnly(new[]
    {
        "orders.orders.owner_org_id:SELECT",
        "orders.orders.status:SELECT",
    });

    /// <summary>The grant statements, identical to the ones AI-18 declares for fresh installations.</summary>
    public const string GrantSql =
        """
        REVOKE paqueteria_auto_close_executor FROM paqueteria_app, paqueteria_worker;
        GRANT USAGE ON SCHEMA orders TO paqueteria_auto_close_executor;
        GRANT SELECT (owner_org_id,status) ON orders.orders TO paqueteria_auto_close_executor;
        """;

    public const string FunctionSql =
        """
        CREATE OR REPLACE FUNCTION security.list_auto_close_owner_organizations(
          p_after_owner_org_id uuid,
          p_limit integer)
        RETURNS SETOF uuid
        LANGUAGE plpgsql
        STABLE
        SECURITY DEFINER
        SET search_path = pg_catalog, orders, pg_temp
        AS $function$
        BEGIN
          IF p_limit IS NULL OR p_limit < 1 OR p_limit > 1000 THEN
            RAISE EXCEPTION USING
              ERRCODE = '22023',
              MESSAGE = 'ORD_AUTO_CLOSE_LIMIT_OUT_OF_RANGE';
          END IF;

          -- ORD-AUTO-CLOSE-2026-10-10: only the owner organizations that hold at least one DELIVERED order, strictly
          -- after the given one, in ascending order. The Worker reads the DELIVERED orders of each owner under FORCE
          -- RLS in the tenant transaction of that owner and evaluates every CLOSED guard in the ORD-002 transition.
          RETURN QUERY
          SELECT DISTINCT o.owner_org_id
          FROM orders.orders o
          WHERE o.status = 'DELIVERED'
            AND (p_after_owner_org_id IS NULL OR o.owner_org_id > p_after_owner_org_id)
          ORDER BY o.owner_org_id
          LIMIT p_limit;
        END
        $function$;
        """;

    public static readonly string UpSql =
        $$"""
        RESET ROLE;

        DO $adoption$
        DECLARE
          order_columns text[];
        BEGIN
          IF to_regclass('orders.orders') IS NULL THEN
            RAISE EXCEPTION 'ORD-AUTO-CLOSE requires the canonical AI-06 orders.orders table';
          END IF;

          SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY column_name)
          INTO order_columns
          FROM information_schema.columns
          WHERE table_schema='orders' AND table_name='orders'
            AND column_name IN ('owner_org_id','status');
          IF order_columns IS DISTINCT FROM ARRAY[
            'owner_org_id:uuid:NO',
            'status:text:NO'
          ] THEN
            RAISE EXCEPTION 'orders.orders discovery columns do not match the canonical AI-06 contract';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='orders.orders'::regclass AND relrowsecurity AND relforcerowsecurity
          ) THEN
            RAISE EXCEPTION 'ORD-AUTO-CLOSE requires FORCE ROW LEVEL SECURITY on orders.orders';
          END IF;

          IF to_regclass('orders.orders_owner_status_idx') IS NULL THEN
            RAISE EXCEPTION 'ORD-AUTO-CLOSE requires the canonical AI-06 orders_owner_status_idx';
          END IF;
        END
        $adoption$;

        DO $role$
        BEGIN
          CREATE ROLE paqueteria_auto_close_executor NOLOGIN BYPASSRLS;
        EXCEPTION WHEN duplicate_object THEN NULL;
        END
        $role$;

        DO $existing_role$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM pg_roles
            WHERE rolname='paqueteria_auto_close_executor'
              AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication
                OR NOT rolbypassrls OR NOT rolinherit)
          ) THEN
            RAISE EXCEPTION 'paqueteria_auto_close_executor exists with attributes outside the ORD-AUTO-CLOSE contract';
          END IF;
          IF EXISTS (
            SELECT 1 FROM pg_auth_members m
            WHERE m.member='paqueteria_auto_close_executor'::regrole
          ) THEN
            RAISE EXCEPTION 'paqueteria_auto_close_executor must not inherit any other role';
          END IF;
          IF EXISTS (SELECT 1 FROM pg_class WHERE relowner='paqueteria_auto_close_executor'::regrole)
             OR EXISTS (SELECT 1 FROM pg_namespace WHERE nspowner='paqueteria_auto_close_executor'::regrole)
             OR EXISTS (SELECT 1 FROM pg_type WHERE typowner='paqueteria_auto_close_executor'::regrole)
             OR EXISTS (
               SELECT 1 FROM pg_proc
               WHERE proowner='paqueteria_auto_close_executor'::regrole
                 AND oid IS DISTINCT FROM to_regprocedure('{{FunctionSignature}}')
             ) THEN
            RAISE EXCEPTION 'paqueteria_auto_close_executor already owns objects outside the ORD-AUTO-CLOSE contract';
          END IF;
        END
        $existing_role$;

        {{GrantSql}}

        {{FunctionSql}}

        -- ACL first, while the deploying role still owns the function: ALTER ... OWNER rewrites the grantor to the
        -- new owner, and a managed-service deployer then needs no inherited executor rights.
        REVOKE ALL ON FUNCTION {{FunctionSignature}} FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION {{FunctionSignature}} TO paqueteria_worker;
        ALTER FUNCTION {{FunctionSignature}} OWNER TO paqueteria_auto_close_executor;

        DO $verify$
        DECLARE
          executor oid := 'paqueteria_auto_close_executor'::regrole;
          fn oid := to_regprocedure('{{FunctionSignature}}');
        BEGIN
          IF fn IS NULL
             OR (SELECT proowner FROM pg_proc WHERE oid=fn) <> executor
             OR NOT (SELECT prosecdef FROM pg_proc WHERE oid=fn)
             OR (SELECT provolatile FROM pg_proc WHERE oid=fn) <> 's'
             OR (SELECT proconfig FROM pg_proc WHERE oid=fn) IS DISTINCT FROM ARRAY['{{SearchPath}}']
             OR (SELECT prosrc FROM pg_proc WHERE oid=fn) ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
             OR (SELECT prosrc FROM pg_proc WHERE oid=fn) ~* 'RETURNING'
             OR (SELECT string_agg(
                   CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(acl.grantee) END
                     || ':' || acl.privilege_type || ':' || acl.is_grantable::text, ','
                   ORDER BY (CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(acl.grantee) END) COLLATE "C")
                 FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) acl WHERE p.oid=fn)
                IS DISTINCT FROM 'paqueteria_auto_close_executor:EXECUTE:false,paqueteria_worker:EXECUTE:false'
          THEN
            RAISE EXCEPTION 'ORD-AUTO-CLOSE function security verification failed';
          END IF;

          -- Exactly SELECT(owner_org_id,status) on orders.orders, none grantable, and no table-wide grant anywhere.
          IF EXISTS (
               SELECT 1 FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) acl
               WHERE c.relacl IS NOT NULL AND acl.grantee=executor)
             OR (SELECT array_agg(n.nspname || '.' || c.relname || '.' || a.attname || ':' || acl.privilege_type
                   || CASE WHEN acl.is_grantable THEN ':grantable' ELSE '' END
                   ORDER BY (n.nspname || '.' || c.relname || '.' || a.attname || ':' || acl.privilege_type) COLLATE "C")
                 FROM pg_attribute a
                 JOIN pg_class c ON c.oid=a.attrelid
                 JOIN pg_namespace n ON n.oid=c.relnamespace
                 CROSS JOIN LATERAL aclexplode(a.attacl) acl
                 WHERE a.attacl IS NOT NULL AND acl.grantee=executor)
               IS DISTINCT FROM ARRAY[{{string.Join(",", ExecutorColumnGrants.Select(grant => "'" + grant + "'"))}}]
          THEN
            RAISE EXCEPTION 'paqueteria_auto_close_executor privileges differ from the ORD-AUTO-CLOSE contract';
          END IF;

          -- USAGE on orders only; CREATE nowhere once any E-002 temporary grant on security is revoked by the deployer
          -- (asserted again after the bridge cleanup).
          IF EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN (
                'identity','organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
                'custody','incidents','finance','allies','notifications','reporting','platform','security','extensions')
              AND has_schema_privilege(executor, n.oid, 'USAGE') IS DISTINCT FROM (n.nspname = 'orders')
          ) OR EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN (
                'identity','organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
                'custody','incidents','finance','allies','notifications','reporting','platform','extensions')
              AND has_schema_privilege(executor, n.oid, 'CREATE')
          ) THEN
            RAISE EXCEPTION 'paqueteria_auto_close_executor schema privileges differ from the ORD-AUTO-CLOSE contract';
          END IF;

          IF pg_has_role('paqueteria_app', executor, 'MEMBER')
             OR pg_has_role('paqueteria_worker', executor, 'MEMBER')
             OR pg_has_role('paqueteria_bootstrap', executor, 'MEMBER')
             OR (SELECT count(*) FROM pg_proc WHERE proowner=executor) <> 1
          THEN
            RAISE EXCEPTION 'paqueteria_auto_close_executor ownership or membership differs from the ORD-AUTO-CLOSE contract';
          END IF;
        END
        $verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """;

    /// <summary>
    /// Rollback: only the function goes. The role and its grants are declared by AI-18 and are inert without it; every
    /// order already closed stays CLOSED with its append-only event and audit rows.
    /// </summary>
    public const string DownSql =
        $$"""
        RESET ROLE;
        DROP FUNCTION IF EXISTS {{FunctionSignature}};
        SET LOCAL ROLE paqueteria_migrator;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
