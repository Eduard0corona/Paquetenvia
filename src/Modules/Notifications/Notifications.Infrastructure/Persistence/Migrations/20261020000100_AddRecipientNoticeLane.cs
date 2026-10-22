using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (project owner, E4 "Los 4 del plan (Recommended)"; AI-08 NTF-003, AI-06 and AI-18
/// declared contract, AI-24 <c>recipient_notices</c>, AI-12 <c>NotificationStatusChanged</c> v2). The Notifications lane
/// step that lets the lane carry recipient notices on WhatsApp and e-mail. It is inert: no producer writes
/// <c>orders.recipient-notice-requested</c> yet (AI-08 NTF-004) and nothing calls the provider for those channels until
/// AI-08 NTF-005.
/// <list type="bullet">
/// <item><c>notification_templates</c>: channel IN_APP, WHATSAPP or EMAIL; body only (and always) for IN_APP; the
/// variable catalog closed per template key; one immutable row per organization for each of the four recipient notice
/// keys on both channels, provisioned like <c>orders.created.operations</c>.</item>
/// <item><c>notifications</c>: <c>recipient_kind</c>, <c>order_event_id</c> and <c>delivered_at</c>;
/// <c>recipient_user_id</c> present exactly for IN_APP; one ORDER_DESTINATION row per owner organization, order event,
/// channel and template key; <c>provider_reference</c> unique per channel; a recipient notice never holds a recipient
/// value.</item>
/// <item><c>notification_status_events</c>: status also DELIVERED and CANCELLED.</item>
/// <item><c>notifications.recipient_opt_outs</c>: the platform-wide, append-only store of keyed hashes, FORCE ROW LEVEL
/// SECURITY without a policy, no privilege for a runtime role.</item>
/// <item><c>security.resolve_outbox_consumer</c> routes <c>orders.recipient-notice-requested</c> to NOTIFICATIONS;
/// <c>security.expand_recipient_notice_request</c>, <c>security.cancel_notification</c> and
/// <c>security.is_recipient_opted_out</c> are new; <c>security.read_notification_delivery</c> and
/// <c>security.apply_notification_outcome</c> move to v2 (the published 8-argument settle is dropped);
/// <c>security.emit_notification_status_changed</c> writes <c>notification-status-changed-v2</c>.</item>
/// </list>
/// Every function stays owned by <c>paqueteria_outbox_executor</c>, SECURITY DEFINER where it was or is new, with a
/// pinned search_path, no dynamic SQL and no RETURNING, EXECUTE revoked from PUBLIC and held only by
/// <c>paqueteria_worker</c>. No runtime role gains a table privilege.
/// </summary>
/// <remarks>
/// NTF-001 rollback blocked: the rollback refuses while any row needs the new shape (an ORDER_DESTINATION
/// Notification, a DELIVERED or CANCELLED status, a provider reference, an opt-out, an active
/// <c>orders.recipient-notice-requested</c> row or an active v2 status-changed row) and otherwise restores the published
/// bodies, signatures, constraints and grants.
/// </remarks>
[DbContext(typeof(NotificationsDbContext))]
[Migration(MigrationId)]
public sealed class AddRecipientNoticeLane : Migration
{
    public const string MigrationId = "20261020000100_AddRecipientNoticeLane";
    public const string DecisionId = "NTF-RECIPIENT-NOTICES-2026-10-20";
    public const string DowngradeBlockedMessage = "NTF003_DOWNGRADE_BLOCKED_DATA_PRESENT";
    public const string RequestTopic = "orders.recipient-notice-requested";

    public const string PublishedApplySignature =
        "security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamp with time zone,timestamp with time zone)";

    public const string ApplySignature =
        "security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,text,timestamp with time zone,timestamp with time zone)";

    public const string ExpandSignature =
        "security.expand_recipient_notice_request(uuid,uuid,boolean,interval,time without time zone,time without time zone,text,text[])";

    public const string CancelSignature =
        "security.cancel_notification(uuid,uuid,uuid,integer,text,timestamp with time zone)";

    public const string OptOutSignature = "security.is_recipient_opted_out(text,bytea[])";

    public const string ReadDeliverySignature = "security.read_notification_delivery(uuid,uuid)";

    public const string EmitSignature = CarryOutboxTraceContext.EmitSignature;

    /// <summary>The four recipient notice templates (AI-24 <c>recipient_notices.notices</c>), version 1.</summary>
    public static IReadOnlyList<string> RecipientTemplateKeys { get; } = Array.AsReadOnly(new[]
    {
        "recipient.tracking-link",
        "recipient.in-transit",
        "recipient.delivery-failed",
        "recipient.delivered",
    });

    /// <summary>
    /// The send-time cancellation reasons (AI-24 <c>recipient_notices</c> validity, staleness, caps and opt-out): the
    /// only codes of the outcome CANCELLED.
    /// </summary>
    public static IReadOnlyList<string> CancellationCodes { get; } = Array.AsReadOnly(new[]
    {
        "NOTICE_SUPERSEDED",
        "NOTICE_STALE",
        "NOTICE_LIMIT_REACHED",
        "RECIPIENT_OPTED_OUT",
    });

    /// <summary>The NTF-001 synthetic IN_APP provider codes, the only codes of an IN_APP Notification.</summary>
    public static IReadOnlyList<string> InAppCodes { get; } = Array.AsReadOnly(new[]
    {
        "SYNTHETIC_ACCEPTED",
        "SYNTHETIC_TRANSIENT",
        "SYNTHETIC_PERMANENT",
        "SYNTHETIC_AMBIGUOUS_TIMEOUT",
    });

    /// <summary>
    /// The codes of a WhatsApp or e-mail Notification: the closed <c>MessagingResultCodes</c> set, the terminal ambiguous
    /// reason of NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02, the permanent <c>TRACKING_LINK_UNAVAILABLE</c> and the
    /// cancellation reasons.
    /// </summary>
    public static IReadOnlyList<string> ExternalCodes { get; } = Array.AsReadOnly(new[]
    {
        "MESSAGING_ACCEPTED",
        "MESSAGING_SYNTHETIC_ACCEPTED",
        "MESSAGING_SYNTHETIC_TRANSIENT",
        "MESSAGING_SYNTHETIC_PERMANENT",
        "MESSAGING_SYNTHETIC_AMBIGUOUS_TIMEOUT",
        "MESSAGING_CHANNEL_DISABLED",
        "MESSAGING_TEMPLATE_NOT_CONFIGURED",
        "MESSAGING_TEMPLATE_PARAMETERS_INVALID",
        "MESSAGING_RECIPIENT_INVALID",
        "MESSAGING_PROVIDER_TIMEOUT",
        "MESSAGING_PROVIDER_UNREACHABLE",
        "MESSAGING_PROVIDER_RATE_LIMITED",
        "MESSAGING_PROVIDER_UNAVAILABLE",
        "MESSAGING_PROVIDER_AUTH_FAILED",
        "MESSAGING_PROVIDER_REJECTED",
        "MESSAGING_PROVIDER_RESPONSE_INVALID",
        "MESSAGING_RECIPIENT_UNDELIVERABLE",
        "MESSAGING_REENGAGEMENT_REQUIRED",
        "MESSAGING_PROVIDER_TEMPLATE_REJECTED",
        "MESSAGING_POLICY_BLOCKED",
        "MESSAGING_PROVIDER_CIRCUIT_OPEN",
        "MESSAGING_PROVIDER_CONCURRENCY_LIMITED",
        "AMBIGUOUS_TIMEOUT",
        "TRACKING_LINK_UNAVAILABLE",
        "NOTICE_SUPERSEDED",
        "NOTICE_STALE",
        "NOTICE_LIMIT_REACHED",
        "RECIPIENT_OPTED_OUT",
    });

    /// <summary>The published D8 router (<see cref="AddDispatchOutboxLane.UpSql"/>), as CREATE OR REPLACE.</summary>
    public static string PublishedResolverSql { get; } = PublishedFunction(
        AddDispatchOutboxLane.UpSql, "CREATE OR REPLACE FUNCTION ", "security.resolve_outbox_consumer(");

    /// <summary>The published NTF-001 template provisioning trigger function, as CREATE OR REPLACE.</summary>
    public static string PublishedProvisionSql { get; } = "CREATE OR REPLACE FUNCTION " + PublishedFunction(
        AddTenantSafeOutboxNotifications.UpSql, "CREATE FUNCTION ", "notifications.provision_default_templates(")
        ["CREATE FUNCTION ".Length..];

    /// <summary>The published NTF-001 delivery read, as CREATE (its result type changes, so it is dropped first).</summary>
    public static string PublishedReadDeliverySql { get; } = PublishedFunction(
        AddTenantSafeOutboxNotifications.UpSql, "CREATE FUNCTION ", "security.read_notification_delivery(");

    /// <summary>
    /// The published 8-argument settle (<see cref="FailAmbiguousWhatsAppNotifications.UpSql"/>), as CREATE: the rollback
    /// re-creates it byte for byte after dropping v2.
    /// </summary>
    public static string PublishedApplySql { get; } = "CREATE FUNCTION " + PublishedFunction(
        FailAmbiguousWhatsAppNotifications.UpSql, "CREATE OR REPLACE FUNCTION ", "security.apply_notification_outcome(")
        ["CREATE OR REPLACE FUNCTION ".Length..];

    /// <summary>The published OBS-003 status-change emitter (<see cref="CarryOutboxTraceContext.EmitSql"/>).</summary>
    public static string PublishedEmitSql { get; } = CarryOutboxTraceContext.EmitSql;

    /// <summary>The only differences from the published router, as (published text, new text).</summary>
    public static IReadOnlyList<(string Published, string Replacement)> ResolverEdits { get; } = Array.AsReadOnly(new[]
    {
        (
            "    WHEN p_topic IN ('orders.created','notifications.send-requested') THEN 'NOTIFICATIONS'\n",
            "    WHEN p_topic IN ('orders.created','notifications.send-requested',\n" +
            "      'orders.recipient-notice-requested') THEN 'NOTIFICATIONS'\n"
        ),
    });

    /// <summary>The only differences from the published provisioning trigger function, as (published text, new text).</summary>
    public static IReadOnlyList<(string Published, string Replacement)> ProvisionEdits { get; } = Array.AsReadOnly(new[]
    {
        (
            "    '[\"occurred_at\",\"order_public_id\",\"order_status\"]'::jsonb);\n" +
            "  RETURN NEW;\n",
            "    '[\"occurred_at\",\"order_public_id\",\"order_status\"]'::jsonb);\n" +
            "  -- NTF-RECIPIENT-NOTICES-2026-10-20: the four recipient notice templates, version 1, on WhatsApp and e-mail.\n" +
            "  -- Their texts live in WhatsApp Manager and in configuration (AI-24 recipient_notices.templates), never here.\n" +
            "  INSERT INTO notifications.notification_templates(\n" +
            "    owner_org_id,template_key,version,channel,body,allowed_variables)\n" +
            "  SELECT NEW.id,k.template_key,1,c.channel,NULL,'[\"order_public_id\"]'::jsonb\n" +
            "  FROM (VALUES ('recipient.tracking-link'),('recipient.in-transit'),('recipient.delivery-failed'),\n" +
            "    ('recipient.delivered')) k(template_key)\n" +
            "  CROSS JOIN (VALUES ('WHATSAPP'),('EMAIL')) c(channel);\n" +
            "  RETURN NEW;\n"
        ),
    });

    /// <summary>The only differences from the published emitter, as (published text, new text).</summary>
    public static IReadOnlyList<(string Published, string Replacement)> EmitEdits { get; } = Array.AsReadOnly(new[]
    {
        (
            "DECLARE v_trace_parent text;\n",
            "DECLARE v_trace_parent text; v_notification notifications.notifications%ROWTYPE; v_payload jsonb; v_notice text;\n"
        ),
        (
            "  WHERE n.id=p_notification_id AND n.owner_org_id=p_owner_org_id;\n",
            "  WHERE n.id=p_notification_id AND n.owner_org_id=p_owner_org_id;\n" +
            "  -- NTF-RECIPIENT-NOTICES-2026-10-20 (AI-12 NotificationStatusChanged v2): a recipient notice also carries its\n" +
            "  -- order and its notice; an IN_APP notification carries neither. Never a recipient, a parameter or a code.\n" +
            "  SELECT n.* INTO v_notification FROM notifications.notifications n\n" +
            "  WHERE n.id=p_notification_id AND n.owner_org_id=p_owner_org_id;\n" +
            "  v_payload=jsonb_build_object(\n" +
            "    'schema_version','notification-status-changed-v2','notification_id',p_notification_id,\n" +
            "    'channel',p_channel,'status',p_status,'attempts',p_attempts,'occurred_at',p_occurred_at);\n" +
            "  IF v_notification.recipient_kind='ORDER_DESTINATION' THEN\n" +
            "    v_notice=CASE v_notification.template_key\n" +
            "      WHEN 'recipient.tracking-link' THEN 'TRACKING_LINK'\n" +
            "      WHEN 'recipient.in-transit' THEN 'IN_TRANSIT'\n" +
            "      WHEN 'recipient.delivery-failed' THEN 'DELIVERY_FAILED'\n" +
            "      WHEN 'recipient.delivered' THEN 'DELIVERED' END;\n" +
            "    IF v_notice IS NULL OR v_notification.order_id IS NULL THEN\n" +
            "      RAISE EXCEPTION 'NOTICE_UNMAPPED' USING ERRCODE='22023';\n" +
            "    END IF;\n" +
            "    v_payload=v_payload || jsonb_build_object('order_id',v_notification.order_id,'notice',v_notice);\n" +
            "  END IF;\n"
        ),
        (
            "    jsonb_build_object(\n" +
            "      'schema_version','notification-status-changed-v1','notification_id',p_notification_id,\n" +
            "      'channel',p_channel,'status',p_status,'attempts',p_attempts,'occurred_at',p_occurred_at),\n",
            "    v_payload,\n"
        ),
    });

    /// <summary>The router this step installs.</summary>
    public static string ResolverSql { get; } = ApplyEdits(PublishedResolverSql, ResolverEdits);

    /// <summary>The template provisioning trigger function this step installs.</summary>
    public static string ProvisionSql { get; } = ApplyEdits(PublishedProvisionSql, ProvisionEdits);

    /// <summary>The emitter this step installs (payload <c>notification-status-changed-v2</c>).</summary>
    public static string EmitSql { get; } = ApplyEdits(PublishedEmitSql, EmitEdits);

    /// <summary>
    /// <c>security.read_notification_delivery</c> v2: the published columns plus <c>recipient_kind</c>, <c>order_id</c>,
    /// <c>order_event_id</c> and <c>created_at</c> (the occurred_at of the order event, for the staleness bound); the
    /// template body is NULL for WhatsApp and e-mail.
    /// </summary>
    public const string ReadDeliverySql =
        """
        CREATE FUNCTION security.read_notification_delivery(p_notification_id uuid,p_owner_org_id uuid)
        RETURNS TABLE(notification_id uuid,owner_org_id uuid,channel text,status text,attempts integer,
          version integer,template_key text,template_version integer,variables_snapshot text,template_body text,
          recipient_kind text,order_id uuid,order_event_id uuid,created_at timestamptz)
        LANGUAGE sql STABLE SECURITY DEFINER
        SET search_path=pg_catalog,notifications,security,pg_temp AS $fn$
          SELECT n.id,n.owner_org_id,n.channel,n.status,n.attempts,n.version,n.template_key,
            n.template_version,n.variables_snapshot::text,t.body,n.recipient_kind,n.order_id,n.order_event_id,n.created_at
          FROM notifications.notifications n
          JOIN notifications.notification_templates t
            ON t.owner_org_id=n.owner_org_id AND t.template_key=n.template_key
            AND t.version=n.template_version AND t.channel=n.channel
          WHERE n.id=p_notification_id AND n.owner_org_id=p_owner_org_id;
        $fn$;

        """;

    /// <summary>
    /// <c>security.apply_notification_outcome</c> v2 (AI-24 <c>recipient_notices.outcome_function</c>): the published
    /// settle under the send request's <c>lease_token</c>, with the provider reference, the closed code allowlist per
    /// channel family and the outcome CANCELLED (no attempt counted, the request PROCESSED with its reason). The
    /// WhatsApp ambiguous rule of NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02 holds unchanged.
    /// </summary>
    public static string ApplySql { get; } =
        """
        CREATE FUNCTION security.apply_notification_outcome(
          p_outbox_id uuid,p_lease_token uuid,p_notification_id uuid,p_expected_version integer,
          p_outcome text,p_code text,p_provider_reference text,p_occurred_at timestamptz,p_available_at timestamptz)
        RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,platform,notifications,security,pg_temp AS $fn$
        DECLARE v_request platform.outbox_events%ROWTYPE; v_notification notifications.notifications%ROWTYPE;
          v_status text; v_outbox_status text; v_outcome text; v_code text; v_attempts integer;
        BEGIN
          -- NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-003): v2 of the NTF-001 settle. SUCCESS goes only with an accepted
          -- code, CANCELLED only with a cancellation reason, AMBIGUOUS_FAILED only with AMBIGUOUS_TIMEOUT and
          -- TRACKING_LINK_UNAVAILABLE is permanent; a provider reference is a short opaque provider id and never comes with
          -- a cancellation.
          IF p_outcome IS NULL OR p_code IS NULL
             OR p_outcome NOT IN ('SUCCESS','TRANSIENT','AMBIGUOUS','PERMANENT','AMBIGUOUS_FAILED','CANCELLED')
             OR p_code NOT IN (
        """ + SqlList(InAppCodes.Concat(ExternalCodes), "               ") + """
        )
             OR (p_outcome='SUCCESS')<>(p_code IN ('SYNTHETIC_ACCEPTED','MESSAGING_ACCEPTED','MESSAGING_SYNTHETIC_ACCEPTED'))
             OR (p_outcome='AMBIGUOUS_FAILED')<>(p_code='AMBIGUOUS_TIMEOUT')
             OR (p_outcome='CANCELLED')<>(p_code IN (
        """ + SqlList(CancellationCodes, "               ") + """
        ))
             OR (p_code='TRACKING_LINK_UNAVAILABLE' AND p_outcome<>'PERMANENT')
             OR (p_provider_reference IS NOT NULL AND (p_outcome='CANCELLED'
               OR char_length(p_provider_reference) NOT BETWEEN 1 AND 256
               OR p_provider_reference !~ '^[A-Za-z0-9._:=+/-]+$')) THEN
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
          -- The code family of the channel: the synthetic NTF-001 codes for IN_APP, the messaging codes (and the lane's
          -- own reasons) for WhatsApp and e-mail; only those recipient notices take a provider reference or a cancellation.
          IF (v_notification.channel='IN_APP') <> (p_code IN (
        """ + SqlList(InAppCodes, "               ") + """
        ))
             OR (v_notification.channel='IN_APP' AND p_provider_reference IS NOT NULL) THEN
            RAISE EXCEPTION 'invalid provider outcome' USING ERRCODE='22023';
          END IF;
          -- NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02: a WhatsApp send with an unknown outcome is never retried (the
          -- recipient must not receive it twice). AMBIGUOUS reported for a WhatsApp Notification is settled as
          -- AMBIGUOUS_FAILED, so this function fails safe; the terminal ambiguous outcome is refused for every other channel.
          v_outcome=CASE WHEN p_outcome='AMBIGUOUS' AND v_notification.channel='WHATSAPP'
            THEN 'AMBIGUOUS_FAILED' ELSE p_outcome END;
          IF v_outcome='AMBIGUOUS_FAILED' AND v_notification.channel<>'WHATSAPP' THEN
            RAISE EXCEPTION 'invalid provider outcome' USING ERRCODE='22023';
          END IF;
          v_code=CASE WHEN v_outcome='AMBIGUOUS_FAILED' THEN 'AMBIGUOUS_TIMEOUT' ELSE p_code END;
          v_status=CASE WHEN v_outcome='SUCCESS' THEN 'SENT'
            WHEN v_outcome IN ('PERMANENT','AMBIGUOUS_FAILED') THEN 'FAILED'
            WHEN v_outcome='CANCELLED' THEN 'CANCELLED' ELSE 'PENDING' END;
          v_outbox_status=CASE WHEN v_outcome IN ('SUCCESS','CANCELLED') THEN 'PROCESSED'
            WHEN v_outcome IN ('PERMANENT','AMBIGUOUS_FAILED') THEN 'DEAD' ELSE 'RETRY' END;
          IF v_outbox_status='RETRY' AND p_available_at IS NULL THEN
            RAISE EXCEPTION 'invalid provider outcome' USING ERRCODE='22023';
          END IF;
          -- A cancellation is decided before any provider call: no attempt is counted.
          v_attempts=CASE WHEN v_outcome='CANCELLED' THEN v_notification.attempts ELSE v_notification.attempts+1 END;
          UPDATE notifications.notifications SET status=v_status,attempts=v_attempts,version=version+1,
            last_provider_attempt_code=v_code,
            last_attempt_at=CASE WHEN v_outcome='CANCELLED' THEN last_attempt_at ELSE p_occurred_at END,
            updated_at=p_occurred_at,
            sent_at=CASE WHEN v_outcome='SUCCESS' THEN p_occurred_at ELSE sent_at END,
            provider_reference=COALESCE(p_provider_reference,provider_reference)
            WHERE id=p_notification_id AND version=p_expected_version;
          INSERT INTO notifications.notification_status_events(
            id,owner_org_id,notification_id,version,status,attempts,provider_attempt_code,occurred_at)
          VALUES(pg_catalog.gen_random_uuid(),v_notification.owner_org_id,p_notification_id,p_expected_version+1,
            v_status,v_attempts,v_code,p_occurred_at);
          PERFORM security.emit_notification_status_changed(v_notification.owner_org_id,p_notification_id,
            p_expected_version+1,v_notification.channel,v_status,v_attempts,p_occurred_at);
          UPDATE platform.outbox_events SET status=v_outbox_status,
            last_error=CASE WHEN v_outcome='SUCCESS' THEN NULL ELSE v_code END,
            processed_at=CASE WHEN v_outbox_status IN ('PROCESSED','DEAD') THEN clock_timestamp() ELSE NULL END,
            available_at=CASE WHEN v_outbox_status='RETRY' THEN p_available_at ELSE available_at END,
            locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
            WHERE id=p_outbox_id AND status='PROCESSING' AND lease_token=p_lease_token;
          RETURN FOUND;
        END
        $fn$;

        """;

    /// <summary>
    /// <c>security.cancel_notification</c>: the send-time cancellation of a recipient notice (no longer valid, stale,
    /// over a cap or opted out) under the send request's <c>lease_token</c>, before any decryption or provider call. It is
    /// the CANCELLED outcome of <c>security.apply_notification_outcome</c> v2 and nothing else.
    /// </summary>
    public static string CancelSql { get; } =
        """
        CREATE FUNCTION security.cancel_notification(
          p_outbox_id uuid,p_lease_token uuid,p_notification_id uuid,p_expected_version integer,
          p_code text,p_occurred_at timestamptz)
        RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,platform,notifications,security,pg_temp AS $fn$
        BEGIN
          IF p_code IS NULL OR p_code NOT IN (
        """ + SqlList(CancellationCodes, "               ") + """
        ) THEN
            RAISE EXCEPTION 'invalid cancellation reason' USING ERRCODE='22023';
          END IF;
          RETURN security.apply_notification_outcome(p_outbox_id,p_lease_token,p_notification_id,p_expected_version,
            'CANCELLED',p_code,NULL,p_occurred_at,NULL);
        END
        $fn$;

        """;

    /// <summary>
    /// <c>security.is_recipient_opted_out</c>: whether any of the keyed hashes of one contact (one per configured key
    /// version) is opted out on that channel. It reads only the opt-out store and returns only a boolean.
    /// </summary>
    public const string OptOutSql =
        """
        CREATE FUNCTION security.is_recipient_opted_out(p_channel text,p_contact_hashes bytea[])
        RETURNS boolean LANGUAGE plpgsql STABLE SECURITY DEFINER
        SET search_path=pg_catalog,notifications,pg_temp AS $fn$
        BEGIN
          IF p_channel IS NULL OR p_channel NOT IN ('WHATSAPP','EMAIL') OR p_contact_hashes IS NULL
             OR cardinality(p_contact_hashes) NOT BETWEEN 1 AND 16
             OR EXISTS (SELECT 1 FROM unnest(p_contact_hashes) h WHERE h IS NULL OR octet_length(h)<>32) THEN
            RAISE EXCEPTION 'invalid opt-out lookup' USING ERRCODE='22023';
          END IF;
          RETURN EXISTS (SELECT 1 FROM notifications.recipient_opt_outs o
            WHERE o.channel=p_channel AND o.contact_hash=ANY(p_contact_hashes));
        END
        $fn$;

        """;

    /// <summary>
    /// <c>security.expand_recipient_notice_request</c> (AI-24 <c>recipient_notices.expansion</c>): under the source row's
    /// <c>lease_token</c>, one transaction that turns one <c>orders.recipient-notice-requested</c> row into one
    /// ORDER_DESTINATION Notification per eligible channel (WhatsApp first), each with its PENDING status event, its
    /// status-changed row and its send request available at the start of the quiet-hours window; then the source ends
    /// PROCESSED with SOURCE_EXPANDED. The order, its event, the notice and the time come from the leased row itself; the
    /// caller supplies only what it read under FORCE RLS (the public id and the eligible channels) and the switches. With
    /// notices off the source ends PROCESSED with RECIPIENT_NOTICES_DISABLED, past the stale bound with NOTICE_STALE and
    /// without a channel with NO_ELIGIBLE_CHANNEL, creating nothing. A redelivered request inserts nothing twice.
    /// </summary>
    public const string ExpandSql =
        """
        CREATE FUNCTION security.expand_recipient_notice_request(
          p_source_id uuid,p_lease_token uuid,p_enabled boolean,p_maximum_delay interval,
          p_window_start time,p_window_end time,p_public_id text,p_channels text[])
        RETURNS text LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,platform,notifications,security,pg_temp AS $fn$
        DECLARE v_source platform.outbox_events%ROWTYPE; v_template notifications.notification_templates%ROWTYPE;
          v_order_id uuid; v_order_event_id uuid; v_template_key text; v_occurred_at timestamptz; v_channel text;
          v_notification_id uuid; v_variables jsonb; v_now timestamptz; v_local timestamp; v_available_at timestamptz;
          v_rows integer; v_code text;
        BEGIN
          SELECT * INTO v_source FROM platform.outbox_events
          WHERE id=p_source_id AND status='PROCESSING' AND lease_token=p_lease_token
            AND lease_expires_at>clock_timestamp() FOR UPDATE;
          IF NOT FOUND THEN RETURN 'LEASE_LOST'; END IF;
          IF v_source.topic<>'orders.recipient-notice-requested' OR v_source.aggregate_type<>'Order'
             OR v_source.owner_org_id IS NULL
             OR security.resolve_outbox_consumer(v_source.topic)<>'NOTIFICATIONS'
             OR jsonb_typeof(v_source.payload)<>'object'
             OR ARRAY(SELECT k COLLATE "C" FROM jsonb_object_keys(v_source.payload) k ORDER BY 1)
                <>ARRAY['notice','occurred_at','order_event_id','order_id','schema_version']::text[]
             OR v_source.payload->>'schema_version' IS DISTINCT FROM 'recipient-notice-requested-v1'
             OR jsonb_typeof(v_source.payload->'order_id')<>'string'
             OR jsonb_typeof(v_source.payload->'order_event_id')<>'string'
             OR jsonb_typeof(v_source.payload->'notice')<>'string'
             OR jsonb_typeof(v_source.payload->'occurred_at')<>'string'
             OR v_source.payload->>'order_id' !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
             OR v_source.payload->>'order_event_id' !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
             OR v_source.payload->>'occurred_at' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}([.][0-9]{1,6})?Z$' THEN
            RAISE EXCEPTION 'INVALID_PAYLOAD' USING ERRCODE='22023';
          END IF;
          BEGIN
            v_order_id=(v_source.payload->>'order_id')::uuid;
            v_order_event_id=(v_source.payload->>'order_event_id')::uuid;
            v_occurred_at=(v_source.payload->>'occurred_at')::timestamptz;
          EXCEPTION WHEN invalid_text_representation OR invalid_datetime_format OR datetime_field_overflow THEN
            RAISE EXCEPTION 'INVALID_PAYLOAD' USING ERRCODE='22023';
          END;
          IF v_order_id<>v_source.aggregate_id THEN RAISE EXCEPTION 'INVALID_PAYLOAD' USING ERRCODE='22023'; END IF;
          v_template_key=CASE v_source.payload->>'notice'
            WHEN 'TRACKING_LINK' THEN 'recipient.tracking-link'
            WHEN 'IN_TRANSIT' THEN 'recipient.in-transit'
            WHEN 'DELIVERY_FAILED' THEN 'recipient.delivery-failed'
            WHEN 'DELIVERED' THEN 'recipient.delivered' END;
          IF v_template_key IS NULL THEN RAISE EXCEPTION 'INVALID_PAYLOAD' USING ERRCODE='22023'; END IF;
          v_now=clock_timestamp();
          -- AI-24 recipient_notices.switches.enabled: with notices off nothing is created and nothing is read.
          IF p_enabled IS DISTINCT FROM true THEN
            v_code='RECIPIENT_NOTICES_DISABLED';
          ELSE
            IF p_maximum_delay IS NULL OR p_maximum_delay<interval '1 hour' OR p_maximum_delay>interval '24 hours'
               OR p_window_start IS NULL OR p_window_end IS NULL OR p_window_start>=p_window_end THEN
              RAISE EXCEPTION 'invalid recipient notice settings' USING ERRCODE='22023';
            END IF;
            IF v_now-v_occurred_at>p_maximum_delay THEN
              v_code='NOTICE_STALE';
            ELSE
              IF p_channels IS NULL OR array_ndims(p_channels)>1
                 OR EXISTS (SELECT 1 FROM unnest(p_channels) c WHERE c IS NULL OR c NOT IN ('WHATSAPP','EMAIL'))
                 OR cardinality(p_channels)<>(SELECT count(DISTINCT c) FROM unnest(p_channels) c) THEN
                RAISE EXCEPTION 'invalid recipient notice channels' USING ERRCODE='22023';
              END IF;
              IF cardinality(p_channels)=0 THEN
                v_code='NO_ELIGIBLE_CHANNEL';
              ELSIF p_public_id IS NULL OR p_public_id !~ '^[A-Za-z0-9_-]{1,64}$' THEN
                RAISE EXCEPTION 'INVALID_PAYLOAD' USING ERRCODE='22023';
              END IF;
            END IF;
          END IF;
          IF v_code IS NULL THEN
            -- Every template of the request must exist, exact, before anything is written.
            FOREACH v_channel IN ARRAY p_channels LOOP
              SELECT * INTO v_template FROM notifications.notification_templates t
              WHERE t.owner_org_id=v_source.owner_org_id AND t.template_key=v_template_key
                AND t.version=1 AND t.channel=v_channel;
              IF NOT FOUND THEN
                v_code=CASE WHEN EXISTS (SELECT 1 FROM notifications.notification_templates t
                    WHERE t.owner_org_id=v_source.owner_org_id AND t.template_key=v_template_key AND t.channel=v_channel)
                  THEN 'TEMPLATE_VERSION_UNKNOWN' ELSE 'TEMPLATE_NOT_FOUND' END;
              ELSIF v_template.allowed_variables<>'["order_public_id"]'::jsonb OR v_template.body IS NOT NULL THEN
                v_code='TEMPLATE_VARIABLE_UNKNOWN';
              END IF;
              EXIT WHEN v_code IS NOT NULL;
            END LOOP;
            IF v_code IS NOT NULL THEN
              UPDATE platform.outbox_events SET status='DEAD',last_error=v_code,
                processed_at=clock_timestamp(),locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
                WHERE id=p_source_id AND status='PROCESSING' AND lease_token=p_lease_token;
              RETURN v_code;
            END IF;
          ELSE
            UPDATE platform.outbox_events SET status='PROCESSED',last_error=v_code,
              processed_at=clock_timestamp(),locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
              WHERE id=p_source_id AND status='PROCESSING' AND lease_token=p_lease_token;
            RETURN v_code;
          END IF;
          -- AI-24 recipient_notices.quiet_hours: messages leave only from p_window_start (inclusive) to p_window_end
          -- (exclusive) in America/Mazatlan; a notice requested, or expanded, outside the window waits for its next start.
          v_local=GREATEST(v_occurred_at,v_now) AT TIME ZONE 'America/Mazatlan';
          v_available_at=CASE
            WHEN v_local::time>=p_window_start AND v_local::time<p_window_end THEN v_occurred_at
            WHEN v_local::time<p_window_start THEN (v_local::date+p_window_start) AT TIME ZONE 'America/Mazatlan'
            ELSE ((v_local::date+1)+p_window_start) AT TIME ZONE 'America/Mazatlan' END;
          v_variables=jsonb_build_object('order_public_id',p_public_id);
          FOR v_channel IN SELECT c FROM unnest(p_channels) c ORDER BY CASE c WHEN 'WHATSAPP' THEN 0 ELSE 1 END LOOP
            v_notification_id=pg_catalog.gen_random_uuid();
            INSERT INTO notifications.notifications(
              id,owner_org_id,order_id,order_event_id,recipient_kind,recipient_user_id,channel,status,attempts,version,
              template_key,template_version,variables_snapshot,source_event_id,created_at,updated_at)
            VALUES(v_notification_id,v_source.owner_org_id,v_order_id,v_order_event_id,'ORDER_DESTINATION',NULL,v_channel,
              'PENDING',0,1,v_template_key,1,v_variables,p_source_id,v_occurred_at,v_occurred_at)
            ON CONFLICT (owner_org_id,order_event_id,channel,template_key) WHERE recipient_kind='ORDER_DESTINATION'
            DO NOTHING;
            GET DIAGNOSTICS v_rows=ROW_COUNT;
            IF v_rows=1 THEN
              INSERT INTO notifications.notification_status_events(
                id,owner_org_id,notification_id,version,status,attempts,occurred_at)
              VALUES(pg_catalog.gen_random_uuid(),v_source.owner_org_id,v_notification_id,1,'PENDING',0,v_occurred_at);
              PERFORM security.emit_notification_status_changed(
                v_source.owner_org_id,v_notification_id,1,v_channel,'PENDING',0,v_occurred_at);
              INSERT INTO platform.outbox_events(
                id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
                payload,priority,status,attempts,available_at,created_at,trace_parent)
              VALUES(pg_catalog.gen_random_uuid(),v_source.owner_org_id,v_source.tenant_context,
                'notifications.send-requested','Notification',v_notification_id,1,
                jsonb_build_object('schema_version','notification-send-requested-v1','notification_id',v_notification_id),
                -- OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13: the trace context of the request row it expands.
                50,'PENDING',0,v_available_at,v_occurred_at,v_source.trace_parent);
            END IF;
          END LOOP;
          UPDATE platform.outbox_events SET status='PROCESSED',last_error='SOURCE_EXPANDED',
            processed_at=clock_timestamp(),locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
            WHERE id=p_source_id AND status='PROCESSING' AND lease_token=p_lease_token;
          IF NOT FOUND THEN RAISE EXCEPTION 'LEASE_LOST' USING ERRCODE='40001'; END IF;
          RETURN 'SOURCE_EXPANDED';
        END
        $fn$;

        """;

    private const string FunctionGrantsSql =
        """
        REVOKE ALL ON FUNCTION security.expand_recipient_notice_request(uuid,uuid,boolean,interval,time,time,text,text[]) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.read_notification_delivery(uuid,uuid) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,text,timestamptz,timestamptz) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.cancel_notification(uuid,uuid,uuid,integer,text,timestamptz) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.is_recipient_opted_out(text,bytea[]) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION security.expand_recipient_notice_request(uuid,uuid,boolean,interval,time,time,text,text[]),
          security.read_notification_delivery(uuid,uuid),
          security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,text,timestamptz,timestamptz),
          security.cancel_notification(uuid,uuid,uuid,integer,text,timestamptz),
          security.is_recipient_opted_out(text,bytea[])
          TO paqueteria_worker;

        """;

    private const string PublishedFunctionGrantsSql =
        """
        REVOKE ALL ON FUNCTION security.read_notification_delivery(uuid,uuid) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamptz,timestamptz) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION security.read_notification_delivery(uuid,uuid),
          security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamptz,timestamptz)
          TO paqueteria_worker;

        """;

    private static string TableUpSql { get; } =
        """
        ALTER TABLE notifications.notification_templates
          DROP CONSTRAINT notification_templates_channel_check,
          DROP CONSTRAINT notification_templates_allowed_variables_check,
          ALTER COLUMN body DROP NOT NULL;
        ALTER TABLE notifications.notification_templates
          ADD CONSTRAINT notification_templates_channel_check CHECK (channel IN ('IN_APP','WHATSAPP','EMAIL')),
          ADD CONSTRAINT notification_templates_catalog_check CHECK (
            (template_key='orders.created.operations' AND channel='IN_APP' AND body IS NOT NULL
              AND allowed_variables='["occurred_at","order_public_id","order_status"]'::jsonb)
            OR (template_key IN (
        """ + SqlList(RecipientTemplateKeys, "              ") + """
        ) AND channel IN ('WHATSAPP','EMAIL')
              AND body IS NULL AND allowed_variables='["order_public_id"]'::jsonb));

        ALTER TABLE notifications.notifications
          ADD COLUMN recipient_kind text NOT NULL DEFAULT 'OWNER_ORG_USER',
          ADD COLUMN order_event_id uuid,
          ADD COLUMN delivered_at timestamptz;
        ALTER TABLE notifications.notifications
          ALTER COLUMN recipient_user_id DROP NOT NULL,
          DROP CONSTRAINT notifications_in_app_recipient_required,
          ADD CONSTRAINT notifications_recipient_kind_check CHECK (recipient_kind IN ('OWNER_ORG_USER','ORDER_DESTINATION')),
          ADD CONSTRAINT notifications_recipient_shape_check CHECK (
            (recipient_kind='OWNER_ORG_USER' AND channel='IN_APP' AND recipient_user_id IS NOT NULL
              AND order_event_id IS NULL)
            OR (recipient_kind='ORDER_DESTINATION' AND channel IN ('WHATSAPP','EMAIL') AND recipient_user_id IS NULL
              AND order_id IS NOT NULL AND order_event_id IS NOT NULL
              AND recipient_ciphertext IS NULL AND pii_key_version IS NULL)),
          ADD CONSTRAINT notifications_owner_user_status_check CHECK (
            recipient_kind='ORDER_DESTINATION' OR status IN ('PENDING','SENT','FAILED')),
          ADD CONSTRAINT notifications_delivered_at_check CHECK ((status='DELIVERED')=(delivered_at IS NOT NULL)),
          ADD CONSTRAINT notifications_provider_reference_check CHECK (provider_reference IS NULL OR (
            recipient_kind='ORDER_DESTINATION' AND char_length(provider_reference) BETWEEN 1 AND 256
            AND provider_reference ~ '^[A-Za-z0-9._:=+/-]+$'));
        CREATE UNIQUE INDEX notifications_order_destination_uq
          ON notifications.notifications(owner_org_id,order_event_id,channel,template_key)
          WHERE recipient_kind='ORDER_DESTINATION';
        CREATE UNIQUE INDEX notifications_provider_reference_uq
          ON notifications.notifications(channel,provider_reference)
          WHERE provider_reference IS NOT NULL;

        ALTER TABLE notifications.notification_status_events
          DROP CONSTRAINT notification_status_events_status_check,
          ADD CONSTRAINT notification_status_events_status_check
            CHECK (status IN ('PENDING','SENT','DELIVERED','FAILED','CANCELLED'));

        -- AI-06 notifications.recipient_opt_outs: keyed hashes only (HMAC-SHA256 under the contact lookup key), never a
        -- number, an address, a message body or an organization; append-only; row security forced with no policy, so
        -- only the SECURITY DEFINER functions of AI-18, owned by a role that bypasses row security, reach it.
        CREATE TABLE notifications.recipient_opt_outs (
          contact_hash bytea NOT NULL,
          channel text NOT NULL,
          key_version integer NOT NULL,
          source text NOT NULL,
          recorded_at timestamptz NOT NULL,
          CONSTRAINT recipient_opt_outs_pkey PRIMARY KEY (contact_hash,channel),
          CONSTRAINT recipient_opt_outs_hash_check CHECK (octet_length(contact_hash)=32),
          CONSTRAINT recipient_opt_outs_channel_check CHECK (channel IN ('WHATSAPP','EMAIL')),
          CONSTRAINT recipient_opt_outs_key_version_check CHECK (key_version BETWEEN 1 AND 32767),
          CONSTRAINT recipient_opt_outs_source_check CHECK (
            (source='RECIPIENT_REPLY' AND channel='WHATSAPP')
            OR (source='EMAIL_LINK' AND channel='EMAIL')
            OR source='DISPATCHER')
        );
        ALTER TABLE notifications.recipient_opt_outs ENABLE ROW LEVEL SECURITY;
        ALTER TABLE notifications.recipient_opt_outs FORCE ROW LEVEL SECURITY;
        CREATE TRIGGER recipient_opt_outs_append_only
          BEFORE UPDATE OR DELETE ON notifications.recipient_opt_outs
          FOR EACH ROW EXECUTE FUNCTION platform.reject_runtime_mutation();
        -- The identity.bff_sessions precedent: the default module privileges of the runtime roles are removed.
        REVOKE ALL ON notifications.recipient_opt_outs FROM PUBLIC,paqueteria_app,paqueteria_worker;
        GRANT SELECT ON notifications.recipient_opt_outs TO paqueteria_outbox_executor;

        """;

    private static string TableDownSql { get; } =
        """
        DROP TABLE notifications.recipient_opt_outs;

        ALTER TABLE notifications.notification_status_events
          DROP CONSTRAINT notification_status_events_status_check,
          ADD CONSTRAINT notification_status_events_status_check CHECK (status IN ('PENDING','SENT','FAILED'));

        DROP INDEX notifications.notifications_provider_reference_uq;
        DROP INDEX notifications.notifications_order_destination_uq;
        ALTER TABLE notifications.notifications
          DROP CONSTRAINT notifications_provider_reference_check,
          DROP CONSTRAINT notifications_delivered_at_check,
          DROP CONSTRAINT notifications_owner_user_status_check,
          DROP CONSTRAINT notifications_recipient_shape_check,
          DROP CONSTRAINT notifications_recipient_kind_check,
          DROP COLUMN delivered_at,
          DROP COLUMN order_event_id,
          DROP COLUMN recipient_kind,
          ALTER COLUMN recipient_user_id SET NOT NULL,
          ADD CONSTRAINT notifications_in_app_recipient_required CHECK (channel <> 'IN_APP' OR recipient_user_id IS NOT NULL);

        ALTER TABLE notifications.notification_templates
          DROP CONSTRAINT notification_templates_catalog_check,
          DROP CONSTRAINT notification_templates_channel_check,
          ALTER COLUMN body SET NOT NULL,
          ADD CONSTRAINT notification_templates_channel_check CHECK (channel='IN_APP'),
          ADD CONSTRAINT notification_templates_allowed_variables_check
            CHECK (allowed_variables = '["occurred_at","order_public_id","order_status"]'::jsonb);

        """;

    private static string VerifySql(string label, bool installed) =>
        $$"""
        DO $ntf003_verify$
        DECLARE
          executor oid := 'paqueteria_outbox_executor'::regrole;
          worker_acl text := 'paqueteria_outbox_executor:EXECUTE:false,paqueteria_worker:EXECUTE:false';
          v_installed boolean := {{(installed ? "true" : "false")}};
        BEGIN
          IF (to_regprocedure('{{ApplySignature}}') IS NOT NULL) IS DISTINCT FROM v_installed
             OR (to_regprocedure('{{PublishedApplySignature}}') IS NOT NULL) IS DISTINCT FROM NOT v_installed
             OR (to_regprocedure('{{ExpandSignature}}') IS NOT NULL) IS DISTINCT FROM v_installed
             OR (to_regprocedure('{{CancelSignature}}') IS NOT NULL) IS DISTINCT FROM v_installed
             OR (to_regprocedure('{{OptOutSignature}}') IS NOT NULL) IS DISTINCT FROM v_installed
             OR (to_regclass('notifications.recipient_opt_outs') IS NOT NULL) IS DISTINCT FROM v_installed
             OR (SELECT count(*) FROM information_schema.columns
                 WHERE table_schema='notifications' AND table_name='notifications'
                   AND column_name IN ('recipient_kind','order_event_id','delivered_at'))
                IS DISTINCT FROM (CASE WHEN v_installed THEN 3 ELSE 0 END)::bigint
             -- Every Notifications lane routine this step touches: owner, SECURITY DEFINER, no dynamic SQL, no RETURNING
             -- and EXECUTE held only by its owner and paqueteria_worker.
             OR EXISTS (
               SELECT 1 FROM pg_proc p
               WHERE p.oid IN (to_regprocedure('{{ApplySignature}}'), to_regprocedure('{{PublishedApplySignature}}'),
                               to_regprocedure('{{ExpandSignature}}'), to_regprocedure('{{CancelSignature}}'),
                               to_regprocedure('{{OptOutSignature}}'), to_regprocedure('{{ReadDeliverySignature}}'))
                 AND (p.proowner <> executor
                   OR NOT p.prosecdef
                   OR p.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
                   OR p.prosrc ~* '(^|[^a-z_])RETURNING([^a-z_]|$)'
                   OR p.proconfig IS NULL OR p.proconfig[1] NOT LIKE 'search_path=pg_catalog, %'
                   OR p.proconfig[1] NOT LIKE '%, pg_temp'
                   OR (SELECT string_agg(pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type || ':' || acl.is_grantable::text,
                         ',' ORDER BY pg_get_userbyid(acl.grantee) COLLATE "C")
                       FROM aclexplode(p.proacl) acl) IS DISTINCT FROM worker_acl))
             OR EXISTS (
               SELECT 1 FROM pg_proc p
               WHERE p.oid IN (to_regprocedure('{{EmitSignature}}'), to_regprocedure('security.resolve_outbox_consumer(text)'),
                               to_regprocedure('notifications.provision_default_templates()'))
                 AND (p.proowner <> executor OR p.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
                   OR (position('notification-status-changed-v2' IN p.prosrc) > 0
                       OR position('orders.recipient-notice-requested' IN p.prosrc) > 0
                       OR position('recipient.tracking-link' IN p.prosrc) > 0) IS DISTINCT FROM v_installed))
             OR (SELECT count(*) FROM pg_proc p
                 WHERE p.oid IN (to_regprocedure('{{EmitSignature}}'), to_regprocedure('security.resolve_outbox_consumer(text)'),
                                 to_regprocedure('notifications.provision_default_templates()'))) <> 3
             OR has_schema_privilege('paqueteria_outbox_executor','security','CREATE')
             OR has_schema_privilege('paqueteria_outbox_executor','notifications','CREATE')
             OR has_table_privilege('paqueteria_outbox_executor','notifications.notification_templates','DELETE')
             OR (v_installed AND (
                  NOT (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class
                       WHERE oid=to_regclass('notifications.recipient_opt_outs'))
                  OR EXISTS (SELECT 1 FROM pg_policy WHERE polrelid=to_regclass('notifications.recipient_opt_outs'))
                  OR (SELECT string_agg(pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type || ':' || acl.is_grantable::text,
                        ',' ORDER BY pg_get_userbyid(acl.grantee) COLLATE "C", acl.privilege_type COLLATE "C")
                      FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) acl
                      WHERE c.oid=to_regclass('notifications.recipient_opt_outs')
                        AND acl.grantee <> c.relowner) IS DISTINCT FROM 'paqueteria_outbox_executor:SELECT:false'))
          THEN
            RAISE EXCEPTION 'NTF-RECIPIENT-NOTICES-2026-10-20 {{label}}: the Notifications lane differs from the AI-06 and AI-18 contract';
          END IF;
        END
        $ntf003_verify$;
        """;

    public static string UpSql { get; } = string.Concat(
        """
        -- NTF-001 rollback blocked: NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-003) evolves the Notifications lane for
        -- recipient notices; the published IN_APP path keeps its behaviour, owners and grants.
        GRANT CREATE ON SCHEMA security,notifications TO paqueteria_outbox_executor;

        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;

        DO $ntf003_adoption$
        BEGIN
          IF to_regprocedure('security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamp with time zone,timestamp with time zone)') IS NULL
             OR to_regclass('notifications.recipient_opt_outs') IS NOT NULL THEN
            RAISE EXCEPTION 'NTF-RECIPIENT-NOTICES-2026-10-20 requires the published NTF-001 Notifications lane';
          END IF;
          IF EXISTS (SELECT 1 FROM notifications.notifications
                     WHERE channel<>'IN_APP' OR recipient_user_id IS NULL OR provider_reference IS NOT NULL
                       OR recipient_ciphertext IS NOT NULL OR pii_key_version IS NOT NULL) THEN
            RAISE EXCEPTION 'NTF-RECIPIENT-NOTICES-2026-10-20 requires every existing Notification to be an IN_APP notification of an owner organization user';
          END IF;
        END
        $ntf003_adoption$;

        RESET ROLE;
        SET ROLE paqueteria_migrator;


        """,
        TableUpSql,
        """
        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;

        -- The organizations that hold the NTF-001 template get the recipient notice templates, provisioned like it.
        INSERT INTO notifications.notification_templates(
          owner_org_id,template_key,version,channel,body,allowed_variables)
        SELECT t.owner_org_id,k.template_key,1,c.channel,NULL,'["order_public_id"]'::jsonb
        FROM notifications.notification_templates t
        CROSS JOIN (VALUES ('recipient.tracking-link'),('recipient.in-transit'),('recipient.delivery-failed'),
          ('recipient.delivered')) k(template_key)
        CROSS JOIN (VALUES ('WHATSAPP'),('EMAIL')) c(channel)
        WHERE t.template_key='orders.created.operations' AND t.version=1 AND t.channel='IN_APP'
        ON CONFLICT DO NOTHING;


        """,
        ResolverSql,
        "\n",
        ProvisionSql,
        "\n",
        EmitSql,
        "\n",
        """
        DROP FUNCTION security.read_notification_delivery(uuid,uuid);
        DROP FUNCTION security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamptz,timestamptz);

        """,
        ReadDeliverySql,
        ApplySql,
        CancelSql,
        OptOutSql,
        ExpandSql,
        FunctionGrantsSql,
        """
        RESET ROLE;
        SET ROLE paqueteria_migrator;

        REVOKE CREATE ON SCHEMA security,notifications FROM paqueteria_outbox_executor;

        """,
        VerifySql("up", installed: true));

    /// <summary>
    /// Refuses while any row needs the new shape; otherwise removes the recipient notice templates, drops the new
    /// functions, re-creates the published 8-argument settle and delivery read, restores the published router, emitter
    /// and provisioning bodies, and restores the published columns, constraints and grants.
    /// </summary>
    public static string DownSql { get; } = string.Concat(
        """
        -- NTF-001 rollback blocked: only the NTF-RECIPIENT-NOTICES-2026-10-20 lane step is withdrawn, and only while no row
        -- needs its shape.
        GRANT CREATE ON SCHEMA security,notifications TO paqueteria_outbox_executor;
        -- Temporary: the outbox executor, which bypasses row security, deletes the recipient notice template rows; the grant
        -- is revoked right after.
        GRANT DELETE ON notifications.notification_templates TO paqueteria_outbox_executor;

        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;

        DO $ntf003_rollback$
        BEGIN
          IF EXISTS (SELECT 1 FROM notifications.notifications
                     WHERE recipient_kind<>'OWNER_ORG_USER' OR channel<>'IN_APP' OR order_event_id IS NOT NULL
                       OR delivered_at IS NOT NULL OR provider_reference IS NOT NULL OR status IN ('DELIVERED','CANCELLED'))
             OR EXISTS (SELECT 1 FROM notifications.notification_status_events WHERE status IN ('DELIVERED','CANCELLED'))
             OR EXISTS (SELECT 1 FROM notifications.recipient_opt_outs)
             OR EXISTS (SELECT 1 FROM platform.outbox_events
                        WHERE topic='orders.recipient-notice-requested' AND status IN ('PENDING','RETRY','PROCESSING'))
             OR EXISTS (SELECT 1 FROM platform.outbox_events
                        WHERE topic='notifications.status-changed' AND status IN ('PENDING','RETRY','PROCESSING')
                          AND payload->>'schema_version'='notification-status-changed-v2') THEN
            RAISE EXCEPTION 'NTF003_DOWNGRADE_BLOCKED_DATA_PRESENT' USING
              DETAIL = 'Recipient notice rows, opt-outs or active recipient notice outbox rows exist; the lane step cannot be withdrawn.',
              ERRCODE = 'P0001';
          END IF;
        END
        $ntf003_rollback$;

        RESET ROLE;
        SET ROLE paqueteria_migrator;
        ALTER TABLE notifications.notification_templates DISABLE TRIGGER notification_templates_immutable;
        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;
        DELETE FROM notifications.notification_templates WHERE template_key IN (
        """ + SqlList(RecipientTemplateKeys, "  ") + """
        );

        DROP FUNCTION security.expand_recipient_notice_request(uuid,uuid,boolean,interval,time,time,text,text[]);
        DROP FUNCTION security.is_recipient_opted_out(text,bytea[]);
        DROP FUNCTION security.cancel_notification(uuid,uuid,uuid,integer,text,timestamptz);
        DROP FUNCTION security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,text,timestamptz,timestamptz);
        DROP FUNCTION security.read_notification_delivery(uuid,uuid);


        """,
        PublishedReadDeliverySql,
        "\n",
        PublishedApplySql,
        "\n",
        PublishedFunctionGrantsSql,
        PublishedEmitSql,
        "\n",
        PublishedProvisionSql,
        "\n",
        PublishedResolverSql,
        """

        RESET ROLE;
        SET ROLE paqueteria_migrator;
        ALTER TABLE notifications.notification_templates ENABLE TRIGGER notification_templates_immutable;
        REVOKE DELETE ON notifications.notification_templates FROM paqueteria_outbox_executor;


        """,
        TableDownSql,
        """
        REVOKE CREATE ON SCHEMA security,notifications FROM paqueteria_outbox_executor;

        """,
        VerifySql("down", installed: false));

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);

    /// <summary>One quoted, comma-separated SQL literal per value, each on its own line with the given indentation.</summary>
    private static string SqlList(IEnumerable<string> values, string indent) =>
        "\n" + string.Join(",\n", values.Select(value => indent + "'" + value + "'"));

    /// <summary>
    /// One function of a published lane step, byte for byte, from <paramref name="create"/> through its closing
    /// <c>$fn$;</c>.
    /// </summary>
    private static string PublishedFunction(string sql, string create, string name)
    {
        var start = create + name;
        var from = sql.IndexOf(start, StringComparison.Ordinal);
        if (from < 0 || sql.IndexOf(start, from + start.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException($"The published lane SQL must define {name.TrimEnd('(')} exactly once.");
        }

        var to = sql.IndexOf("$fn$;\n", from, StringComparison.Ordinal);
        if (to < 0)
        {
            throw new InvalidOperationException($"The published definition of {name.TrimEnd('(')} has no end.");
        }

        return sql[from..(to + "$fn$;\n".Length)];
    }

    private static string ApplyEdits(string sql, IEnumerable<(string Published, string Replacement)> edits) =>
        edits.Aggregate(sql, (current, edit) => ReplaceOnce(current, edit.Published, edit.Replacement));

    private static string ReplaceOnce(string sql, string published, string replacement)
    {
        var index = sql.IndexOf(published, StringComparison.Ordinal);
        if (index < 0 || sql.IndexOf(published, index + published.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                $"The published function must contain '{published.Trim()}' exactly once.");
        }

        return string.Concat(sql.AsSpan(0, index), replacement, sql.AsSpan(index + published.Length));
    }
}
