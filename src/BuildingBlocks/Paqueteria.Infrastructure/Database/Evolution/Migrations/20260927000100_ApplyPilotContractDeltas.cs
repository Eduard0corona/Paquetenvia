using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paqueteria.Infrastructure.Database.Evolution.Migrations;

/// <summary>
/// Pilot contract deltas decided by the project owner on 2026-09-27, for installations whose
/// canonical AI-06/AI-18 baseline predates them. Fresh installations already receive the same
/// objects from AI-06/AI-18; this lane runs after every module lane, so it also re-establishes the
/// AI-06 tracking projection that the RTM-002 Orders migration rewrites on a fresh install.
/// <list type="bullet">
/// <item>IDENTITY-ORG-ACTIVE-REQUIRED: <c>security.resolve_identity_context(text)</c> only counts
/// memberships inside an ACTIVE organization, with <c>SELECT(id,status)</c> on
/// <c>organizations.organizations</c> for <c>paqueteria_bootstrap</c>.</item>
/// <item>AI05-TIMELINE-ORDER: the public timeline is ordered by
/// <c>(occurred_at, aggregate_version)</c>, with <c>SELECT(aggregate_version)</c> on
/// <c>orders.order_events</c> for <c>paqueteria_bootstrap</c>.</item>
/// <item>AI06-PILOT-INDEXES: the outbox purge partial indexes and four operational indexes.</item>
/// </list>
/// Nothing is dropped and no row is rewritten. Both bootstrap functions keep their owner, their
/// ACL and their search_path; the lane refuses a pre-existing object whose shape differs.
/// </summary>
[DbContext(typeof(PlatformEvolutionDbContext))]
[Migration(MigrationId)]
public sealed class ApplyPilotContractDeltas : Migration
{
    public const string MigrationId = "20260927000100_ApplyPilotContractDeltas";

    /// <summary>Every index this lane owns, exactly as <c>pg_get_indexdef</c> renders it.</summary>
    public static readonly IReadOnlyList<(string Name, string Definition)> Indexes =
    [
        ("orders.orders_updated_at_idx",
            "CREATE INDEX orders_updated_at_idx ON orders.orders USING btree (updated_at)"),
        ("dispatch.assignments_driver_idx",
            "CREATE INDEX assignments_driver_idx ON dispatch.assignments USING btree (driver_id)"),
        ("dispatch.assignments_route_idx",
            "CREATE INDEX assignments_route_idx ON dispatch.assignments USING btree (route_id) WHERE (route_id IS NOT NULL)"),
        ("routes.route_stops_order_idx",
            "CREATE INDEX route_stops_order_idx ON routes.route_stops USING btree (order_id)"),
        ("platform.outbox_purge_processed_idx",
            "CREATE INDEX outbox_purge_processed_idx ON platform.outbox_events USING btree (processed_at) WHERE (status = 'PROCESSED'::text)"),
        ("platform.outbox_purge_dead_idx",
            "CREATE INDEX outbox_purge_dead_idx ON platform.outbox_events USING btree (COALESCE(processed_at, created_at)) WHERE (status = 'DEAD'::text)"),
        ("platform.location_outbox_purge_processed_idx",
            "CREATE INDEX location_outbox_purge_processed_idx ON platform.location_outbox_events USING btree (processed_at) WHERE (status = 'PROCESSED'::text)"),
        ("platform.location_outbox_purge_dead_idx",
            "CREATE INDEX location_outbox_purge_dead_idx ON platform.location_outbox_events USING btree (COALESCE(processed_at, created_at)) WHERE (status = 'DEAD'::text)"),
    ];

