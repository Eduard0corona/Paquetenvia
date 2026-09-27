using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>
/// D8-OUTBOX-LANE-DISPATCH (project owner, 2026-09-27): the DISPATCH outbox lane that carries
/// <c>dispatch.order-status-reaction-requested</c> to the Dispatch assignment-lifecycle consumer (D8).
/// It lives in this lane because the lane owns <c>security.resolve_outbox_consumer</c> (NTF-001
/// precedent, followed by EXT-001 and RTE-001). The claim/requeue functions copy the REALTIME lane
/// functions exactly, with the consumer literal changed. <c>orders.status-changed</c> keeps its
/// REALTIME route.
/// </summary>
[DbContext(typeof(NotificationsDbContext))]
[Migration(MigrationId)]
public sealed class AddDispatchOutboxLane : Migration
{
    public const string MigrationId = "20260927000200_AddDispatchOutboxLane";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);

    public const string UpSql =
        """
        -- NTF-001 rollback blocked: preserve ownership and privilege boundaries while adding the D8 DISPATCH lane.
        GRANT CREATE ON SCHEMA security TO paqueteria_outbox_executor;

        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;

        CREATE OR REPLACE FUNCTION security.resolve_outbox_consumer(p_topic text) RETURNS text
        LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
        SET search_path=pg_catalog,security,pg_temp AS $fn$
          SELECT CASE
            WHEN p_topic IN (
              'orders.status-changed','orders.timeline-event-added',
              'dispatch.assignment-changed','dispatch.external-offer-changed',
              'notifications.status-changed','routes.route-changed') THEN 'REALTIME'
            WHEN p_topic IN ('orders.created','notifications.send-requested') THEN 'NOTIFICATIONS'
            WHEN p_topic='dispatch.order-status-reaction-requested' THEN 'DISPATCH'
            ELSE 'UNROUTED'
          END;
        $fn$;

        CREATE FUNCTION security.claim_dispatch_outbox(
          p_worker_id text,p_batch_size integer DEFAULT 50,
          p_lease_duration interval DEFAULT interval '2 minutes')
        RETURNS SETOF platform.outbox_events LANGUAGE sql SECURITY DEFINER
        SET search_path=pg_catalog,platform,security,pg_temp AS $fn$
          WITH candidates AS (
            SELECT id FROM platform.outbox_events
            WHERE status IN ('PENDING','RETRY') AND available_at <= clock_timestamp()
              AND security.resolve_outbox_consumer(topic)='DISPATCH'
            ORDER BY priority DESC,available_at,created_at FOR UPDATE SKIP LOCKED
            LIMIT LEAST(GREATEST(p_batch_size,1),200)
          )
          UPDATE platform.outbox_events o SET
            status='PROCESSING',attempts=o.attempts+1,locked_at=clock_timestamp(),
            locked_by=p_worker_id,lease_token=pg_catalog.gen_random_uuid(),
            lease_expires_at=clock_timestamp()+GREATEST(p_lease_duration,interval '10 seconds'),last_error=NULL
          FROM candidates c WHERE o.id=c.id RETURNING o.*;
        $fn$;

        CREATE FUNCTION security.requeue_stale_dispatch_outbox(
          p_older_than interval DEFAULT interval '0 seconds',
          p_batch_size integer DEFAULT 100,p_max_attempts integer DEFAULT 10)
        RETURNS integer LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,platform,security,pg_temp AS $fn$
        DECLARE v_count integer;
        BEGIN
          WITH candidates AS (
            SELECT id FROM platform.outbox_events
            WHERE status='PROCESSING'
              AND security.resolve_outbox_consumer(topic)='DISPATCH'
              AND lease_expires_at <= clock_timestamp()-GREATEST(p_older_than,interval '0 seconds')
            ORDER BY lease_expires_at FOR UPDATE SKIP LOCKED
            LIMIT LEAST(GREATEST(p_batch_size,1),1000)
          ), updated AS (
            UPDATE platform.outbox_events o SET
              status=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1) THEN 'DEAD' ELSE 'RETRY' END,
              available_at=clock_timestamp(),
              processed_at=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1) THEN clock_timestamp() ELSE NULL END,
              last_error=COALESCE(o.last_error,'LEASE_EXPIRED'),
              locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
            FROM candidates c WHERE o.id=c.id RETURNING 1
          ) SELECT count(*) INTO v_count FROM updated;
          RETURN v_count;
        END
        $fn$;

        REVOKE ALL ON FUNCTION security.claim_dispatch_outbox(text,integer,interval) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.requeue_stale_dispatch_outbox(interval,integer,integer) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION security.claim_dispatch_outbox(text,integer,interval),
          security.requeue_stale_dispatch_outbox(interval,integer,integer)
          TO paqueteria_worker;

        RESET ROLE;
        SET ROLE paqueteria_migrator;

        REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;
        """;

    public const string DownSql =
        """
        -- NTF-001 rollback blocked: only the D8 DISPATCH lane is withdrawn, and only when it is drained.
        GRANT CREATE ON SCHEMA security TO paqueteria_outbox_executor;

        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;

        DO $rollback$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM platform.outbox_events
            WHERE topic='dispatch.order-status-reaction-requested'
              AND status IN ('PENDING','RETRY','PROCESSING')
          ) THEN
            RAISE EXCEPTION 'D8 DISPATCH lane rollback blocked by active Dispatch-owned rows';
          END IF;
        END
        $rollback$;

        DROP FUNCTION security.claim_dispatch_outbox(text,integer,interval);
        DROP FUNCTION security.requeue_stale_dispatch_outbox(interval,integer,integer);

        CREATE OR REPLACE FUNCTION security.resolve_outbox_consumer(p_topic text) RETURNS text
        LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
        SET search_path=pg_catalog,security,pg_temp AS $fn$
          SELECT CASE
            WHEN p_topic IN (
              'orders.status-changed','orders.timeline-event-added',
              'dispatch.assignment-changed','dispatch.external-offer-changed',
              'notifications.status-changed','routes.route-changed') THEN 'REALTIME'
            WHEN p_topic IN ('orders.created','notifications.send-requested') THEN 'NOTIFICATIONS'
            ELSE 'UNROUTED'
          END;
        $fn$;

        RESET ROLE;
        SET ROLE paqueteria_migrator;

        REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;
        """;
}
