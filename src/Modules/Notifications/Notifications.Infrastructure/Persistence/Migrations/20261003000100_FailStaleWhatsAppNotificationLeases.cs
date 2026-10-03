using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>
/// NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03 (project owner, "Sí, marcar fallido y avisar"): when the
/// process sending a WhatsApp message dies mid-send, its <c>notifications.send-requested</c> row stays
/// PROCESSING until the lease expires. The send may already have reached Meta, so stale recovery no longer
/// requeues it: the Notification ends FAILED with <c>AMBIGUOUS_TIMEOUT</c> (the reason of
/// NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02), the request ends DEAD and the dispatchers are told through the
/// <c>notifications.status-changed</c> row written in the same transaction (REALTIME lane,
/// <c>NotificationStatusChanged.v1</c>, operations audience, no recipient or PII). Only the body of
/// <c>security.recover_stale_notifications_outbox</c> changes: same signature, owner, search_path and grants.
/// The terminal update is guarded by the expired row's own <c>lease_token</c>; a WhatsApp row locked by a
/// concurrent settle is skipped and never requeued. IN_APP and EMAIL keep the NTF-001 recovery.
/// </summary>
[DbContext(typeof(NotificationsDbContext))]
[Migration(MigrationId)]
public sealed class FailStaleWhatsAppNotificationLeases : Migration
{
    public const string MigrationId = "20261003000100_FailStaleWhatsAppNotificationLeases";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);

    public const string UpSql =
        """
        -- NTF-001 rollback blocked: only the recover_stale_notifications_outbox body changes; owner and grants are kept.
        GRANT CREATE ON SCHEMA security TO paqueteria_outbox_executor;

        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;

        CREATE OR REPLACE FUNCTION security.recover_stale_notifications_outbox(
          p_worker_id text,p_batch_size integer,p_max_attempts integer,p_lease_duration interval)
        RETURNS SETOF platform.outbox_events LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,platform,security,pg_temp AS $fn$
        DECLARE v_request platform.outbox_events%ROWTYPE; v_notification notifications.notifications%ROWTYPE;
          v_now timestamptz;
        BEGIN
          -- NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03: a WhatsApp send whose lease expired may already have
          -- reached the provider, so it is never requeued. Same terminal outcome as an ambiguous settle
          -- (NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02): FAILED with AMBIGUOUS_TIMEOUT, request DEAD, one
          -- notifications.status-changed row for the operations audience, all in this transaction.
          FOR v_request IN
            SELECT o.* FROM platform.outbox_events o
            WHERE o.status='PROCESSING' AND o.topic='notifications.send-requested'
              AND o.lease_expires_at<=clock_timestamp()
              AND EXISTS (SELECT 1 FROM notifications.notifications n
                WHERE n.id=o.aggregate_id AND n.owner_org_id=o.owner_org_id AND n.channel='WHATSAPP')
            ORDER BY o.lease_expires_at FOR UPDATE OF o SKIP LOCKED
            LIMIT LEAST(GREATEST(p_batch_size,1),1000)
          LOOP
            v_now=clock_timestamp();
            UPDATE platform.outbox_events SET status='DEAD',last_error='AMBIGUOUS_TIMEOUT',processed_at=v_now,
              locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
              WHERE id=v_request.id AND status='PROCESSING' AND lease_token=v_request.lease_token;
            IF NOT FOUND THEN CONTINUE; END IF;
            SELECT * INTO v_notification FROM notifications.notifications
            WHERE id=v_request.aggregate_id AND owner_org_id=v_request.owner_org_id AND channel='WHATSAPP'
            FOR UPDATE;
            IF FOUND AND v_notification.status='PENDING' THEN
              UPDATE notifications.notifications SET status='FAILED',attempts=attempts+1,version=version+1,
                last_provider_attempt_code='AMBIGUOUS_TIMEOUT',last_attempt_at=v_now,updated_at=v_now
                WHERE id=v_notification.id AND version=v_notification.version;
              INSERT INTO notifications.notification_status_events(
                id,owner_org_id,notification_id,version,status,attempts,provider_attempt_code,occurred_at)
              VALUES(pg_catalog.gen_random_uuid(),v_notification.owner_org_id,v_notification.id,v_notification.version+1,
                'FAILED',v_notification.attempts+1,'AMBIGUOUS_TIMEOUT',v_now);
              PERFORM security.emit_notification_status_changed(v_notification.owner_org_id,v_notification.id,
                v_notification.version+1,v_notification.channel,'FAILED',v_notification.attempts+1,v_now);
            END IF;
          END LOOP;

          RETURN QUERY
          WITH candidates AS (
            SELECT o.id FROM platform.outbox_events o
            WHERE o.status='PROCESSING'
              AND security.resolve_outbox_consumer(o.topic)='NOTIFICATIONS'
              AND o.lease_expires_at<=clock_timestamp()
              AND NOT (o.topic='notifications.send-requested' AND EXISTS (
                SELECT 1 FROM notifications.notifications n
                WHERE n.id=o.aggregate_id AND n.owner_org_id=o.owner_org_id AND n.channel='WHATSAPP'))
            ORDER BY o.lease_expires_at FOR UPDATE OF o SKIP LOCKED
            LIMIT LEAST(GREATEST(p_batch_size,1),1000)
          ), updated AS (
            UPDATE platform.outbox_events o SET
              status=CASE
                WHEN o.attempts>=GREATEST(p_max_attempts,1) AND o.topic='notifications.send-requested' THEN 'PROCESSING'
                WHEN o.attempts>=GREATEST(p_max_attempts,1) THEN 'DEAD'
                ELSE 'RETRY' END,
              available_at=clock_timestamp(),
              processed_at=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1)
                AND o.topic<>'notifications.send-requested' THEN clock_timestamp() ELSE NULL END,
              last_error=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1) THEN 'MAX_ATTEMPTS_EXHAUSTED'
                ELSE COALESCE(o.last_error,'LEASE_EXPIRED') END,
              locked_at=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1)
                AND o.topic='notifications.send-requested' THEN clock_timestamp() ELSE NULL END,
              locked_by=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1)
                AND o.topic='notifications.send-requested' THEN p_worker_id ELSE NULL END,
              lease_token=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1)
                AND o.topic='notifications.send-requested' THEN pg_catalog.gen_random_uuid() ELSE NULL END,
              lease_expires_at=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1)
                AND o.topic='notifications.send-requested' THEN clock_timestamp()+GREATEST(p_lease_duration,interval '10 seconds') ELSE NULL END
            FROM candidates c WHERE o.id=c.id RETURNING o.*
          ) SELECT u.* FROM updated u
            WHERE u.status='PROCESSING' AND u.topic='notifications.send-requested';
        END
        $fn$;

        RESET ROLE;
        SET ROLE paqueteria_migrator;

        REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;
        """;

    /// <summary>
    /// Restores the NTF-001 body. WhatsApp Notifications already FAILED with <c>AMBIGUOUS_TIMEOUT</c> by stale
    /// recovery stay terminal (FAILED/DEAD are never claimed or recovered again).
    /// </summary>
    public const string DownSql =
        """
        -- NTF-001 rollback blocked: restore the NTF-001 recover_stale_notifications_outbox body.
        GRANT CREATE ON SCHEMA security TO paqueteria_outbox_executor;

        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;

        CREATE OR REPLACE FUNCTION security.recover_stale_notifications_outbox(
          p_worker_id text,p_batch_size integer,p_max_attempts integer,p_lease_duration interval)
        RETURNS SETOF platform.outbox_events LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,platform,security,pg_temp AS $fn$
        BEGIN
          RETURN QUERY
          WITH candidates AS (
            SELECT id FROM platform.outbox_events
            WHERE status='PROCESSING'
              AND security.resolve_outbox_consumer(topic)='NOTIFICATIONS'
              AND lease_expires_at<=clock_timestamp()
            ORDER BY lease_expires_at FOR UPDATE SKIP LOCKED
            LIMIT LEAST(GREATEST(p_batch_size,1),1000)
          ), updated AS (
            UPDATE platform.outbox_events o SET
              status=CASE
                WHEN o.attempts>=GREATEST(p_max_attempts,1) AND o.topic='notifications.send-requested' THEN 'PROCESSING'
                WHEN o.attempts>=GREATEST(p_max_attempts,1) THEN 'DEAD'
                ELSE 'RETRY' END,
              available_at=clock_timestamp(),
              processed_at=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1)
                AND o.topic<>'notifications.send-requested' THEN clock_timestamp() ELSE NULL END,
              last_error=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1) THEN 'MAX_ATTEMPTS_EXHAUSTED'
                ELSE COALESCE(o.last_error,'LEASE_EXPIRED') END,
              locked_at=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1)
                AND o.topic='notifications.send-requested' THEN clock_timestamp() ELSE NULL END,
              locked_by=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1)
                AND o.topic='notifications.send-requested' THEN p_worker_id ELSE NULL END,
              lease_token=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1)
                AND o.topic='notifications.send-requested' THEN pg_catalog.gen_random_uuid() ELSE NULL END,
              lease_expires_at=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1)
                AND o.topic='notifications.send-requested' THEN clock_timestamp()+GREATEST(p_lease_duration,interval '10 seconds') ELSE NULL END
            FROM candidates c WHERE o.id=c.id RETURNING o.*
          ) SELECT u.* FROM updated u
            WHERE u.status='PROCESSING' AND u.topic='notifications.send-requested';
        END
        $fn$;

        RESET ROLE;
        SET ROLE paqueteria_migrator;

        REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;
        """;
}
