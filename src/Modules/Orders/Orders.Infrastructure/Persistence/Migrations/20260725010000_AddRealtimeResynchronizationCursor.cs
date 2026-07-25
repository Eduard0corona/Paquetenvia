using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrdersDbContext))]
[Migration(MigrationId)]
public sealed class AddRealtimeResynchronizationCursor : Migration
{
    public const string MigrationId = "20260725010000_AddRealtimeResynchronizationCursor";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            RESET ROLE;

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

            ALTER FUNCTION security.get_public_tracking_projection(text)
              OWNER TO paqueteria_bootstrap;
            REVOKE ALL ON FUNCTION security.get_public_tracking_projection(text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION security.get_public_tracking_projection(text)
              TO paqueteria_app;
            GRANT SELECT (version) ON orders.orders TO paqueteria_bootstrap;

            DO $verify$
            BEGIN
              IF pg_get_userbyid(
                   (SELECT proowner FROM pg_proc
                    WHERE oid='security.get_public_tracking_projection(text)'::regprocedure)
                 ) <> 'paqueteria_bootstrap'
                 OR has_function_privilege(
                   'public',
                   'security.get_public_tracking_projection(text)',
                   'EXECUTE')
                 OR NOT has_function_privilege(
                   'paqueteria_app',
                   'security.get_public_tracking_projection(text)',
                   'EXECUTE')
                 OR NOT has_column_privilege(
                   'paqueteria_bootstrap',
                   'orders.orders',
                   'version',
                   'SELECT')
              THEN
                RAISE EXCEPTION 'RTM-002 public tracking cursor security verification failed';
              END IF;
            END
            $verify$;

            SET LOCAL ROLE paqueteria_migrator;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Published snapshot cursors remain compatible; rollback is deliberately non-destructive.
    }
}
