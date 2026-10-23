using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>
/// NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 (project owner, L1 "Función de conteo (Recommended)" and L2 "Toda la plataforma
/// (Recommended)"; AI-08 NTF-005; AI-24 <c>recipient_notices.caps</c>; AI-06 and AI-18 declared contract). The Worker
/// holds no privilege on <c>notifications.notifications</c>, so it cannot count the recipient notices the two caps
/// limit. This Notifications lane step adds one read-only function and the two partial indexes it reads:
/// <list type="bullet">
/// <item><c>security.read_recipient_notice_counts(uuid,uuid)</c>: under the live <c>lease_token</c> of a
/// <c>notifications.send-requested</c> row whose recipient notice is PENDING, one row of two integers - the other
/// notices of the same owner organization, order and channel with attempts &gt; 0, and the other notices of that
/// channel on the whole platform with attempts &gt; 0 whose last attempt falls on the current calendar day in
/// America/Mazatlan (computed here with AT TIME ZONE); otherwise no row. It returns no identifier, recipient, code or
/// time. Owned by <c>paqueteria_outbox_executor</c>, SECURITY DEFINER with a pinned search_path, no dynamic SQL and
/// no RETURNING, EXECUTE revoked from PUBLIC and held only by <c>paqueteria_worker</c>.</item>
/// <item><c>notifications_recipient_order_channel_idx</c> and <c>notifications_recipient_attempt_day_idx</c>, partial
/// indexes on recipient notices only.</item>
/// </list>
/// No table, column, constraint, row, role or table privilege changes; the runtime roles still reach the Notifications
/// tables only through SECURITY DEFINER functions.
/// </summary>
/// <remarks>
/// NTF-001 rollback blocked: the rollback drops only the function and the two indexes; no row depends on them.
/// </remarks>
[DbContext(typeof(NotificationsDbContext))]
[Migration(MigrationId)]
public sealed class AddRecipientNoticeCounts : Migration
{
    public const string MigrationId = "20261023000100_AddRecipientNoticeCounts";
    public const string DecisionId = "NTF-RECIPIENT-NOTICE-CAPS-2026-10-23";

    public const string CountsSignature = "security.read_recipient_notice_counts(uuid,uuid)";

    /// <summary>The per-order index, exactly as AI-06 declares it.</summary>
    public const string OrderChannelIndexDdl =
        "CREATE INDEX notifications_recipient_order_channel_idx ON notifications.notifications(owner_org_id,order_id,channel) WHERE recipient_kind='ORDER_DESTINATION';";

    /// <summary>The per-day index, exactly as AI-06 declares it.</summary>
    public const string AttemptDayIndexDdl =
        "CREATE INDEX notifications_recipient_attempt_day_idx ON notifications.notifications(channel,last_attempt_at) WHERE recipient_kind='ORDER_DESTINATION' AND attempts>0;";

    /// <summary>
    /// <c>security.read_recipient_notice_counts</c> (AI-24 <c>recipient_notices.caps</c>, AI-18 recipient notices step
    /// 6): the two counts of the caps, for the send request whose live lease the caller holds.
    /// </summary>
    public const string CountsSql =
        """
        CREATE FUNCTION security.read_recipient_notice_counts(p_outbox_id uuid,p_lease_token uuid)
        RETURNS TABLE(order_channel_count integer,day_channel_count integer)
        LANGUAGE plpgsql SECURITY DEFINER
        SET search_path=pg_catalog,platform,notifications,pg_temp AS $fn$
        DECLARE v_request platform.outbox_events%ROWTYPE; v_notification notifications.notifications%ROWTYPE;
          v_day date; v_day_start timestamptz; v_day_end timestamptz;
        BEGIN
          -- NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 (L1, L2): only the holder of the live lease of a send request learns the
          -- two numbers of its notice, and nothing else.
          SELECT * INTO v_request FROM platform.outbox_events
          WHERE id=p_outbox_id AND status='PROCESSING' AND lease_token=p_lease_token
            AND lease_expires_at>clock_timestamp() AND topic='notifications.send-requested';
          IF NOT FOUND THEN RETURN; END IF;
          SELECT * INTO v_notification FROM notifications.notifications
          WHERE id=v_request.aggregate_id AND owner_org_id=v_request.owner_org_id
            AND recipient_kind='ORDER_DESTINATION' AND status='PENDING';
          IF NOT FOUND THEN RETURN; END IF;
          -- The calendar day of America/Mazatlan, so the container needs no time-zone data.
          v_day=(clock_timestamp() AT TIME ZONE 'America/Mazatlan')::date;
          v_day_start=v_day::timestamp AT TIME ZONE 'America/Mazatlan';
          v_day_end=(v_day+1)::timestamp AT TIME ZONE 'America/Mazatlan';
          RETURN QUERY SELECT
            -- L2, L5: the per-order cap counts the other notices of the same order and channel with attempts>0.
            (SELECT count(*)::integer FROM notifications.notifications n
             WHERE n.owner_org_id=v_notification.owner_org_id AND n.order_id=v_notification.order_id
               AND n.channel=v_notification.channel AND n.recipient_kind='ORDER_DESTINATION'
               AND n.attempts>0 AND n.id<>v_notification.id),
            -- L2 "Toda la plataforma": every organization, one channel, on the Mazatlan day of each notice's last attempt.
            (SELECT count(*)::integer FROM notifications.notifications n
             WHERE n.channel=v_notification.channel AND n.recipient_kind='ORDER_DESTINATION'
               AND n.attempts>0 AND n.id<>v_notification.id
               AND n.last_attempt_at>=v_day_start AND n.last_attempt_at<v_day_end);
        END
        $fn$;

        """;