    public const string UpSql =
        """
        RESET ROLE;

        DO $adoption$
        BEGIN
          IF to_regprocedure('security.resolve_identity_context(text)') IS NULL
             OR to_regprocedure('security.get_public_tracking_projection(text)') IS NULL THEN
            RAISE EXCEPTION 'PILOT-DELTAS requires the canonical AI-06 bootstrap functions';
          END IF;

          IF EXISTS (
            SELECT 1 FROM pg_proc
            WHERE oid IN (
                'security.resolve_identity_context(text)'::regprocedure,
                'security.get_public_tracking_projection(text)'::regprocedure)
              AND (pg_get_userbyid(proowner) <> 'paqueteria_bootstrap' OR NOT prosecdef)
          ) THEN
            RAISE EXCEPTION 'PILOT-DELTAS requires both bootstrap functions owned by paqueteria_bootstrap as SECURITY DEFINER';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='organizations' AND table_name='organizations'
              AND column_name='status' AND data_type='text' AND is_nullable='NO'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='orders' AND table_name='order_events'
              AND column_name='aggregate_version' AND data_type='integer' AND is_nullable='NO'
          ) THEN
            RAISE EXCEPTION 'PILOT-DELTAS columns do not match the canonical AI-06 contract';
          END IF;

          IF to_regclass('orders.orders') IS NULL
             OR to_regclass('dispatch.assignments') IS NULL
             OR to_regclass('routes.route_stops') IS NULL
             OR to_regclass('platform.outbox_events') IS NULL
             OR to_regclass('platform.location_outbox_events') IS NULL THEN
            RAISE EXCEPTION 'PILOT-DELTAS requires the canonical AI-06 index targets';
          END IF;
        END
        $adoption$;

        -- CREATE OR REPLACE keeps the owner (paqueteria_bootstrap), the ACL and every attribute.
        CREATE OR REPLACE FUNCTION security.resolve_identity_context(p_identity_subject text)
        RETURNS jsonb
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, organizations, security, pg_temp
        AS $function$
        DECLARE v_result jsonb;
        BEGIN
          SELECT jsonb_build_object(
            'user_id', u.id,
            'status', u.status,
            'memberships', COALESCE(jsonb_agg(jsonb_build_object(
              'organization_id', m.organization_id,
              'role', m.role,
              'is_default', m.is_default
            ) ORDER BY m.is_default DESC, m.organization_id) FILTER (WHERE m.id IS NOT NULL), '[]'::jsonb)
          ) INTO v_result
          FROM identity.users u
          LEFT JOIN organizations.organization_memberships m
            ON m.user_id=u.id AND m.status='ACTIVE'
           -- IDENTITY-ORG-ACTIVE-REQUIRED: a membership only counts inside an ACTIVE organization.
           AND EXISTS (
             SELECT 1 FROM organizations.organizations org
             WHERE org.id=m.organization_id AND org.status='ACTIVE')
          WHERE u.identity_subject=p_identity_subject AND u.status='ACTIVE'
          GROUP BY u.id,u.status;
          RETURN v_result;
        END
        $function$;

        CREATE OR REPLACE FUNCTION security.get_public_tracking_projection(p_token text)
        RETURNS jsonb
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, extensions, orders, security, pg_temp
        AS $function$
        DECLARE v_result jsonb;
        BEGIN
          SELECT jsonb_build_object(
            'public_id', o.public_id,
            'public_status', security.map_public_order_status(o.status),
            'aggregate_version', o.version,
            'estimated_window', NULL,
            'timeline', COALESCE((
              SELECT jsonb_agg(
                jsonb_build_object('code',e.public_event_code,'occurred_at',e.occurred_at)
                ORDER BY e.occurred_at, e.aggregate_version
              )
              FROM orders.order_events e
              WHERE e.order_id=o.id
                AND e.public_event_code IS NOT NULL
            ), '[]'::jsonb)
          ) INTO v_result
          FROM orders.public_tracking_tokens t
          JOIN orders.orders o ON o.id=t.order_id
          WHERE t.token_hash=extensions.digest(pg_catalog.convert_to(p_token,'UTF8'),'sha256')
            AND t.revoked_at IS NULL
            AND t.expires_at > clock_timestamp()
            AND security.map_public_order_status(o.status) IS NOT NULL;
          RETURN v_result;
        END
        $function$;

        -- Grants and indexes are issued by the owner of every table they touch.
        SET LOCAL ROLE paqueteria_migrator;

        GRANT SELECT (id,status) ON organizations.organizations TO paqueteria_bootstrap;
        GRANT SELECT (aggregate_version) ON orders.order_events TO paqueteria_bootstrap;

        CREATE INDEX IF NOT EXISTS orders_updated_at_idx ON orders.orders(updated_at);
        CREATE INDEX IF NOT EXISTS assignments_driver_idx ON dispatch.assignments(driver_id);
        CREATE INDEX IF NOT EXISTS assignments_route_idx ON dispatch.assignments(route_id) WHERE route_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS route_stops_order_idx ON routes.route_stops(order_id);
        CREATE INDEX IF NOT EXISTS outbox_purge_processed_idx
          ON platform.outbox_events(processed_at) WHERE status='PROCESSED';
        CREATE INDEX IF NOT EXISTS outbox_purge_dead_idx
          ON platform.outbox_events((COALESCE(processed_at,created_at))) WHERE status='DEAD';
        CREATE INDEX IF NOT EXISTS location_outbox_purge_processed_idx
          ON platform.location_outbox_events(processed_at) WHERE status='PROCESSED';
        CREATE INDEX IF NOT EXISTS location_outbox_purge_dead_idx
          ON platform.location_outbox_events((COALESCE(processed_at,created_at))) WHERE status='DEAD';

        RESET ROLE;

        DO $verify$
        DECLARE
          identity_fn oid := to_regprocedure('security.resolve_identity_context(text)');
          tracking_fn oid := to_regprocedure('security.get_public_tracking_projection(text)');
        BEGIN
          IF EXISTS (
            SELECT 1 FROM pg_proc
            WHERE oid IN (identity_fn, tracking_fn)
              AND (pg_get_userbyid(proowner) <> 'paqueteria_bootstrap'
                OR NOT prosecdef
                OR prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
                OR has_function_privilege('public', oid, 'EXECUTE')
                OR has_function_privilege('paqueteria_worker', oid, 'EXECUTE')
                OR NOT has_function_privilege('paqueteria_app', oid, 'EXECUTE'))
          ) OR NOT ('search_path=pg_catalog, identity, organizations, security, pg_temp' = ANY(
                COALESCE((SELECT proconfig FROM pg_proc WHERE oid=identity_fn), ARRAY[]::text[])))
            OR NOT ('search_path=pg_catalog, extensions, orders, security, pg_temp' = ANY(
                COALESCE((SELECT proconfig FROM pg_proc WHERE oid=tracking_fn), ARRAY[]::text[])))
            OR position('org.status=''ACTIVE''' IN (SELECT prosrc FROM pg_proc WHERE oid=identity_fn)) = 0
            OR position('ORDER BY e.occurred_at, e.aggregate_version' IN
                (SELECT prosrc FROM pg_proc WHERE oid=tracking_fn)) = 0
          THEN
            RAISE EXCEPTION 'PILOT-DELTAS bootstrap function security verification failed';
          END IF;

          -- The exact AI-18 bootstrap column set: nothing missing, nothing extra, SELECT only.
          IF (SELECT array_agg(table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type
                ORDER BY table_schema, table_name, column_name, privilege_type)
              FROM information_schema.column_privileges
              WHERE grantee='paqueteria_bootstrap')
             IS DISTINCT FROM ARRAY[
               'identity.users.id:SELECT',
               'identity.users.identity_subject:SELECT',
               'identity.users.status:SELECT',
               'orders.order_events.aggregate_version:SELECT',
               'orders.order_events.occurred_at:SELECT',
               'orders.order_events.order_id:SELECT',
               'orders.order_events.public_event_code:SELECT',
               'orders.orders.id:SELECT',
               'orders.orders.public_id:SELECT',
               'orders.orders.status:SELECT',
               'orders.orders.version:SELECT',
               'orders.public_tracking_tokens.expires_at:SELECT',
               'orders.public_tracking_tokens.id:SELECT',
               'orders.public_tracking_tokens.order_id:SELECT',
               'orders.public_tracking_tokens.revoked_at:SELECT',
               'orders.public_tracking_tokens.token_hash:SELECT',
               'organizations.organization_memberships.id:SELECT',
               'organizations.organization_memberships.is_default:SELECT',
               'organizations.organization_memberships.organization_id:SELECT',
               'organizations.organization_memberships.role:SELECT',
               'organizations.organization_memberships.status:SELECT',
               'organizations.organization_memberships.user_id:SELECT',
               'organizations.organizations.id:SELECT',
               'organizations.organizations.status:SELECT'
             ]
             OR (SELECT count(*) FROM information_schema.table_privileges
                 WHERE grantee='paqueteria_bootstrap') <> 0
          THEN
            RAISE EXCEPTION 'paqueteria_bootstrap column privileges differ from the AI-18 contract';
          END IF;

          IF (SELECT count(*)
              FROM (VALUES
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
                 'CREATE INDEX location_outbox_purge_dead_idx ON platform.location_outbox_events USING btree (COALESCE(processed_at, created_at)) WHERE (status = ''DEAD''::text)')
              ) expected(name, definition)
              JOIN pg_index i ON i.indexrelid = to_regclass(expected.name)
              WHERE i.indisvalid AND i.indisready
                AND pg_get_indexdef(i.indexrelid) = expected.definition
                AND pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid=i.indexrelid)) = 'paqueteria_migrator') <> 8
          THEN
            RAISE EXCEPTION 'PILOT-DELTAS index set differs from the canonical AI-06 contract';
          END IF;
        END
        $verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """;

