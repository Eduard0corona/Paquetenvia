using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>
/// NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02 (project owner, GATE-004, "Marcar fallido y avisar"): a
/// WhatsApp send whose outcome is unknown (timeout, or Meta may have accepted it) ends FAILED with the
/// distinct reason <c>AMBIGUOUS_TIMEOUT</c>; its send request ends DEAD and is never retried or
/// requeued. The dispatchers are told through the <c>notifications.status-changed</c> row the same
/// settle already writes (REALTIME lane, <c>NotificationStatusChanged.v1</c>, operations audience, no
/// recipient or PII). Only the body of <c>security.apply_notification_outcome</c> changes: same
/// signature, owner, <c>lease_token</c> check and grants. IN_APP and EMAIL keep the AMBIGUOUS retry.
/// </summary>
[DbContext(typeof(NotificationsDbContext))]
[Migration(MigrationId)]
public sealed class FailAmbiguousWhatsAppNotifications : Migration
{
    public const string MigrationId = "20261002000100_FailAmbiguousWhatsAppNotifications";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);

    public const string UpSql =
        """
        -- NTF-001 rollback blocked: only the apply_notification_outcome body changes; owner and grants are kept.
        GRANT CREATE ON SCHEMA security TO paqueteria_outbox_executor;

        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;

        CREATE OR REPLACE FUNCTION security.apply_notification_outcome(
          p_outbox_id uuid,p_lease_token uuid,p_notification_id uuid,p_expected_version integer,
          p_outcome text,p_code text,p_occurred_at timestamptz,p_available_at timestamptz)
        RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,platform,notifications,security,pg_temp AS $fn$
        DECLARE v_request platform.outbox_events%ROWTYPE; v_notification notifications.notifications%ROWTYPE;
          v_status text; v_outbox_status text; v_outcome text; v_code text;
        BEGIN
          IF p_outcome NOT IN ('SUCCESS','TRANSIENT','AMBIGUOUS','PERMANENT','AMBIGUOUS_FAILED')
             OR p_code NOT IN ('SYNTHETIC_ACCEPTED','SYNTHETIC_TRANSIENT','SYNTHETIC_PERMANENT','SYNTHETIC_AMBIGUOUS_TIMEOUT','AMBIGUOUS_TIMEOUT')
             OR (p_outcome='AMBIGUOUS_FAILED')<>(p_code='AMBIGUOUS_TIMEOUT') THEN
            RAISE EXCEPTION 'invalid provider outcome' USING ERRCODE='22023';
          END IF;
          SELECT * INTO v_request FROM platform.outbox_events
          WHERE id=p_outbox_id AND status='PROCESSING' AND lease_token=p_lease_token
            AND lease_expires_at>clock_timestamp() AND topic='notifications.send-requested' FOR UPDATE;
          IF NOT FOUND THEN RETURN false; END IF;
          SELECT * INTO v_notification FROM notifications.notifications
          WHERE id=p_notification_id AND owner_org_id=v_request.owner_org_id
            AND version=p_expected_version AND status='PENDING' FOR UPDATE;
          IF NOT FOUND OR v_request.aggregate_id<>p_notification_id THEN RETURN false; END IF;
          -- NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02: a WhatsApp send with an unknown outcome is never
          -- retried (the customer must not receive it twice). AMBIGUOUS reported for a WhatsApp
          -- Notification is settled as AMBIGUOUS_FAILED, so this function fails safe; the terminal
          -- ambiguous outcome is refused for every other channel.
          v_outcome=CASE WHEN p_outcome='AMBIGUOUS' AND v_notification.channel='WHATSAPP'
            THEN 'AMBIGUOUS_FAILED' ELSE p_outcome END;
          IF v_outcome='AMBIGUOUS_FAILED' AND v_notification.channel<>'WHATSAPP' THEN
            RAISE EXCEPTION 'invalid provider outcome' USING ERRCODE='22023';
          END IF;
          v_code=CASE WHEN v_outcome='AMBIGUOUS_FAILED' THEN 'AMBIGUOUS_TIMEOUT' ELSE p_code END;
          v_status=CASE WHEN v_outcome='SUCCESS' THEN 'SENT'
            WHEN v_outcome IN ('PERMANENT','AMBIGUOUS_FAILED') THEN 'FAILED' ELSE 'PENDING' END;
          v_outbox_status=CASE WHEN v_outcome='SUCCESS' THEN 'PROCESSED'
            WHEN v_outcome IN ('PERMANENT','AMBIGUOUS_FAILED') THEN 'DEAD' ELSE 'RETRY' END;
          UPDATE notifications.notifications SET status=v_status,attempts=attempts+1,version=version+1,
            last_provider_attempt_code=v_code,last_attempt_at=p_occurred_at,updated_at=p_occurred_at,
            sent_at=CASE WHEN v_outcome='SUCCESS' THEN p_occurred_at ELSE sent_at END
            WHERE id=p_notification_id AND version=p_expected_version;
          INSERT INTO notifications.notification_status_events(
            id,owner_org_id,notification_id,version,status,attempts,provider_attempt_code,occurred_at)
          VALUES(pg_catalog.gen_random_uuid(),v_notification.owner_org_id,p_notification_id,p_expected_version+1,
            v_status,v_notification.attempts+1,v_code,p_occurred_at);
          PERFORM security.emit_notification_status_changed(v_notification.owner_org_id,p_notification_id,
            p_expected_version+1,v_notification.channel,v_status,v_notification.attempts+1,p_occurred_at);
          UPDATE platform.outbox_events SET status=v_outbox_status,last_error=CASE WHEN v_outbox_status='PROCESSED' THEN NULL ELSE v_code END,
            processed_at=CASE WHEN v_outbox_status IN ('PROCESSED','DEAD') THEN clock_timestamp() ELSE NULL END,
            available_at=CASE WHEN v_outbox_status='RETRY' THEN p_available_at ELSE available_at END,
            locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
            WHERE id=p_outbox_id AND status='PROCESSING' AND lease_token=p_lease_token;
          RETURN FOUND;
        END
        $fn$;

        RESET ROLE;
        SET ROLE paqueteria_migrator;

        REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;
        """;

    /// <summary>
    /// Restores the NTF-001 body. Notifications already FAILED with <c>AMBIGUOUS_TIMEOUT</c> stay
    /// terminal (FAILED/DEAD are never claimed again).
    /// </summary>
    public const string DownSql =
        """
        -- NTF-001 rollback blocked: restore the NTF-001 apply_notification_outcome body.
        GRANT CREATE ON SCHEMA security TO paqueteria_outbox_executor;

        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;

        CREATE OR REPLACE FUNCTION security.apply_notification_outcome(
          p_outbox_id uuid,p_lease_token uuid,p_notification_id uuid,p_expected_version integer,
          p_outcome text,p_code text,p_occurred_at timestamptz,p_available_at timestamptz)
        RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,platform,notifications,security,pg_temp AS $fn$
        DECLARE v_request platform.outbox_events%ROWTYPE; v_notification notifications.notifications%ROWTYPE;
          v_status text; v_outbox_status text;
        BEGIN
          IF p_outcome NOT IN ('SUCCESS','TRANSIENT','AMBIGUOUS','PERMANENT')
             OR p_code NOT IN ('SYNTHETIC_ACCEPTED','SYNTHETIC_TRANSIENT','SYNTHETIC_PERMANENT','SYNTHETIC_AMBIGUOUS_TIMEOUT') THEN
            RAISE EXCEPTION 'invalid provider outcome' USING ERRCODE='22023';
          END IF;
          SELECT * INTO v_request FROM platform.outbox_events
          WHERE id=p_outbox_id AND status='PROCESSING' AND lease_token=p_lease_token
            AND lease_expires_at>clock_timestamp() AND topic='notifications.send-requested' FOR UPDATE;
          IF NOT FOUND THEN RETURN false; END IF;
          SELECT * INTO v_notification FROM notifications.notifications
          WHERE id=p_notification_id AND owner_org_id=v_request.owner_org_id
            AND version=p_expected_version AND status='PENDING' FOR UPDATE;
          IF NOT FOUND OR v_request.aggregate_id<>p_notification_id THEN RETURN false; END IF;
          v_status=CASE WHEN p_outcome='SUCCESS' THEN 'SENT' WHEN p_outcome='PERMANENT' THEN 'FAILED' ELSE 'PENDING' END;
          v_outbox_status=CASE WHEN p_outcome='SUCCESS' THEN 'PROCESSED' WHEN p_outcome='PERMANENT' THEN 'DEAD' ELSE 'RETRY' END;
          UPDATE notifications.notifications SET status=v_status,attempts=attempts+1,version=version+1,
            last_provider_attempt_code=p_code,last_attempt_at=p_occurred_at,updated_at=p_occurred_at,
            sent_at=CASE WHEN p_outcome='SUCCESS' THEN p_occurred_at ELSE sent_at END
            WHERE id=p_notification_id AND version=p_expected_version;
          INSERT INTO notifications.notification_status_events(
            id,owner_org_id,notification_id,version,status,attempts,provider_attempt_code,occurred_at)
          VALUES(pg_catalog.gen_random_uuid(),v_notification.owner_org_id,p_notification_id,p_expected_version+1,
            v_status,v_notification.attempts+1,p_code,p_occurred_at);
          PERFORM security.emit_notification_status_changed(v_notification.owner_org_id,p_notification_id,
            p_expected_version+1,v_notification.channel,v_status,v_notification.attempts+1,p_occurred_at);
          UPDATE platform.outbox_events SET status=v_outbox_status,last_error=CASE WHEN v_outbox_status='PROCESSED' THEN NULL ELSE p_code END,
            processed_at=CASE WHEN v_outbox_status IN ('PROCESSED','DEAD') THEN clock_timestamp() ELSE NULL END,
            available_at=CASE WHEN v_outbox_status='RETRY' THEN p_available_at ELSE available_at END,
            locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
            WHERE id=p_outbox_id AND status='PROCESSING' AND lease_token=p_lease_token;
          RETURN FOUND;
        END
        $fn$;

        RESET ROLE;
        SET ROLE paqueteria_migrator;

        REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;
        """;
}
