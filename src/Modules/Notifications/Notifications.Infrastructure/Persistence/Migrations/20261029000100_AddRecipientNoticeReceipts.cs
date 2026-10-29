using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (project owner, round O: O5 "Ampliar AI-18 (Recommended)", O7 "Aprobar lo
/// propuesto (Recommended)", O10 "Sí, el registro de la baja basta (Recommended)" and O11 "Aprobarlas todas
/// (Recommended)"; AI-08 NTF-006; AI-06 and AI-18 recipient notices step 3; AI-24 <c>recipient_notices</c>). The
/// Notifications lane step of delivery receipts, recipient opt-outs and the automatic reply:
/// <list type="bullet">
/// <item>the role <c>paqueteria_notification_receipt_executor</c> (NOLOGIN BYPASSRLS, declared by AI-18, created here
/// idempotently for installations that predate it), granted to no runtime role, with exactly the column-limited
/// privileges of AI-18 step 3 (as amended by O5) and EXECUTE on the existing status-changed writer;</item>
/// <item><c>notifications.recipient_auto_replies</c>, the platform-wide automatic-reply store (AI-06), with FORCE ROW
/// LEVEL SECURITY and no policy, reached only through the functions below;</item>
/// <item>owned by the executor, SECURITY DEFINER with a pinned search_path, no dynamic SQL and no RETURNING:
/// <c>security.record_messaging_receipt</c>, <c>security.record_recipient_opt_out</c> and
/// <c>security.request_recipient_auto_reply</c> (EXECUTE for <c>paqueteria_app</c> only), and
/// <c>security.claim_recipient_auto_replies</c> and <c>security.settle_recipient_auto_reply</c> (EXECUTE for
/// <c>paqueteria_worker</c> only).</item>
/// </list>
/// Receipts move a recipient notice only forward (SENT to DELIVERED or FAILED) and ignore an unknown provider reference;
/// each change writes one status event and, through <c>security.emit_notification_status_changed</c>, one
/// <c>notifications.status-changed</c> row. No runtime role gains a table privilege.
/// </summary>
/// <remarks>
/// NTF-001 rollback blocked: the rollback refuses while any automatic-reply request exists; otherwise it drops the
/// functions and the store and revokes every privilege of the executor, which then stays (AI-18 declares it), inert.
/// </remarks>
[DbContext(typeof(NotificationsDbContext))]
[Migration(MigrationId)]
public sealed class AddRecipientNoticeReceipts : Migration
{
    public const string MigrationId = "20261029000100_AddRecipientNoticeReceipts";
    public const string DecisionId = "NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25";
    public const string DowngradeBlockedMessage = "NTF006_DOWNGRADE_BLOCKED_DATA_PRESENT";
    public const string Role = "paqueteria_notification_receipt_executor";

    public const string ReceiptSignature =
        "security.record_messaging_receipt(text,text,text,text,timestamp with time zone)";

    public const string OptOutSignature =
        "security.record_recipient_opt_out(text,text,bytea,integer,bytea[])";

    public const string RequestSignature =
        "security.request_recipient_auto_reply(uuid,text,bytea,integer,bytea[],bytea,text,timestamp with time zone)";

    public const string ClaimSignature = "security.claim_recipient_auto_replies(integer,interval)";

    public const string SettleSignature =
        "security.settle_recipient_auto_reply(uuid,uuid,text,text,timestamp with time zone)";

    public const string EmitSignature = CarryOutboxTraceContext.EmitSignature;

    /// <summary>The functions <c>paqueteria_app</c> executes (the webhooks and the e-mail unsubscribe).</summary>
    public static IReadOnlyList<string> ApiFunctions { get; } = Array.AsReadOnly(new[]
    {
        ReceiptSignature,
        OptOutSignature,
        RequestSignature,
    });

    /// <summary>The functions <c>paqueteria_worker</c> executes (the automatic-reply sender).</summary>
    public static IReadOnlyList<string> WorkerFunctions { get; } = Array.AsReadOnly(new[]
    {
        ClaimSignature,
        SettleSignature,
    });

    /// <summary>
    /// AI-24 <c>recipient_notices.receipts</c>: the only codes a FAILED receipt records - the closed
    /// <c>MessagingResultCodes</c> the Meta classifier and the ACS status map produce (O6).
    /// </summary>
    public static IReadOnlyList<string> ReceiptFailureCodes { get; } = Array.AsReadOnly(new[]
    {
        "MESSAGING_PROVIDER_AUTH_FAILED",
        "MESSAGING_PROVIDER_RATE_LIMITED",
        "MESSAGING_PROVIDER_UNAVAILABLE",
        "MESSAGING_PROVIDER_REJECTED",
        "MESSAGING_RECIPIENT_UNDELIVERABLE",
        "MESSAGING_REENGAGEMENT_REQUIRED",
        "MESSAGING_PROVIDER_TEMPLATE_REJECTED",
        "MESSAGING_POLICY_BLOCKED",
    });

    /// <summary>The codes of an automatic-reply request that ended SENT.</summary>
    public static IReadOnlyList<string> AutoReplySentCodes { get; } = Array.AsReadOnly(new[]
    {
        "MESSAGING_ACCEPTED",
        "MESSAGING_SYNTHETIC_ACCEPTED",
    });

    /// <summary>
    /// The codes of an automatic-reply request that ended FAILED after its one provider call: the closed messaging set
    /// (accepted excluded) and the WhatsApp ambiguous reason of NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02.
    /// </summary>
    public static IReadOnlyList<string> AutoReplyFailureCodes { get; } = Array.AsReadOnly(new[]
    {
        "MESSAGING_SYNTHETIC_TRANSIENT",
        "MESSAGING_SYNTHETIC_PERMANENT",
        "MESSAGING_SYNTHETIC_AMBIGUOUS_TIMEOUT",
        "MESSAGING_TEMPLATE_NOT_CONFIGURED",
        "MESSAGING_TEMPLATE_PARAMETERS_INVALID",
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
    });

    /// <summary>
    /// The codes of an automatic-reply request dropped unsent: notices or WhatsApp off (O7), older than 23 hours, or a
    /// sealed number the Worker cannot use.
    /// </summary>
    public static IReadOnlyList<string> AutoReplyDropCodes { get; } = Array.AsReadOnly(new[]
    {
        "MESSAGING_CHANNEL_DISABLED",
        "AUTO_REPLY_STALE",
        "MESSAGING_RECIPIENT_INVALID",
    });

    /// <summary>The store, exactly as AI-06 declares it.</summary>
    public const string TableDdl =
        """
        CREATE TABLE notifications.recipient_auto_replies (
          id uuid NOT NULL,
          kind text NOT NULL,
          contact_hash bytea NOT NULL,
          contact_key_version integer NOT NULL,
          sealed_number bytea,
          number_key_version text,
          received_at timestamptz NOT NULL,
          status text NOT NULL,
          attempts integer NOT NULL,
          available_at timestamptz NOT NULL,
          lease_token uuid,
          lease_expires_at timestamptz,
          last_code text,
          settled_at timestamptz,
          CONSTRAINT recipient_auto_replies_pkey PRIMARY KEY (id),
          CONSTRAINT recipient_auto_replies_kind_check CHECK (kind IN ('AUTO_REPLY','OPT_OUT_CONFIRMATION')),
          CONSTRAINT recipient_auto_replies_hash_check CHECK (octet_length(contact_hash)=32),
          CONSTRAINT recipient_auto_replies_key_version_check CHECK (contact_key_version BETWEEN 1 AND 32767),
          CONSTRAINT recipient_auto_replies_status_check CHECK (status IN ('PENDING','PROCESSING','SENT','FAILED','DROPPED')),
          CONSTRAINT recipient_auto_replies_number_check CHECK ((status IN ('PENDING','PROCESSING'))=(sealed_number IS NOT NULL) AND (sealed_number IS NULL)=(number_key_version IS NULL)),
          CONSTRAINT recipient_auto_replies_lease_check CHECK ((status='PROCESSING')=(lease_token IS NOT NULL) AND (lease_token IS NULL)=(lease_expires_at IS NULL)),
          CONSTRAINT recipient_auto_replies_settled_check CHECK ((status IN ('SENT','FAILED','DROPPED'))=(settled_at IS NOT NULL)),
          CONSTRAINT recipient_auto_replies_attempts_check CHECK (attempts BETWEEN 0 AND 1)
        );
        """;

    /// <summary>The indexes and the row security of the store, exactly as AI-06 declares them, one per line.</summary>
    public static IReadOnlyList<string> TableStatements { get; } = Array.AsReadOnly(new[]
    {
        "CREATE UNIQUE INDEX recipient_auto_replies_confirmation_uq ON notifications.recipient_auto_replies(contact_hash) WHERE kind='OPT_OUT_CONFIRMATION';",
        "CREATE INDEX recipient_auto_replies_contact_idx ON notifications.recipient_auto_replies(contact_hash,received_at) WHERE kind='AUTO_REPLY';",
        "CREATE INDEX recipient_auto_replies_pending_idx ON notifications.recipient_auto_replies(available_at) WHERE status='PENDING';",
        "ALTER TABLE notifications.recipient_auto_replies ENABLE ROW LEVEL SECURITY;",
        "ALTER TABLE notifications.recipient_auto_replies FORCE ROW LEVEL SECURITY;",
        "REVOKE ALL ON notifications.recipient_auto_replies FROM PUBLIC,paqueteria_app,paqueteria_worker;",
    });

    /// <summary>
    /// The exact privileges of the executor on tables and schemas (AI-18 recipient notices step 3 declares these
    /// statements byte for byte), issued by their owner <c>paqueteria_migrator</c>.
    /// </summary>
    public static IReadOnlyList<string> GrantStatements { get; } = Array.AsReadOnly(new[]
    {
        "GRANT USAGE ON SCHEMA notifications,security TO paqueteria_notification_receipt_executor;",
        "GRANT SELECT (id,owner_org_id,channel,status,provider_reference,version,attempts) ON notifications.notifications TO paqueteria_notification_receipt_executor;",
        "GRANT UPDATE (status,delivered_at,version,updated_at,last_provider_attempt_code) ON notifications.notifications TO paqueteria_notification_receipt_executor;",
        "GRANT INSERT (id,owner_org_id,notification_id,version,status,attempts,provider_attempt_code,occurred_at) ON notifications.notification_status_events TO paqueteria_notification_receipt_executor;",
        "GRANT SELECT (contact_hash,channel,source,recorded_at) ON notifications.recipient_opt_outs TO paqueteria_notification_receipt_executor;",
        "GRANT INSERT (contact_hash,channel,key_version,source,recorded_at) ON notifications.recipient_opt_outs TO paqueteria_notification_receipt_executor;",
        "GRANT SELECT (id,kind,contact_hash,sealed_number,number_key_version,received_at,status,attempts,available_at,lease_token,lease_expires_at) ON notifications.recipient_auto_replies TO paqueteria_notification_receipt_executor;",
        "GRANT INSERT (id,kind,contact_hash,contact_key_version,sealed_number,number_key_version,received_at,status,attempts,available_at) ON notifications.recipient_auto_replies TO paqueteria_notification_receipt_executor;",
        "GRANT UPDATE (status,attempts,available_at,lease_token,lease_expires_at,last_code,settled_at,sealed_number,number_key_version) ON notifications.recipient_auto_replies TO paqueteria_notification_receipt_executor;",
    });

    /// <summary>O5: the executor may call the existing status-changed writer, granted by its owner.</summary>
    public const string EmitGrantStatement =
        "GRANT EXECUTE ON FUNCTION security.emit_notification_status_changed(uuid,uuid,integer,text,text,integer,timestamptz) TO paqueteria_notification_receipt_executor;";

    /// <summary>
    /// The column privileges the grants above produce (schema.table.column:privilege), sorted as the catalog check
    /// sorts them (C collation).
    /// </summary>
    public static IReadOnlyList<string> ColumnGrants { get; } = Array.AsReadOnly(GrantStatements
        .Where(static statement => !statement.StartsWith("GRANT USAGE", StringComparison.Ordinal))
        .SelectMany(static statement =>
        {
            var privilege = statement["GRANT ".Length..statement.IndexOf(' ', "GRANT ".Length)];
            var open = statement.IndexOf('(', StringComparison.Ordinal);
            var close = statement.IndexOf(')', StringComparison.Ordinal);
            var table = statement[(statement.IndexOf(" ON ", StringComparison.Ordinal) + 4)..statement.IndexOf(" TO ", StringComparison.Ordinal)];
            return statement[(open + 1)..close].Split(',')
                .Select(column => $"{table}.{column}:{privilege}");
        })
        .Order(StringComparer.Ordinal)
        .ToArray());

    /// <summary>
    /// <c>security.record_messaging_receipt</c> (AI-24 <c>recipient_notices.receipts.function</c>): the delivery receipt
    /// of one provider reference. Only a SENT recipient notice moves (to DELIVERED or FAILED); a reference that matches
    /// nothing, a notice that is not SENT and a repeated receipt change nothing. It reads only the AI-18 columns and
    /// builds the status-changed row through the existing writer (O5).
    /// </summary>
    public static string ReceiptSql { get; } =
        """
        CREATE FUNCTION security.record_messaging_receipt(
          p_channel text,p_provider_reference text,p_status text,p_code text,p_reported_at timestamptz)
        RETURNS text LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,notifications,security,pg_temp AS $fn$
        DECLARE v_id uuid; v_owner uuid; v_channel text; v_status text; v_version integer; v_attempts integer;
          v_now timestamptz;
        BEGIN
          -- NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-24 recipient_notices.receipts): DELIVERED carries no code; FAILED
          -- carries one closed MessagingResultCodes code (O5, O6).
          IF p_channel IS NULL OR p_channel NOT IN ('WHATSAPP','EMAIL')
             OR p_provider_reference IS NULL OR char_length(p_provider_reference) NOT BETWEEN 1 AND 256
             OR p_provider_reference !~ '^[A-Za-z0-9._:=+/-]+$'
             OR p_status IS NULL OR p_status NOT IN ('DELIVERED','FAILED')
             OR (p_status='DELIVERED') <> (p_code IS NULL)
             OR (p_status='FAILED' AND p_code NOT IN (
        """ + SqlList(ReceiptFailureCodes, "               ") + """
        ))
             OR p_reported_at IS NULL THEN
            RAISE EXCEPTION 'invalid messaging receipt' USING ERRCODE='22023';
          END IF;
          SELECT n.id,n.owner_org_id,n.channel,n.status,n.version,n.attempts
            INTO v_id,v_owner,v_channel,v_status,v_version,v_attempts
          FROM notifications.notifications n
          WHERE n.channel=p_channel AND n.provider_reference=p_provider_reference
          FOR UPDATE;
          IF NOT FOUND THEN
            -- O11 choice 6: a receipt that arrives before its send was settled matches nothing and is ignored.
            RETURN 'UNKNOWN_REFERENCE';
          END IF;
          IF v_status<>'SENT' THEN
            -- Forward only: a repeated receipt, or one for a notice that already moved on, changes nothing.
            RETURN 'UNCHANGED';
          END IF;
          v_now=clock_timestamp();
          IF p_status='DELIVERED' THEN
            UPDATE notifications.notifications SET status='DELIVERED',delivered_at=LEAST(p_reported_at,v_now),
              version=v_version+1,updated_at=v_now
              WHERE id=v_id AND version=v_version;
          ELSE
            UPDATE notifications.notifications SET status='FAILED',last_provider_attempt_code=p_code,
              version=v_version+1,updated_at=v_now
              WHERE id=v_id AND version=v_version;
          END IF;
          INSERT INTO notifications.notification_status_events(
            id,owner_org_id,notification_id,version,status,attempts,provider_attempt_code,occurred_at)
          VALUES(pg_catalog.gen_random_uuid(),v_owner,v_id,v_version+1,p_status,v_attempts,p_code,v_now);
          PERFORM security.emit_notification_status_changed(v_owner,v_id,v_version+1,v_channel,p_status,v_attempts,v_now);
          RETURN 'RECORDED';
        END
        $fn$;

        """;

    /// <summary>
    /// <c>security.record_recipient_opt_out</c> (AI-24 <c>recipient_notices.opt_out</c>): records the opt-out of one
    /// contact hash (current key version) unless any configured version of the same contact is already opted out on the
    /// channel. Returns true only when this call recorded it. The store stays append-only; nothing is removed.
    /// </summary>
    public const string OptOutSql =
        """
        CREATE FUNCTION security.record_recipient_opt_out(
          p_channel text,p_source text,p_contact_hash bytea,p_key_version integer,p_known_hashes bytea[])
        RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,notifications,pg_temp AS $fn$
        DECLARE v_rows integer;
        BEGIN
          IF p_channel IS NULL OR p_channel NOT IN ('WHATSAPP','EMAIL')
             OR p_source IS NULL OR NOT ((p_source='RECIPIENT_REPLY' AND p_channel='WHATSAPP')
               OR (p_source='EMAIL_LINK' AND p_channel='EMAIL') OR p_source='DISPATCHER')
             OR p_contact_hash IS NULL OR octet_length(p_contact_hash)<>32
             OR p_key_version IS NULL OR p_key_version NOT BETWEEN 1 AND 32767
             OR p_known_hashes IS NULL OR array_ndims(p_known_hashes)<>1
             OR cardinality(p_known_hashes) NOT BETWEEN 1 AND 16
             OR EXISTS (SELECT 1 FROM unnest(p_known_hashes) h WHERE h IS NULL OR octet_length(h)<>32)
             OR NOT (p_contact_hash=ANY(p_known_hashes)) THEN
            RAISE EXCEPTION 'invalid recipient opt-out' USING ERRCODE='22023';
          END IF;
          IF EXISTS (SELECT 1 FROM notifications.recipient_opt_outs o
                     WHERE o.channel=p_channel AND o.contact_hash=ANY(p_known_hashes)) THEN
            RETURN false;
          END IF;
          -- recorded_at is the transaction time: the opt-out confirmation of the same message is requested in this
          -- same transaction (AI-24 recipient_notices.opt_out.confirmation).
          INSERT INTO notifications.recipient_opt_outs(contact_hash,channel,key_version,source,recorded_at)
          VALUES(p_contact_hash,p_channel,p_key_version,p_source,now())
          ON CONFLICT DO NOTHING;
          GET DIAGNOSTICS v_rows=ROW_COUNT;
          RETURN v_rows=1;
        END
        $fn$;

        """;

    /// <summary>
    /// <c>security.request_recipient_auto_reply</c> (AI-24 <c>recipient_notices.auto_reply</c> and
    /// <c>opt_out.confirmation</c>): records one request with the sender number sealed under the recipient-notice key.
    /// An automatic reply is refused when the contact already had one in the 24 hours before this message; the opt-out
    /// confirmation only for the message whose opt-out was recorded in this same transaction, and once per contact. A
    /// message older than 23 hours gets nothing.
    /// </summary>
    public const string RequestSql =
        """
        CREATE FUNCTION security.request_recipient_auto_reply(
          p_request_id uuid,p_kind text,p_contact_hash bytea,p_contact_key_version integer,p_known_hashes bytea[],
          p_sealed_number bytea,p_number_key_version text,p_received_at timestamptz)
        RETURNS text LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,notifications,pg_temp AS $fn$
        DECLARE v_now timestamptz;
        BEGIN
          v_now=clock_timestamp();
          IF p_request_id IS NULL OR p_kind IS NULL OR p_kind NOT IN ('AUTO_REPLY','OPT_OUT_CONFIRMATION')
             OR p_contact_hash IS NULL OR octet_length(p_contact_hash)<>32
             OR p_contact_key_version IS NULL OR p_contact_key_version NOT BETWEEN 1 AND 32767
             OR p_known_hashes IS NULL OR array_ndims(p_known_hashes)<>1
             OR cardinality(p_known_hashes) NOT BETWEEN 1 AND 16
             OR EXISTS (SELECT 1 FROM unnest(p_known_hashes) h WHERE h IS NULL OR octet_length(h)<>32)
             OR NOT (p_contact_hash=ANY(p_known_hashes))
             OR p_sealed_number IS NULL OR octet_length(p_sealed_number) NOT BETWEEN 1 AND 4096
             OR p_number_key_version IS NULL OR char_length(p_number_key_version) NOT BETWEEN 1 AND 300
             OR p_received_at IS NULL OR p_received_at>v_now+interval '5 minutes' THEN
            RAISE EXCEPTION 'invalid automatic reply request' USING ERRCODE='22023';
          END IF;
          -- AI-24 recipient_notices.auto_reply.path: Meta's 23-hour bound inside the 24-hour customer service window.
          IF p_received_at<=v_now-interval '23 hours' THEN
            RETURN 'STALE';
          END IF;
          -- One decision per contact at a time, so the limits below hold under concurrent deliveries.
          PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(pg_catalog.encode(p_contact_hash,'hex'),6006));
          IF p_kind='AUTO_REPLY' THEN
            IF EXISTS (SELECT 1 FROM notifications.recipient_auto_replies r
                       WHERE r.kind='AUTO_REPLY' AND r.contact_hash=ANY(p_known_hashes)
                         AND r.received_at>p_received_at-interval '24 hours') THEN
              RETURN 'LIMITED';
            END IF;
          ELSIF NOT EXISTS (SELECT 1 FROM notifications.recipient_opt_outs o
                            WHERE o.channel='WHATSAPP' AND o.contact_hash=p_contact_hash
                              AND o.source='RECIPIENT_REPLY' AND o.recorded_at=now())
                OR EXISTS (SELECT 1 FROM notifications.recipient_auto_replies r
                           WHERE r.kind='OPT_OUT_CONFIRMATION' AND r.contact_hash=ANY(p_known_hashes)) THEN
            RETURN 'NOT_ELIGIBLE';
          END IF;
          INSERT INTO notifications.recipient_auto_replies(
            id,kind,contact_hash,contact_key_version,sealed_number,number_key_version,received_at,status,attempts,
            available_at)
          VALUES(p_request_id,p_kind,p_contact_hash,p_contact_key_version,p_sealed_number,p_number_key_version,
            p_received_at,'PENDING',0,v_now);
          RETURN 'RECORDED';
        END
        $fn$;

        """;

    /// <summary>
    /// <c>security.claim_recipient_auto_replies</c>: the Worker's lease on a batch of pending requests. First a request
    /// whose lease expired ends FAILED with AMBIGUOUS_TIMEOUT (it may have reached Meta, which has no idempotency) and a
    /// pending request older than 23 hours ends DROPPED with AUTO_REPLY_STALE, both with the number erased; then up to
    /// <c>p_batch_size</c> pending requests are leased under one new lease token and returned.
    /// </summary>
    public const string ClaimSql =
        """
        CREATE FUNCTION security.claim_recipient_auto_replies(p_batch_size integer,p_lease interval)
        RETURNS TABLE(request_id uuid,kind text,sealed_number bytea,number_key_version text,received_at timestamptz,
          lease_token uuid)
        LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,notifications,pg_temp AS $fn$
        #variable_conflict use_column
        DECLARE v_now timestamptz; v_lease uuid; v_ids uuid[];
        BEGIN
          IF p_batch_size IS NULL OR p_batch_size NOT BETWEEN 1 AND 100
             OR p_lease IS NULL OR p_lease<interval '10 seconds' OR p_lease>interval '10 minutes' THEN
            RAISE EXCEPTION 'invalid automatic reply claim' USING ERRCODE='22023';
          END IF;
          v_now=clock_timestamp();
          -- NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03: a stale lease may have sent; it is never retried.
          UPDATE notifications.recipient_auto_replies SET status='FAILED',last_code='AMBIGUOUS_TIMEOUT',attempts=1,
            sealed_number=NULL,number_key_version=NULL,lease_token=NULL,lease_expires_at=NULL,settled_at=v_now
            WHERE status='PROCESSING' AND lease_expires_at<=v_now;
          UPDATE notifications.recipient_auto_replies SET status='DROPPED',last_code='AUTO_REPLY_STALE',
            sealed_number=NULL,number_key_version=NULL,settled_at=v_now
            WHERE status='PENDING' AND received_at<=v_now-interval '23 hours';
          v_ids=ARRAY(
            SELECT r.id FROM notifications.recipient_auto_replies r
            WHERE r.status='PENDING' AND r.available_at<=v_now
            ORDER BY r.received_at,r.id
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED);
          IF cardinality(v_ids)=0 THEN
            RETURN;
          END IF;
          v_lease=pg_catalog.gen_random_uuid();
          UPDATE notifications.recipient_auto_replies SET status='PROCESSING',lease_token=v_lease,
            lease_expires_at=v_now+p_lease
            WHERE id=ANY(v_ids) AND status='PENDING';
          RETURN QUERY
            SELECT r.id,r.kind,r.sealed_number,r.number_key_version,r.received_at,r.lease_token
            FROM notifications.recipient_auto_replies r
            WHERE r.id=ANY(v_ids) AND r.status='PROCESSING' AND r.lease_token=v_lease
            ORDER BY r.received_at,r.id;
        END
        $fn$;

        """;

    /// <summary>
    /// <c>security.settle_recipient_auto_reply</c>: under the live lease, SENT or FAILED after the one provider call and
    /// DROPPED without it erase the sealed number; RETRY (a transient failure before the call) puts the request back with
    /// its number until <c>p_available_at</c>.
    /// </summary>
    public static string SettleSql { get; } =
        """
        CREATE FUNCTION security.settle_recipient_auto_reply(
          p_request_id uuid,p_lease_token uuid,p_outcome text,p_code text,p_available_at timestamptz)
        RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,notifications,pg_temp AS $fn$
        DECLARE v_rows integer; v_now timestamptz;
        BEGIN
          IF p_request_id IS NULL OR p_lease_token IS NULL OR p_outcome IS NULL
             OR p_outcome NOT IN ('SENT','FAILED','DROPPED','RETRY')
             OR (p_outcome='RETRY')<>(p_available_at IS NOT NULL)
             OR (p_outcome='RETRY' AND p_code IS NOT NULL)
             OR (p_outcome='SENT' AND (p_code IS NULL OR p_code NOT IN (
        """ + SqlList(AutoReplySentCodes, "               ") + """
        )))
             OR (p_outcome='FAILED' AND (p_code IS NULL OR p_code NOT IN (
        """ + SqlList(AutoReplyFailureCodes, "               ") + """
        )))
             OR (p_outcome='DROPPED' AND (p_code IS NULL OR p_code NOT IN (
        """ + SqlList(AutoReplyDropCodes, "               ") + """
        ))) THEN
            RAISE EXCEPTION 'invalid automatic reply outcome' USING ERRCODE='22023';
          END IF;
          v_now=clock_timestamp();
          IF p_outcome='RETRY' THEN
            UPDATE notifications.recipient_auto_replies SET status='PENDING',available_at=p_available_at,
              lease_token=NULL,lease_expires_at=NULL
              WHERE id=p_request_id AND status='PROCESSING' AND lease_token=p_lease_token AND lease_expires_at>v_now;
          ELSE
            -- AI-24 recipient_notices.auto_reply.path: the sealed number is erased right after the attempt, whatever its
            -- outcome; a request dropped unsent counts no attempt.
            UPDATE notifications.recipient_auto_replies SET status=p_outcome,last_code=p_code,
              attempts=CASE WHEN p_outcome='DROPPED' THEN 0 ELSE 1 END,
              sealed_number=NULL,number_key_version=NULL,lease_token=NULL,lease_expires_at=NULL,settled_at=v_now
              WHERE id=p_request_id AND status='PROCESSING' AND lease_token=p_lease_token AND lease_expires_at>v_now;
          END IF;
          GET DIAGNOSTICS v_rows=ROW_COUNT;
          RETURN v_rows=1;
        END
        $fn$;

        """;

    private static string FunctionGrantsSql { get; } = string.Concat(
        string.Concat(ApiFunctions.Concat(WorkerFunctions)
            .Select(static signature => $"REVOKE ALL ON FUNCTION {ShortSignature(signature)} FROM PUBLIC;\n")),
        $"GRANT EXECUTE ON FUNCTION {string.Join(",\n  ", ApiFunctions.Select(ShortSignature))}\n  TO paqueteria_app;\n",
        $"GRANT EXECUTE ON FUNCTION {string.Join(",\n  ", WorkerFunctions.Select(ShortSignature))}\n  TO paqueteria_worker;\n");

    private static string SqlTextArray(IEnumerable<string> values) =>
        "ARRAY[" + string.Join(",", values.Select(static value => "'" + value + "'")) + "]::text[]";

    private static string VerifySql(string label, bool installed) =>
        $$"""
        DO $ntf006_verify$
        DECLARE
          executor oid := to_regrole('{{Role}}');
          v_installed boolean := {{(installed ? "true" : "false")}};
        BEGIN
          IF executor IS NULL
             OR (to_regclass('notifications.recipient_auto_replies') IS NOT NULL) IS DISTINCT FROM v_installed
             OR (SELECT count(*) FROM unnest({{SqlTextArray(ApiFunctions.Concat(WorkerFunctions))}}) s
                 WHERE to_regprocedure(s) IS NOT NULL) IS DISTINCT FROM (CASE WHEN v_installed THEN 5 ELSE 0 END)::bigint
             -- NOLOGIN BYPASSRLS, inherits nothing and is reachable from no runtime role.
             OR EXISTS (SELECT 1 FROM pg_roles r WHERE r.oid=executor
                        AND (r.rolcanlogin OR r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication
                          OR NOT r.rolbypassrls OR NOT r.rolinherit))
             OR EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member=executor)
             OR EXISTS (SELECT 1 FROM pg_auth_members m JOIN pg_roles member ON member.oid=m.member
                        WHERE m.roleid=executor
                          AND member.rolname IN ('paqueteria_app','paqueteria_worker','paqueteria_realtime','paqueteria_bootstrap'))
             -- Each function: its owner, SECURITY DEFINER, a pinned search_path, no dynamic SQL, no RETURNING and
             -- EXECUTE only for its owner and its one runtime role.
             OR EXISTS (
               SELECT 1 FROM pg_proc p
               WHERE p.oid IN (SELECT to_regprocedure(s) FROM unnest({{SqlTextArray(ApiFunctions.Concat(WorkerFunctions))}}) s)
                 AND (p.proowner<>executor
                   OR NOT p.prosecdef
                   OR p.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
                   OR p.prosrc ~* '(^|[^a-z_])RETURNING([^a-z_]|$)'
                   OR p.proconfig IS NULL OR p.proconfig[1] NOT LIKE 'search_path=pg_catalog, %'
                   OR p.proconfig[1] NOT LIKE '%, pg_temp'
                   OR (SELECT string_agg(pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type || ':' || acl.is_grantable::text,
                         ',' ORDER BY pg_get_userbyid(acl.grantee) COLLATE "C")
                       FROM aclexplode(p.proacl) acl)
                      IS DISTINCT FROM (CASE
                        WHEN p.oid IN (SELECT to_regprocedure(s) FROM unnest({{SqlTextArray(ApiFunctions)}}) s)
                        THEN 'paqueteria_app:EXECUTE:false,{{Role}}:EXECUTE:false'
                        ELSE '{{Role}}:EXECUTE:false,paqueteria_worker:EXECUTE:false' END)))
             -- It owns nothing else: no relation, schema or type, and no other function.
             OR EXISTS (SELECT 1 FROM pg_class WHERE relowner=executor)
             OR EXISTS (SELECT 1 FROM pg_namespace WHERE nspowner=executor)
             OR EXISTS (SELECT 1 FROM pg_type WHERE typowner=executor)
             OR EXISTS (SELECT 1 FROM pg_proc p WHERE p.proowner=executor
                        AND p.oid NOT IN (SELECT to_regprocedure(s) FROM unnest({{SqlTextArray(ApiFunctions.Concat(WorkerFunctions))}}) s
                                          WHERE to_regprocedure(s) IS NOT NULL))
             -- Exactly its column privileges, none grantable, and no table-wide privilege.
             OR EXISTS (SELECT 1 FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) acl
                        WHERE c.relacl IS NOT NULL AND acl.grantee=executor)
             OR (SELECT array_agg(n.nspname || '.' || c.relname || '.' || a.attname || ':' || acl.privilege_type
                   || CASE WHEN acl.is_grantable THEN ':grantable' ELSE '' END
                   ORDER BY (n.nspname || '.' || c.relname || '.' || a.attname || ':' || acl.privilege_type) COLLATE "C")
                 FROM pg_attribute a
                 JOIN pg_class c ON c.oid=a.attrelid
                 JOIN pg_namespace n ON n.oid=c.relnamespace
                 CROSS JOIN LATERAL aclexplode(a.attacl) acl
                 WHERE a.attacl IS NOT NULL AND acl.grantee=executor)
               IS DISTINCT FROM (CASE WHEN v_installed THEN {{SqlTextArray(ColumnGrants)}} END)
             -- USAGE on notifications and security only, CREATE nowhere.
             OR EXISTS (SELECT 1 FROM pg_namespace n
                        WHERE n.nspname IN ('identity','organizations','clients','locations','pricing','orders','dispatch',
                            'drivers','routes','custody','incidents','finance','allies','notifications','reporting',
                            'platform','security','extensions')
                          AND has_schema_privilege(executor,n.oid,'USAGE') IS DISTINCT FROM
                            (v_installed AND n.nspname IN ('notifications','security')))
             OR EXISTS (SELECT 1 FROM pg_namespace n
                        WHERE n.nspname IN ('identity','organizations','clients','locations','pricing','orders','dispatch',
                            'drivers','routes','custody','incidents','finance','allies','notifications','reporting',
                            'platform','security','extensions','public')
                          AND has_schema_privilege(executor,n.oid,'CREATE'))
             -- Never either outbox table directly; the status-changed row goes through the existing writer only (O5).
             OR has_table_privilege(executor,'platform.outbox_events','SELECT,INSERT,UPDATE,DELETE')
             OR has_table_privilege(executor,'platform.location_outbox_events','SELECT,INSERT,UPDATE,DELETE')
             OR (SELECT array_agg(p.oid::regprocedure::text ORDER BY p.oid::regprocedure::text COLLATE "C")
                 FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) acl
                 WHERE p.proacl IS NOT NULL AND acl.grantee=executor AND p.proowner<>executor)
               IS DISTINCT FROM (CASE WHEN v_installed
                 THEN ARRAY['security.emit_notification_status_changed(uuid,uuid,integer,text,text,integer,timestamp with time zone)']::text[] END)
             OR EXISTS (SELECT 1 FROM pg_default_acl d CROSS JOIN LATERAL aclexplode(d.defaclacl) acl
                        WHERE acl.grantee=executor)
             -- The store: row security forced with no policy, no privilege for any runtime role.
             OR (v_installed AND (
                  NOT (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class
                       WHERE oid=to_regclass('notifications.recipient_auto_replies'))
                  OR EXISTS (SELECT 1 FROM pg_policy WHERE polrelid=to_regclass('notifications.recipient_auto_replies'))
                  -- No table-level privilege for anyone but the owner (every executor grant is per column) ...
                  OR EXISTS (SELECT 1 FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) acl
                             WHERE c.oid=to_regclass('notifications.recipient_auto_replies') AND acl.grantee<>c.relowner)
                  -- ... and none reaches a runtime role through a membership.
                  OR EXISTS (SELECT 1 FROM unnest(ARRAY['paqueteria_app','paqueteria_worker','paqueteria_realtime','paqueteria_bootstrap']) role(name)
                             WHERE to_regrole(role.name) IS NOT NULL
                               AND has_table_privilege(role.name,'notifications.recipient_auto_replies',
                                                       'SELECT,INSERT,UPDATE,DELETE,REFERENCES,TRIGGER'))))
             OR has_schema_privilege('paqueteria_outbox_executor','security','CREATE')
          THEN
            RAISE EXCEPTION 'NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 {{label}}: the Notifications lane differs from the AI-06 and AI-18 contract';
          END IF;
        END
        $ntf006_verify$;
        """;

    public static string UpSql { get; } = string.Concat(
        """
        -- NTF-001 rollback blocked: NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-08 NTF-006) adds the delivery receipts,
        -- the recipient opt-out writer and the automatic-reply store; nothing published changes.
        RESET ROLE;

        DO $ntf006_adoption$
        BEGIN
          IF to_regprocedure('security.read_recipient_notice_counts(uuid,uuid)') IS NULL
             OR to_regclass('notifications.recipient_opt_outs') IS NULL
             OR to_regclass('notifications.recipient_auto_replies') IS NOT NULL THEN
            RAISE EXCEPTION 'NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 requires the NTF-005 recipient notice count step';
          END IF;
        END
        $ntf006_adoption$;

        -- AI-18 creates the executor on fresh installations; an installation that predates it gets it here.
        DO $ntf006_role$
        BEGIN
          CREATE ROLE paqueteria_notification_receipt_executor NOLOGIN BYPASSRLS;
        EXCEPTION WHEN duplicate_object THEN NULL;
        END
        $ntf006_role$;

        REVOKE paqueteria_notification_receipt_executor FROM paqueteria_app, paqueteria_worker, paqueteria_realtime;

        SET ROLE paqueteria_migrator;

        -- AI-06 (NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25): the automatic-reply store.

        """,
        TableDdl,
        "\n",
        string.Join("\n", TableStatements),
        "\n\n",
        """
        -- AI-18 recipient notices step 3 (amended by O5): the executor's exact privileges.

        """,
        string.Join("\n", GrantStatements),
        "\n",
        """
        GRANT CREATE ON SCHEMA security TO paqueteria_notification_receipt_executor;

        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;

        """,
        EmitGrantStatement,
        "\n",
        """

        RESET ROLE;
        SET ROLE paqueteria_notification_receipt_executor;


        """,
        ReceiptSql,
        OptOutSql,
        RequestSql,
        ClaimSql,
        SettleSql,
        FunctionGrantsSql,
        """

        RESET ROLE;
        SET ROLE paqueteria_migrator;

        REVOKE CREATE ON SCHEMA security FROM paqueteria_notification_receipt_executor;

        """,
        VerifySql("up", installed: true));

    /// <summary>
    /// Refuses while any automatic-reply request exists; otherwise drops the five functions and the store and revokes
    /// every privilege of the executor. The executor stays, inert.
    /// </summary>
    public static string DownSql { get; } = string.Concat(
        """
        -- NTF-001 rollback blocked: only the NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 lane step is withdrawn, and only while
        -- no automatic-reply request exists.
        RESET ROLE;
        SET ROLE paqueteria_notification_receipt_executor;

        -- The store forces row security with no policy, so only a BYPASSRLS role sees its rows: the check runs as the
        -- executor (the migrator, its owner, would see none and drop live requests).
        DO $ntf006_rollback$
        BEGIN
          IF EXISTS (SELECT 1 FROM notifications.recipient_auto_replies r WHERE r.id IS NOT NULL) THEN
            RAISE EXCEPTION 'NTF006_DOWNGRADE_BLOCKED_DATA_PRESENT' USING
              DETAIL = 'Automatic-reply requests exist; the lane step cannot be withdrawn.',
              ERRCODE = 'P0001';
          END IF;
        END
        $ntf006_rollback$;

        """,
        string.Concat(WorkerFunctions.Reverse().Concat(ApiFunctions.Reverse())
            .Select(static signature => $"DROP FUNCTION {ShortSignature(signature)};\n")),
        """

        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;

        REVOKE EXECUTE ON FUNCTION security.emit_notification_status_changed(uuid,uuid,integer,text,text,integer,timestamptz) FROM paqueteria_notification_receipt_executor;

        RESET ROLE;
        SET ROLE paqueteria_migrator;

        """,
        string.Join("\n", GrantStatements.Reverse()
            .Select(static statement => "REVOKE " + statement["GRANT ".Length..].Replace(" TO ", " FROM ", StringComparison.Ordinal))),
        "\n",
        """
        DROP TABLE notifications.recipient_auto_replies;

        """,
        VerifySql("down", installed: false));

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);

    /// <summary>A regprocedure signature in the short form a GRANT or DROP statement takes.</summary>
    private static string ShortSignature(string signature) =>
        signature.Replace("timestamp with time zone", "timestamptz", StringComparison.Ordinal);

    /// <summary>One quoted, comma-separated SQL literal per value, each on its own line with the given indentation.</summary>
    private static string SqlList(IEnumerable<string> values, string indent) =>
        "\n" + string.Join(",\n", values.Select(value => indent + "'" + value + "'"));
}
