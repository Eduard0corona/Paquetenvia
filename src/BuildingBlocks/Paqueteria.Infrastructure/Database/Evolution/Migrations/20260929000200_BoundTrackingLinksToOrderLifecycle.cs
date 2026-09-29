using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Paqueteria.Infrastructure.Database.Evolution.Migrations;

/// <summary>
/// TRK-002-AUTO-LINK: a public tracking link lives while its order is in progress and for 24 hours after the order
/// first reaches DELIVERED, RETURNED or CANCELLED for the public (DELIVERED also covers CLOSED, CLAIM_OPEN and
/// CLAIM_RESOLVED; RESCHEDULED stays in progress). The check sits in
/// <c>security.get_public_tracking_projection(text)</c>, atomically with the token lookup, and fails closed: a
/// finished order without its final event resolves to nothing. It reads only columns
/// <c>paqueteria_bootstrap</c> already holds (<c>order_events.order_id</c>, <c>public_event_code</c>,
/// <c>occurred_at</c>), so no grant changes. The hash lookup, the revocation and the <c>expires_at</c> ceiling are
/// unchanged. This lane runs after every module lane and after the pilot deltas, which re-establish the previous
/// body on a fresh installation; CREATE OR REPLACE keeps the owner, the ACL and the search_path.
/// </summary>
[DbContext(typeof(PlatformEvolutionDbContext))]
[Migration(MigrationId)]
public sealed class BoundTrackingLinksToOrderLifecycle : Migration
{
    public const string MigrationId = "20260929000200_BoundTrackingLinksToOrderLifecycle";
    public const string LifecycleMarker = "TRK-002-AUTO-LINK";

    public const string UpSql =
        """
        RESET ROLE;

        DO $adoption$
        BEGIN
          IF to_regprocedure('security.get_public_tracking_projection(text)') IS NULL
             OR to_regprocedure('security.map_public_order_status(text)') IS NULL THEN
            RAISE EXCEPTION 'TRK-002-AUTO-LINK requires the canonical AI-06 tracking functions';
          END IF;

          IF EXISTS (
            SELECT 1 FROM pg_proc
            WHERE oid='security.get_public_tracking_projection(text)'::regprocedure
              AND (pg_get_userbyid(proowner) <> 'paqueteria_bootstrap' OR NOT prosecdef)
          ) THEN
            RAISE EXCEPTION 'TRK-002-AUTO-LINK requires the tracking projection owned by paqueteria_bootstrap as SECURITY DEFINER';
          END IF;

          IF NOT has_column_privilege('paqueteria_bootstrap','orders.order_events','order_id','SELECT')
             OR NOT has_column_privilege('paqueteria_bootstrap','orders.order_events','public_event_code','SELECT')
             OR NOT has_column_privilege('paqueteria_bootstrap','orders.order_events','occurred_at','SELECT')
          THEN
            RAISE EXCEPTION 'TRK-002-AUTO-LINK requires the AI-18 bootstrap column grants on orders.order_events';
          END IF;
        END
        $adoption$;

        -- CREATE OR REPLACE keeps the owner (paqueteria_bootstrap), the ACL and every attribute.
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
            AND security.map_public_order_status(o.status) IS NOT NULL
            -- TRK-002-AUTO-LINK: valid while the order is in progress, then for 24 hours after the first event that took
            -- it to DELIVERED, RETURNED or CANCELLED (public statuses that cannot be left). No such event: fail closed.
            AND (security.map_public_order_status(o.status) NOT IN ('DELIVERED','RETURNED','CANCELLED')
              OR clock_timestamp() < (
                SELECT min(f.occurred_at)
                FROM orders.order_events f
                WHERE f.order_id=o.id
                  AND f.public_event_code IN ('DELIVERED','RETURNED','CANCELLED')
              ) + interval '24 hours');
          RETURN v_result;
        END
        $function$;

        DO $verify$
        DECLARE
          tracking_fn oid := to_regprocedure('security.get_public_tracking_projection(text)');
        BEGIN
          IF EXISTS (
            SELECT 1 FROM pg_proc
            WHERE oid = tracking_fn
              AND (pg_get_userbyid(proowner) <> 'paqueteria_bootstrap'
                OR NOT prosecdef
                OR prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
                OR has_function_privilege('public', oid, 'EXECUTE')
                OR has_function_privilege('paqueteria_worker', oid, 'EXECUTE')
                OR NOT has_function_privilege('paqueteria_app', oid, 'EXECUTE'))
          ) OR NOT ('search_path=pg_catalog, extensions, orders, security, pg_temp' = ANY(
                COALESCE((SELECT proconfig FROM pg_proc WHERE oid=tracking_fn), ARRAY[]::text[])))
            OR position('ORDER BY e.occurred_at, e.aggregate_version' IN
                (SELECT prosrc FROM pg_proc WHERE oid=tracking_fn)) = 0
            OR position('TRK-002-AUTO-LINK' IN (SELECT prosrc FROM pg_proc WHERE oid=tracking_fn)) = 0
            OR position('+ interval ''24 hours''' IN (SELECT prosrc FROM pg_proc WHERE oid=tracking_fn)) = 0
            OR position('t.revoked_at IS NULL' IN (SELECT prosrc FROM pg_proc WHERE oid=tracking_fn)) = 0
          THEN
            RAISE EXCEPTION 'TRK-002-AUTO-LINK tracking projection security verification failed';
          END IF;
        END
        $verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """;

    /// <summary>
    /// The previous body would keep every derived link (stored with <c>expires_at='infinity'</c>) valid forever, so
    /// the lifecycle bound is never removed by a rollback. The previous release works with this body unchanged.
    /// </summary>
    public const string SchemaDowngradeNotSupportedSql =
        """
        DO $schema_downgrade$
        BEGIN
          RAISE EXCEPTION USING
            MESSAGE = 'TRK002_LIFECYCLE_DOWNGRADE_NOT_SUPPORTED',
            DETAIL = 'TRK-002-AUTO-LINK rollback blocked: without the lifecycle bound derived links would never end; the previous release works with it in place.',
            ERRCODE = 'P0001';
        END
        $schema_downgrade$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaDowngradeNotSupportedSql);
}
