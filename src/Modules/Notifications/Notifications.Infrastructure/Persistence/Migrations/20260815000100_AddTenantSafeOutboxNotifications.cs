using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NotificationsDbContext))]
[Migration(MigrationId)]
public sealed class AddTenantSafeOutboxNotifications : Migration
{
    public const string MigrationId = "20260815000100_AddTenantSafeOutboxNotifications";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaDowngradeNotSupportedSql);

    public const string SchemaDowngradeNotSupportedSql =
        """
        DO $schema_downgrade$
        BEGIN
          RAISE EXCEPTION USING
            MESSAGE = 'NTF001_SCHEMA_DOWNGRADE_NOT_SUPPORTED',
            DETAIL = 'Use the NTF-001 operational routing rollback and deploy the previous worker version; schema and migration history must remain applied.',
            ERRCODE = 'P0001';
        END
        $schema_downgrade$;
        """;

    public const string UpSql =
        """
        GRANT USAGE ON SCHEMA notifications TO paqueteria_outbox_executor;
        GRANT SELECT ON notifications.notifications TO paqueteria_outbox_executor;
        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;
        DO $cutover$
        BEGIN
          IF to_regclass('notifications.notifications') IS NULL
             OR to_regclass('platform.outbox_events') IS NULL THEN
            RAISE EXCEPTION 'NTF-001 requires the canonical AI-06 notifications and outbox tables';
          END IF;
          IF EXISTS (
            SELECT 1 FROM platform.outbox_events
            WHERE topic='orders.created' AND status IN ('PENDING','RETRY','PROCESSING')
          ) THEN
            RAISE EXCEPTION 'NTF-001 cutover blocked by active historical orders.created rows';
          END IF;
          IF EXISTS (SELECT 1 FROM notifications.notifications) THEN
            RAISE EXCEPTION 'NTF-001 cannot evolve non-empty legacy notifications without an explicit data migration';
          END IF;
        END
        $cutover$;
        RESET ROLE;
        SET ROLE paqueteria_migrator;

        ALTER TABLE notifications.notifications
          ADD COLUMN recipient_user_id uuid,
          ADD COLUMN version integer NOT NULL DEFAULT 1,
          ADD COLUMN template_version integer,
          ADD COLUMN variables_snapshot jsonb,
          ADD COLUMN source_event_id uuid,
          ADD COLUMN last_provider_attempt_code text,
          ADD COLUMN last_attempt_at timestamptz,
          ADD COLUMN updated_at timestamptz NOT NULL DEFAULT clock_timestamp();
        ALTER TABLE notifications.notifications
          ALTER COLUMN recipient_user_id SET NOT NULL,
          ALTER COLUMN template_version SET NOT NULL,
          ALTER COLUMN variables_snapshot SET NOT NULL,
          ALTER COLUMN source_event_id SET NOT NULL,
          ADD CONSTRAINT notifications_version_positive CHECK (version > 0),
          ADD CONSTRAINT notifications_template_version_positive CHECK (template_version > 0),
          ADD CONSTRAINT notifications_attempts_nonnegative CHECK (attempts >= 0),
          ADD CONSTRAINT notifications_in_app_recipient_required CHECK (channel <> 'IN_APP' OR recipient_user_id IS NOT NULL),
          ADD CONSTRAINT notifications_source_recipient_uq UNIQUE
            (owner_org_id,source_event_id,recipient_user_id,channel,template_key,template_version);
        CREATE INDEX notifications_pending_idx
          ON notifications.notifications(owner_org_id,status,updated_at)
          WHERE status='PENDING';

        CREATE TABLE notifications.notification_templates (
          owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id) ON DELETE CASCADE,
          template_key text NOT NULL,
          version integer NOT NULL CHECK (version > 0),
          channel text NOT NULL CHECK (channel='IN_APP'),
          body text NOT NULL,
          allowed_variables jsonb NOT NULL,
          created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
          PRIMARY KEY(owner_org_id,template_key,version,channel),
          CHECK (allowed_variables = '["occurred_at","order_public_id","order_status"]'::jsonb)
        );
        CREATE TABLE notifications.notification_status_events (
          id uuid PRIMARY KEY,
          owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
          notification_id uuid NOT NULL REFERENCES notifications.notifications(id),
          version integer NOT NULL CHECK (version > 0),
          status text NOT NULL CHECK (status IN ('PENDING','SENT','FAILED')),
          attempts integer NOT NULL CHECK (attempts >= 0),
          provider_attempt_code text,
          occurred_at timestamptz NOT NULL,
          UNIQUE(notification_id,version)
        );
        CREATE INDEX notification_status_events_tenant_notification_idx
          ON notifications.notification_status_events(owner_org_id,notification_id,version);

        INSERT INTO notifications.notification_templates(
          owner_org_id,template_key,version,channel,body,allowed_variables)
        SELECT id,'orders.created.operations',1,'IN_APP',
          'Order {{order_public_id}} is {{order_status}} as of {{occurred_at}}.',
          '["occurred_at","order_public_id","order_status"]'::jsonb
        FROM organizations.organizations
        ON CONFLICT DO NOTHING;

        ALTER TABLE notifications.notification_templates ENABLE ROW LEVEL SECURITY;
        ALTER TABLE notifications.notification_templates FORCE ROW LEVEL SECURITY;
        ALTER TABLE notifications.notification_status_events ENABLE ROW LEVEL SECURITY;
        ALTER TABLE notifications.notification_status_events FORCE ROW LEVEL SECURITY;
        CREATE POLICY notification_templates_tenant ON notifications.notification_templates
          USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
        CREATE POLICY notification_status_events_tenant ON notifications.notification_status_events
          USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
        CREATE TRIGGER notification_status_events_append_only
          BEFORE UPDATE OR DELETE ON notifications.notification_status_events
          FOR EACH ROW EXECUTE FUNCTION platform.reject_runtime_mutation();

        CREATE FUNCTION notifications.reject_template_mutation() RETURNS trigger
        LANGUAGE plpgsql SET search_path=pg_catalog,notifications,pg_temp AS $fn$
        BEGIN
          IF TG_OP='DELETE' AND pg_trigger_depth()>1 THEN
            RETURN OLD;
          END IF;
          RAISE EXCEPTION 'notification template versions are immutable' USING ERRCODE='42501';
        END
        $fn$;
        CREATE TRIGGER notification_templates_immutable
          BEFORE UPDATE OR DELETE ON notifications.notification_templates
          FOR EACH ROW EXECUTE FUNCTION notifications.reject_template_mutation();

        CREATE FUNCTION notifications.provision_default_templates() RETURNS trigger
        LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,notifications,pg_temp AS $fn$
        BEGIN
          INSERT INTO notifications.notification_templates(
            owner_org_id,template_key,version,channel,body,allowed_variables)
          VALUES(
            NEW.id,'orders.created.operations',1,'IN_APP',
            'Order {{order_public_id}} is {{order_status}} as of {{occurred_at}}.',
            '["occurred_at","order_public_id","order_status"]'::jsonb);
          RETURN NEW;
        END
        $fn$;
        CREATE TRIGGER organizations_provision_notification_templates
          AFTER INSERT ON organizations.organizations
          FOR EACH ROW EXECUTE FUNCTION notifications.provision_default_templates();

        CREATE FUNCTION security.resolve_outbox_consumer(p_topic text) RETURNS text
        LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
        SET search_path=pg_catalog,security,pg_temp AS $fn$
          SELECT CASE
            WHEN p_topic IN (
              'orders.status-changed','orders.timeline-event-added',
              'dispatch.assignment-changed','notifications.status-changed') THEN 'REALTIME'
            WHEN p_topic IN ('orders.created','notifications.send-requested') THEN 'NOTIFICATIONS'
            ELSE 'UNROUTED'
          END;
        $fn$;

        CREATE FUNCTION security.claim_realtime_outbox(
          p_worker_id text,p_batch_size integer DEFAULT 50,
          p_lease_duration interval DEFAULT interval '2 minutes')
        RETURNS SETOF platform.outbox_events LANGUAGE sql SECURITY DEFINER
        SET search_path=pg_catalog,platform,security,pg_temp AS $fn$
          WITH candidates AS (
            SELECT id FROM platform.outbox_events
            WHERE status IN ('PENDING','RETRY') AND available_at <= clock_timestamp()
              AND security.resolve_outbox_consumer(topic)='REALTIME'
            ORDER BY priority DESC,available_at,created_at FOR UPDATE SKIP LOCKED
            LIMIT LEAST(GREATEST(p_batch_size,1),200)
          )
          UPDATE platform.outbox_events o SET
            status='PROCESSING',attempts=o.attempts+1,locked_at=clock_timestamp(),
            locked_by=p_worker_id,lease_token=pg_catalog.gen_random_uuid(),
            lease_expires_at=clock_timestamp()+GREATEST(p_lease_duration,interval '10 seconds'),last_error=NULL
          FROM candidates c WHERE o.id=c.id RETURNING o.*;
        $fn$;

        CREATE FUNCTION security.claim_notifications_outbox(
          p_worker_id text,p_batch_size integer DEFAULT 50,
          p_lease_duration interval DEFAULT interval '2 minutes')
        RETURNS SETOF platform.outbox_events LANGUAGE sql SECURITY DEFINER
        SET search_path=pg_catalog,platform,security,pg_temp AS $fn$
          WITH candidates AS (
            SELECT id FROM platform.outbox_events
            WHERE status IN ('PENDING','RETRY') AND available_at <= clock_timestamp()
              AND security.resolve_outbox_consumer(topic)='NOTIFICATIONS'
            ORDER BY priority DESC,available_at,created_at FOR UPDATE SKIP LOCKED
            LIMIT LEAST(GREATEST(p_batch_size,1),200)
          )
          UPDATE platform.outbox_events o SET
            status='PROCESSING',attempts=o.attempts+1,locked_at=clock_timestamp(),
            locked_by=p_worker_id,lease_token=pg_catalog.gen_random_uuid(),
            lease_expires_at=clock_timestamp()+GREATEST(p_lease_duration,interval '10 seconds'),last_error=NULL
          FROM candidates c WHERE o.id=c.id RETURNING o.*;
        $fn$;

        CREATE FUNCTION security.claim_unowned_outbox(
          p_worker_id text,p_batch_size integer DEFAULT 50,
          p_lease_duration interval DEFAULT interval '2 minutes')
        RETURNS SETOF platform.outbox_events LANGUAGE sql SECURITY DEFINER
        SET search_path=pg_catalog,platform,security,pg_temp AS $fn$
          WITH candidates AS (
            SELECT id FROM platform.outbox_events
            WHERE status IN ('PENDING','RETRY') AND available_at <= clock_timestamp()
              AND security.resolve_outbox_consumer(topic)='UNROUTED'
            ORDER BY priority DESC,available_at,created_at FOR UPDATE SKIP LOCKED
            LIMIT LEAST(GREATEST(p_batch_size,1),200)
          )
          UPDATE platform.outbox_events o SET
            status='PROCESSING',attempts=o.attempts+1,locked_at=clock_timestamp(),
            locked_by=p_worker_id,lease_token=pg_catalog.gen_random_uuid(),
            lease_expires_at=clock_timestamp()+GREATEST(p_lease_duration,interval '10 seconds'),last_error=NULL
          FROM candidates c WHERE o.id=c.id RETURNING o.*;
        $fn$;

        CREATE FUNCTION security.requeue_stale_realtime_outbox(
          p_older_than interval DEFAULT interval '0 seconds',
          p_batch_size integer DEFAULT 100,p_max_attempts integer DEFAULT 10)
        RETURNS integer LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,platform,security,pg_temp AS $fn$
        DECLARE v_count integer;
        BEGIN
          WITH candidates AS (
            SELECT id FROM platform.outbox_events
            WHERE status='PROCESSING'
              AND security.resolve_outbox_consumer(topic)='REALTIME'
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

        CREATE FUNCTION security.recover_stale_notifications_outbox(
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

        CREATE FUNCTION security.requeue_stale_unowned_outbox(
          p_older_than interval DEFAULT interval '0 seconds',
          p_batch_size integer DEFAULT 100,p_max_attempts integer DEFAULT 10)
        RETURNS integer LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,platform,security,pg_temp AS $fn$
        DECLARE v_count integer;
        BEGIN
          WITH candidates AS (
            SELECT id FROM platform.outbox_events
            WHERE status='PROCESSING'
              AND security.resolve_outbox_consumer(topic)='UNROUTED'
              AND lease_expires_at<=clock_timestamp()-GREATEST(p_older_than,interval '0 seconds')
            ORDER BY lease_expires_at FOR UPDATE SKIP LOCKED
            LIMIT LEAST(GREATEST(p_batch_size,1),1000)
          ), updated AS (
            UPDATE platform.outbox_events o SET
              status=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1) THEN 'DEAD' ELSE 'RETRY' END,
              available_at=clock_timestamp(),
              processed_at=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1) THEN clock_timestamp() ELSE NULL END,
              last_error=CASE WHEN o.attempts>=GREATEST(p_max_attempts,1) THEN 'UNKNOWN_TOPIC'
                ELSE COALESCE(o.last_error,'LEASE_EXPIRED') END,
              locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
            FROM candidates c WHERE o.id=c.id RETURNING 1
          ) SELECT count(*) INTO v_count FROM updated;
          RETURN v_count;
        END
        $fn$;

        CREATE FUNCTION security.read_owner_dispatcher_ids(p_owner_org_id uuid,p_limit integer)
        RETURNS TABLE(user_id uuid) LANGUAGE sql STABLE SECURITY DEFINER
        SET search_path=pg_catalog,organizations,security,pg_temp AS $fn$
          SELECT DISTINCT m.user_id
          FROM organizations.organization_memberships m
          WHERE m.organization_id=p_owner_org_id AND m.status='ACTIVE' AND m.role='DISPATCHER'
          ORDER BY m.user_id LIMIT LEAST(GREATEST(p_limit,1),501);
        $fn$;
        CREATE FUNCTION security.read_active_notification_users(p_requested_user_ids uuid[])
        RETURNS TABLE(user_id uuid) LANGUAGE sql STABLE SECURITY DEFINER
        SET search_path=pg_catalog,identity,security,pg_temp AS $fn$
          SELECT u.id FROM identity.users u
          WHERE u.status='ACTIVE' AND u.id=ANY(p_requested_user_ids)
          ORDER BY u.id LIMIT 500;
        $fn$;

        CREATE FUNCTION security.emit_notification_status_changed(
          p_owner_org_id uuid,p_notification_id uuid,p_version integer,p_channel text,
          p_status text,p_attempts integer,p_occurred_at timestamptz)
        RETURNS void LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,platform,security,pg_temp AS $fn$
        BEGIN
          INSERT INTO platform.outbox_events(
            id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
            payload,priority,status,attempts,available_at,created_at)
          VALUES(
            pg_catalog.gen_random_uuid(),p_owner_org_id,
            jsonb_build_object('organization_ids',jsonb_build_array(p_owner_org_id::text)),
            'notifications.status-changed','Notification',p_notification_id,p_version,
            jsonb_build_object(
              'schema_version','notification-status-changed-v1','notification_id',p_notification_id,
              'channel',p_channel,'status',p_status,'attempts',p_attempts,'occurred_at',p_occurred_at),
            50,'PENDING',0,p_occurred_at,p_occurred_at);
        END
        $fn$;

        CREATE FUNCTION security.expand_order_created_notifications(
          p_source_id uuid,p_lease_token uuid,p_order_id uuid,p_public_id text,p_status text,
          p_occurred_at timestamptz,p_recipient_ids uuid[])
        RETURNS text LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,platform,notifications,security,pg_temp AS $fn$
        DECLARE v_source platform.outbox_events%ROWTYPE; v_template notifications.notification_templates%ROWTYPE;
          v_recipient uuid; v_notification_id uuid; v_variables jsonb;
        BEGIN
          SELECT * INTO v_source FROM platform.outbox_events
          WHERE id=p_source_id AND status='PROCESSING' AND lease_token=p_lease_token
            AND lease_expires_at>clock_timestamp() FOR UPDATE;
          IF NOT FOUND THEN RETURN 'LEASE_LOST'; END IF;
          IF v_source.topic<>'orders.created' OR v_source.aggregate_type<>'Order'
             OR v_source.aggregate_id<>p_order_id OR v_source.owner_org_id IS NULL
             OR security.resolve_outbox_consumer(v_source.topic)<>'NOTIFICATIONS' THEN
            RAISE EXCEPTION 'INVALID_PAYLOAD' USING ERRCODE='22023';
          END IF;
          SELECT * INTO v_template FROM notifications.notification_templates t
          WHERE t.owner_org_id=v_source.owner_org_id AND t.template_key='orders.created.operations'
            AND t.version=1 AND t.channel='IN_APP';
          IF NOT FOUND THEN
            IF EXISTS (SELECT 1 FROM notifications.notification_templates t
              WHERE t.owner_org_id=v_source.owner_org_id AND t.template_key='orders.created.operations'
                AND t.channel='IN_APP') THEN
              UPDATE platform.outbox_events SET status='DEAD',last_error='TEMPLATE_VERSION_UNKNOWN',
                processed_at=clock_timestamp(),locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
                WHERE id=p_source_id;
              RETURN 'TEMPLATE_VERSION_UNKNOWN';
            END IF;
            UPDATE platform.outbox_events SET status='DEAD',last_error='TEMPLATE_NOT_FOUND',
              processed_at=clock_timestamp(),locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
              WHERE id=p_source_id;
            RETURN 'TEMPLATE_NOT_FOUND';
          END IF;
          IF v_template.allowed_variables<>'["occurred_at","order_public_id","order_status"]'::jsonb THEN
            UPDATE platform.outbox_events SET status='DEAD',last_error='TEMPLATE_VARIABLE_UNKNOWN',
              processed_at=clock_timestamp(),locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
              WHERE id=p_source_id;
            RETURN 'TEMPLATE_VARIABLE_UNKNOWN';
          END IF;
          IF cardinality(p_recipient_ids)>500 THEN RAISE EXCEPTION 'AUDIENCE_LIMIT_EXCEEDED' USING ERRCODE='22023'; END IF;
          IF EXISTS (SELECT 1 FROM unnest(p_recipient_ids) value WHERE value IS NULL)
             OR cardinality(p_recipient_ids)<>(SELECT count(DISTINCT value) FROM unnest(p_recipient_ids) value) THEN
            RAISE EXCEPTION 'AUDIENCE_CONTRACT_VIOLATION' USING ERRCODE='22023';
          END IF;
          IF cardinality(p_recipient_ids)=0 THEN
            UPDATE platform.outbox_events SET status='PROCESSED',last_error='NO_ELIGIBLE_RECIPIENT',
              processed_at=clock_timestamp(),locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
              WHERE id=p_source_id;
            RETURN 'NO_ELIGIBLE_RECIPIENT';
          END IF;
          v_variables=jsonb_build_object(
            'order_public_id',p_public_id,'order_status',p_status,
            'occurred_at',to_char(p_occurred_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'));
          FOR v_recipient IN SELECT DISTINCT value FROM unnest(p_recipient_ids) value ORDER BY value LOOP
            v_notification_id=pg_catalog.gen_random_uuid();
            INSERT INTO notifications.notifications(
              id,owner_org_id,order_id,recipient_user_id,channel,status,attempts,version,
              template_key,template_version,variables_snapshot,source_event_id,created_at,updated_at)
            VALUES(v_notification_id,v_source.owner_org_id,NULL,v_recipient,'IN_APP','PENDING',0,1,
              v_template.template_key,v_template.version,v_variables,p_source_id,p_occurred_at,p_occurred_at)
            ON CONFLICT ON CONSTRAINT notifications_source_recipient_uq DO NOTHING
            RETURNING id INTO v_notification_id;
            IF v_notification_id IS NOT NULL THEN
              INSERT INTO notifications.notification_status_events(
                id,owner_org_id,notification_id,version,status,attempts,occurred_at)
              VALUES(pg_catalog.gen_random_uuid(),v_source.owner_org_id,v_notification_id,1,'PENDING',0,p_occurred_at);
              PERFORM security.emit_notification_status_changed(
                v_source.owner_org_id,v_notification_id,1,'IN_APP','PENDING',0,p_occurred_at);
              INSERT INTO platform.outbox_events(
                id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
                payload,priority,status,attempts,available_at,created_at)
              VALUES(pg_catalog.gen_random_uuid(),v_source.owner_org_id,v_source.tenant_context,
                'notifications.send-requested','Notification',v_notification_id,1,
                jsonb_build_object('schema_version','notification-send-requested-v1','notification_id',v_notification_id),
                50,'PENDING',0,p_occurred_at,p_occurred_at);
            END IF;
          END LOOP;
          UPDATE platform.outbox_events SET status='PROCESSED',last_error='SOURCE_EXPANDED',
            processed_at=clock_timestamp(),locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
            WHERE id=p_source_id AND status='PROCESSING' AND lease_token=p_lease_token;
          IF NOT FOUND THEN RAISE EXCEPTION 'LEASE_LOST' USING ERRCODE='40001'; END IF;
          RETURN 'SOURCE_EXPANDED';
        END
        $fn$;

        CREATE FUNCTION security.read_notification_delivery(p_notification_id uuid,p_owner_org_id uuid)
        RETURNS TABLE(notification_id uuid,owner_org_id uuid,channel text,status text,attempts integer,
          version integer,template_key text,template_version integer,variables_snapshot text,template_body text)
        LANGUAGE sql STABLE SECURITY DEFINER
        SET search_path=pg_catalog,notifications,security,pg_temp AS $fn$
          SELECT n.id,n.owner_org_id,n.channel,n.status,n.attempts,n.version,n.template_key,
            n.template_version,n.variables_snapshot::text,t.body
          FROM notifications.notifications n
          JOIN notifications.notification_templates t
            ON t.owner_org_id=n.owner_org_id AND t.template_key=n.template_key
            AND t.version=n.template_version AND t.channel=n.channel
          WHERE n.id=p_notification_id AND n.owner_org_id=p_owner_org_id;
        $fn$;

        CREATE FUNCTION security.apply_notification_outcome(
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

        CREATE FUNCTION security.finalize_notification_max_attempts(
          p_outbox_id uuid,p_lease_token uuid,p_notification_id uuid,p_expected_version integer,p_occurred_at timestamptz)
        RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,platform,notifications,security,pg_temp AS $fn$
        DECLARE v_request platform.outbox_events%ROWTYPE; v_notification notifications.notifications%ROWTYPE;
        BEGIN
          SELECT * INTO v_request FROM platform.outbox_events
          WHERE id=p_outbox_id AND status='PROCESSING' AND lease_token=p_lease_token
            AND lease_expires_at>clock_timestamp() AND topic='notifications.send-requested' FOR UPDATE;
          IF NOT FOUND THEN RETURN false; END IF;
          SELECT * INTO v_notification FROM notifications.notifications
          WHERE id=p_notification_id AND owner_org_id=v_request.owner_org_id
            AND version=p_expected_version AND status='PENDING' FOR UPDATE;
          IF NOT FOUND OR v_request.aggregate_id<>p_notification_id THEN RETURN false; END IF;
          UPDATE notifications.notifications SET status='FAILED',version=version+1,
            last_provider_attempt_code='MAX_ATTEMPTS_EXHAUSTED',last_attempt_at=p_occurred_at,updated_at=p_occurred_at
            WHERE id=p_notification_id AND version=p_expected_version;
          INSERT INTO notifications.notification_status_events(
            id,owner_org_id,notification_id,version,status,attempts,provider_attempt_code,occurred_at)
          VALUES(pg_catalog.gen_random_uuid(),v_notification.owner_org_id,p_notification_id,p_expected_version+1,
            'FAILED',v_notification.attempts,'MAX_ATTEMPTS_EXHAUSTED',p_occurred_at);
          PERFORM security.emit_notification_status_changed(v_notification.owner_org_id,p_notification_id,
            p_expected_version+1,v_notification.channel,'FAILED',v_notification.attempts,p_occurred_at);
          UPDATE platform.outbox_events SET status='DEAD',last_error='MAX_ATTEMPTS_EXHAUSTED',processed_at=clock_timestamp(),
            locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
            WHERE id=p_outbox_id AND status='PROCESSING' AND lease_token=p_lease_token;
          RETURN FOUND;
        END
        $fn$;

        REVOKE ALL ON notifications.notification_templates,notifications.notification_status_events FROM PUBLIC;
        GRANT USAGE ON SCHEMA notifications,organizations,identity TO paqueteria_outbox_executor;
        REVOKE ALL ON notifications.notifications,notifications.notification_templates,
          notifications.notification_status_events FROM paqueteria_worker,paqueteria_app;
        GRANT SELECT ON notifications.notifications,notifications.notification_templates,
          notifications.notification_status_events TO paqueteria_app;
        GRANT SELECT,INSERT,UPDATE ON notifications.notifications TO paqueteria_outbox_executor;
        GRANT SELECT,INSERT ON notifications.notification_status_events TO paqueteria_outbox_executor;
        GRANT SELECT,INSERT ON notifications.notification_templates TO paqueteria_outbox_executor;
        GRANT INSERT ON platform.outbox_events TO paqueteria_outbox_executor;
        GRANT SELECT (id,status) ON identity.users TO paqueteria_outbox_executor;
        GRANT SELECT (user_id,organization_id,role,status) ON organizations.organization_memberships TO paqueteria_outbox_executor;

        RESET ROLE;
        ALTER FUNCTION security.resolve_outbox_consumer(text) OWNER TO paqueteria_outbox_executor;
        ALTER FUNCTION security.claim_realtime_outbox(text,integer,interval) OWNER TO paqueteria_outbox_executor;
        ALTER FUNCTION security.claim_notifications_outbox(text,integer,interval) OWNER TO paqueteria_outbox_executor;
        ALTER FUNCTION security.claim_unowned_outbox(text,integer,interval) OWNER TO paqueteria_outbox_executor;
        ALTER FUNCTION security.requeue_stale_realtime_outbox(interval,integer,integer) OWNER TO paqueteria_outbox_executor;
        ALTER FUNCTION security.recover_stale_notifications_outbox(text,integer,integer,interval) OWNER TO paqueteria_outbox_executor;
        ALTER FUNCTION security.requeue_stale_unowned_outbox(interval,integer,integer) OWNER TO paqueteria_outbox_executor;
        ALTER FUNCTION security.read_owner_dispatcher_ids(uuid,integer) OWNER TO paqueteria_outbox_executor;
        ALTER FUNCTION security.read_active_notification_users(uuid[]) OWNER TO paqueteria_outbox_executor;
        ALTER FUNCTION security.emit_notification_status_changed(uuid,uuid,integer,text,text,integer,timestamptz) OWNER TO paqueteria_outbox_executor;
        ALTER FUNCTION security.expand_order_created_notifications(uuid,uuid,uuid,text,text,timestamptz,uuid[]) OWNER TO paqueteria_outbox_executor;
        ALTER FUNCTION security.read_notification_delivery(uuid,uuid) OWNER TO paqueteria_outbox_executor;
        ALTER FUNCTION security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamptz,timestamptz) OWNER TO paqueteria_outbox_executor;
        ALTER FUNCTION security.finalize_notification_max_attempts(uuid,uuid,uuid,integer,timestamptz) OWNER TO paqueteria_outbox_executor;
        ALTER FUNCTION notifications.provision_default_templates() OWNER TO paqueteria_outbox_executor;
        REVOKE ALL ON FUNCTION security.resolve_outbox_consumer(text) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.claim_realtime_outbox(text,integer,interval) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.claim_notifications_outbox(text,integer,interval) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.claim_unowned_outbox(text,integer,interval) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.requeue_stale_realtime_outbox(interval,integer,integer) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.recover_stale_notifications_outbox(text,integer,integer,interval) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.requeue_stale_unowned_outbox(interval,integer,integer) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.read_owner_dispatcher_ids(uuid,integer) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.read_active_notification_users(uuid[]) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.emit_notification_status_changed(uuid,uuid,integer,text,text,integer,timestamptz) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.expand_order_created_notifications(uuid,uuid,uuid,text,text,timestamptz,uuid[]) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.read_notification_delivery(uuid,uuid) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamptz,timestamptz) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.finalize_notification_max_attempts(uuid,uuid,uuid,integer,timestamptz) FROM PUBLIC;
        REVOKE ALL ON FUNCTION notifications.provision_default_templates() FROM PUBLIC;
        REVOKE EXECUTE ON FUNCTION security.claim_outbox(text,integer,interval) FROM paqueteria_worker;
        REVOKE EXECUTE ON FUNCTION security.requeue_stale_outbox(interval,integer,integer) FROM paqueteria_worker;
        GRANT EXECUTE ON FUNCTION security.resolve_outbox_consumer(text),
          security.claim_realtime_outbox(text,integer,interval),
          security.claim_notifications_outbox(text,integer,interval),
          security.claim_unowned_outbox(text,integer,interval),
          security.requeue_stale_realtime_outbox(interval,integer,integer),
          security.recover_stale_notifications_outbox(text,integer,integer,interval),
          security.requeue_stale_unowned_outbox(interval,integer,integer),
          security.read_owner_dispatcher_ids(uuid,integer),
          security.read_active_notification_users(uuid[]),
          security.expand_order_created_notifications(uuid,uuid,uuid,text,text,timestamptz,uuid[]),
          security.read_notification_delivery(uuid,uuid),
          security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamptz,timestamptz),
          security.finalize_notification_max_attempts(uuid,uuid,uuid,integer,timestamptz)
          TO paqueteria_worker;
        SET ROLE paqueteria_migrator;
        """;

    public const string OperationalRollbackSql =
        """
        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;
        DO $rollback$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM platform.outbox_events
            WHERE security.resolve_outbox_consumer(topic)='NOTIFICATIONS'
              AND status IN ('PENDING','RETRY','PROCESSING')
          ) THEN
            RAISE EXCEPTION 'NTF-001 rollback blocked by active Notifications-owned rows';
          END IF;
        END
        $rollback$;
        RESET ROLE;
        GRANT EXECUTE ON FUNCTION security.claim_outbox(text,integer,interval) TO paqueteria_worker;
        GRANT EXECUTE ON FUNCTION security.requeue_stale_outbox(interval,integer,integer) TO paqueteria_worker;
        REVOKE EXECUTE ON FUNCTION security.claim_realtime_outbox(text,integer,interval),
          security.claim_notifications_outbox(text,integer,interval),
          security.claim_unowned_outbox(text,integer,interval),
          security.requeue_stale_realtime_outbox(interval,integer,integer),
          security.recover_stale_notifications_outbox(text,integer,integer,interval),
          security.requeue_stale_unowned_outbox(interval,integer,integer),
          security.read_owner_dispatcher_ids(uuid,integer),
          security.read_active_notification_users(uuid[]),
          security.expand_order_created_notifications(uuid,uuid,uuid,text,text,timestamptz,uuid[]),
          security.read_notification_delivery(uuid,uuid),
          security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamptz,timestamptz),
          security.finalize_notification_max_attempts(uuid,uuid,uuid,integer,timestamptz)
          FROM paqueteria_worker;
        SET ROLE paqueteria_migrator;
        """;
}