    /// <summary>
    /// Returns the installation to the pre-decision AI-06/AI-18 contract: the previous bootstrap
    /// function bodies, the previous bootstrap column grants and no pilot index. Rolling back
    /// re-admits memberships of SUSPENDED and CLOSED organizations, so it must travel with the
    /// previous application release, never alone.
    /// </summary>
    public const string DownSql =
        """
        RESET ROLE;

        CREATE OR REPLACE FUNCTION security.resolve_identity_context(p_identity_subject text)
        RETURNS jsonb
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, organizations, security, pg_temp
        AS $function$
        DECLARE v_result jsonb;
        BEGIN
          SELECT jsonb_build_object(
            'user_id', u.id,
            'status', u.status,
            'memberships', COALESCE(jsonb_agg(jsonb_build_object(
              'organization_id', m.organization_id,
              'role', m.role,
              'is_default', m.is_default
            ) ORDER BY m.is_default DESC, m.organization_id) FILTER (WHERE m.id IS NOT NULL), '[]'::jsonb)
          ) INTO v_result
          FROM identity.users u
          LEFT JOIN organizations.organization_memberships m
            ON m.user_id=u.id AND m.status='ACTIVE'
          WHERE u.identity_subject=p_identity_subject AND u.status='ACTIVE'
          GROUP BY u.id,u.status;
          RETURN v_result;
        END
        $function$;

        CREATE OR REPLACE FUNCTION security.get_public_tracking_projection(p_token text)
        RETURNS jsonb
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, extensions, orders, security, pg_temp
        AS $function$
        DECLARE v_result jsonb;
        BEGIN
          SELECT jsonb_build_object(
            'public_id', o.public_id,
            'public_status', security.map_public_order_status(o.status),
            'aggregate_version', o.version,
            'estimated_window', NULL,
            'timeline', COALESCE((
              SELECT jsonb_agg(
                jsonb_build_object('code',e.public_event_code,'occurred_at',e.occurred_at)
                ORDER BY e.occurred_at
              )
              FROM orders.order_events e
              WHERE e.order_id=o.id
                AND e.public_event_code IS NOT NULL
            ), '[]'::jsonb)
          ) INTO v_result
          FROM orders.public_tracking_tokens t
          JOIN orders.orders o ON o.id=t.order_id
          WHERE t.token_hash=extensions.digest(pg_catalog.convert_to(p_token,'UTF8'),'sha256')
            AND t.revoked_at IS NULL
            AND t.expires_at > clock_timestamp()
            AND security.map_public_order_status(o.status) IS NOT NULL;
          RETURN v_result;
        END
        $function$;

        SET LOCAL ROLE paqueteria_migrator;

        REVOKE SELECT (id,status) ON organizations.organizations FROM paqueteria_bootstrap;
        REVOKE SELECT (aggregate_version) ON orders.order_events FROM paqueteria_bootstrap;

        DROP INDEX IF EXISTS orders.orders_updated_at_idx;
        DROP INDEX IF EXISTS dispatch.assignments_driver_idx;
        DROP INDEX IF EXISTS dispatch.assignments_route_idx;
        DROP INDEX IF EXISTS routes.route_stops_order_idx;
        DROP INDEX IF EXISTS platform.outbox_purge_processed_idx;
        DROP INDEX IF EXISTS platform.outbox_purge_dead_idx;
        DROP INDEX IF EXISTS platform.location_outbox_purge_processed_idx;
        DROP INDEX IF EXISTS platform.location_outbox_purge_dead_idx;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