    private static string VerifySql(string label, bool installed) =>
        $$"""
        DO $ntf005_verify$
        DECLARE
          v_installed boolean := {{(installed ? "true" : "false")}};
        BEGIN
          IF (to_regprocedure('{{CountsSignature}}') IS NOT NULL) IS DISTINCT FROM v_installed
             OR (to_regclass('notifications.notifications_recipient_order_channel_idx') IS NOT NULL) IS DISTINCT FROM v_installed
             OR (to_regclass('notifications.notifications_recipient_attempt_day_idx') IS NOT NULL) IS DISTINCT FROM v_installed
             OR EXISTS (
               SELECT 1 FROM pg_proc p
               WHERE p.oid=to_regprocedure('{{CountsSignature}}')
                 AND (p.proowner <> 'paqueteria_outbox_executor'::regrole
                   OR NOT p.prosecdef
                   OR p.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
                   OR p.prosrc ~* '(^|[^a-z_])RETURNING([^a-z_]|$)'
                   OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog, platform, notifications, pg_temp']
                   OR (SELECT string_agg(pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type || ':' || acl.is_grantable::text,
                         ',' ORDER BY pg_get_userbyid(acl.grantee) COLLATE "C")
                       FROM aclexplode(p.proacl) acl)
                      IS DISTINCT FROM 'paqueteria_outbox_executor:EXECUTE:false,paqueteria_worker:EXECUTE:false'))
             OR EXISTS (
               SELECT 1 FROM (VALUES
                 ('notifications.notifications_recipient_order_channel_idx',
                  'CREATE INDEX notifications_recipient_order_channel_idx ON notifications.notifications USING btree (owner_org_id, order_id, channel) WHERE (recipient_kind = ''ORDER_DESTINATION''::text)'),
                 ('notifications.notifications_recipient_attempt_day_idx',
                  'CREATE INDEX notifications_recipient_attempt_day_idx ON notifications.notifications USING btree (channel, last_attempt_at) WHERE ((recipient_kind = ''ORDER_DESTINATION''::text) AND (attempts > 0))'))
                 expected(name,definition)
               WHERE to_regclass(expected.name) IS NOT NULL
                 AND pg_get_indexdef(to_regclass(expected.name)) IS DISTINCT FROM expected.definition)
             OR has_schema_privilege('paqueteria_outbox_executor','security','CREATE')
             OR has_table_privilege('paqueteria_worker','notifications.notifications','SELECT')
          THEN
            RAISE EXCEPTION 'NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 {{label}}: the Notifications lane differs from the AI-06 and AI-18 contract';
          END IF;
        END
        $ntf005_verify$;
        """;

    public static string UpSql { get; } = string.Concat(
        """
        -- NTF-001 rollback blocked: NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 (AI-08 NTF-005) adds the read-only count of the
        -- recipient notice caps and its two partial indexes; nothing published changes.
        DO $ntf005_adoption$
        BEGIN
          IF to_regprocedure('security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,text,timestamp with time zone,timestamp with time zone)') IS NULL
             OR to_regprocedure('security.read_recipient_notice_counts(uuid,uuid)') IS NOT NULL THEN
            RAISE EXCEPTION 'NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 requires the NTF-003 recipient notice lane step';
          END IF;
        END
        $ntf005_adoption$;

        -- AI-06 (NTF-RECIPIENT-NOTICE-CAPS-2026-10-23): the partial indexes of the two counts.

        """,
        OrderChannelIndexDdl,
        "\n",
        AttemptDayIndexDdl,
        "\n",
        """

        GRANT CREATE ON SCHEMA security TO paqueteria_outbox_executor;

        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;


        """,
        CountsSql,
        """
        REVOKE ALL ON FUNCTION security.read_recipient_notice_counts(uuid,uuid) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION security.read_recipient_notice_counts(uuid,uuid) TO paqueteria_worker;

        RESET ROLE;
        SET ROLE paqueteria_migrator;

        REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;

        """,
        VerifySql("up", installed: true));

    /// <summary>Drops the function and the two indexes; no row depends on them, so nothing is refused.</summary>
    public static string DownSql { get; } = string.Concat(
        """
        -- NTF-001 rollback blocked: only the NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 lane step is withdrawn.
        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;

        DROP FUNCTION security.read_recipient_notice_counts(uuid,uuid);

        RESET ROLE;
        SET ROLE paqueteria_migrator;

        DROP INDEX notifications.notifications_recipient_attempt_day_idx;
        DROP INDEX notifications.notifications_recipient_order_channel_idx;

        """,
        VerifySql("down", installed: false));

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
