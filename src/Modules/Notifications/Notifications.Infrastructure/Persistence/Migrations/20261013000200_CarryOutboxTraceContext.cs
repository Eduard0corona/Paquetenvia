using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>
/// OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13 (project owner, Q-PR7: "Columna nueva (Recommended)"). The two
/// Notifications functions that write outbox rows inside the database store the trace context of the event the rows
/// derive from, so the realtime and Notifications consumers of those rows work in the trace of the request that created
/// the order:
/// <list type="bullet">
/// <item><c>security.expand_order_created_notifications</c>: each <c>notifications.send-requested</c> row it inserts
/// carries the <c>trace_parent</c> of the <c>orders.created</c> row it expands (the row it holds under its lease).</item>
/// <item><c>security.emit_notification_status_changed</c>: each <c>notifications.status-changed</c> row carries the
/// <c>trace_parent</c> of the outbox event that created the notification (<c>notifications.notifications.source_event_id</c>),
/// read in the same transaction; none once that event has been purged. Every caller (expand, apply, finalize and the
/// stale-lease recovery) therefore writes the originating trace, also when it runs in a dispatch cycle that sees no
/// per-message activity.</item>
/// </list>
/// Only the two bodies change, derived from the published NTF-001 ones by the reviewed edits in
/// <see cref="EmitEdits"/> and <see cref="ExpandEdits"/>: same signatures, owner (<c>paqueteria_outbox_executor</c>),
/// <c>SECURITY DEFINER</c>, <c>search_path</c> and grants. The payloads are unchanged: the trace context never enters
/// an event's content, a SignalR message or a notification. The column is declared by AI-06 on fresh installations and
/// added elsewhere by the Dispatch lane step <c>20261013000100_AddOutboxTraceContext</c>, which runs earlier; this step
/// requires it.
/// </summary>
/// <remarks>
/// NTF-001 rollback blocked: this step replaces two function bodies only. Its rollback restores the published NTF-001
/// bodies; rows already written keep their trace context until the Dispatch lane step is rolled back.
/// </remarks>
[DbContext(typeof(NotificationsDbContext))]
[Migration(MigrationId)]
public sealed class CarryOutboxTraceContext : Migration
{
    public const string MigrationId = "20261013000200_CarryOutboxTraceContext";
    public const string DecisionId = "OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13";

    public const string EmitSignature =
        "security.emit_notification_status_changed(uuid,uuid,integer,text,text,integer,timestamp with time zone)";

    public const string ExpandSignature =
        "security.expand_order_created_notifications(uuid,uuid,uuid,text,text,timestamp with time zone,uuid[])";

    /// <summary>The published NTF-001 status-change emitter, as CREATE OR REPLACE.</summary>
    public static string PublishedEmitSql { get; } = PublishedFunction("security.emit_notification_status_changed(");

    /// <summary>The published NTF-001 expansion of <c>orders.created</c>, as CREATE OR REPLACE.</summary>
    public static string PublishedExpandSql { get; } = PublishedFunction("security.expand_order_created_notifications(");

    /// <summary>The only differences from the published emitter, as (published text, new text).</summary>
    public static IReadOnlyList<(string Published, string Replacement)> EmitEdits { get; } = Array.AsReadOnly(new[]
    {
        (
            "SET search_path=pg_catalog,platform,security,pg_temp AS $fn$\n" +
            "BEGIN\n",
            "SET search_path=pg_catalog,platform,security,pg_temp AS $fn$\n" +
            "DECLARE v_trace_parent text;\n" +
            "BEGIN\n" +
            "  -- OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13: the trace context of the outbox event that created the\n" +
            "  -- notification (the originating request's trace), read in this transaction; none once it was purged.\n" +
            "  SELECT o.trace_parent INTO v_trace_parent\n" +
            "  FROM notifications.notifications n\n" +
            "  JOIN platform.outbox_events o ON o.id=n.source_event_id AND o.owner_org_id=n.owner_org_id\n" +
            "  WHERE n.id=p_notification_id AND n.owner_org_id=p_owner_org_id;\n"
        ),
        (
            "    payload,priority,status,attempts,available_at,created_at)\n",
            "    payload,priority,status,attempts,available_at,created_at,trace_parent)\n"
        ),
        (
            "    50,'PENDING',0,p_occurred_at,p_occurred_at);\n",
            "    50,'PENDING',0,p_occurred_at,p_occurred_at,v_trace_parent);\n"
        ),
    });

    /// <summary>The only differences from the published expansion, as (published text, new text).</summary>
    public static IReadOnlyList<(string Published, string Replacement)> ExpandEdits { get; } = Array.AsReadOnly(new[]
    {
        (
            "        payload,priority,status,attempts,available_at,created_at)\n",
            "        payload,priority,status,attempts,available_at,created_at,trace_parent)\n"
        ),
        (
            "        50,'PENDING',0,p_occurred_at,p_occurred_at);\n",
            "        -- OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13: the trace context of the orders.created row it expands.\n" +
            "        50,'PENDING',0,p_occurred_at,p_occurred_at,v_source.trace_parent);\n"
        ),
    });

    /// <summary>The emitter this step installs.</summary>
    public static string EmitSql { get; } = EmitEdits.Aggregate(
        PublishedEmitSql, (sql, edit) => ReplaceOnce(sql, edit.Published, edit.Replacement));

    /// <summary>The expansion this step installs.</summary>
    public static string ExpandSql { get; } = ExpandEdits.Aggregate(
        PublishedExpandSql, (sql, edit) => ReplaceOnce(sql, edit.Published, edit.Replacement));

    private static string VerifySql(string label, bool traceBodies) =>
        $$"""
        DO $obs003_trace_verify$
        DECLARE
          executor oid := 'paqueteria_outbox_executor'::regrole;
          emit oid := to_regprocedure('{{EmitSignature}}');
          expand oid := to_regprocedure('{{ExpandSignature}}');
        BEGIN
          IF emit IS NULL OR expand IS NULL
             OR EXISTS (
               SELECT 1 FROM pg_proc p
               WHERE p.oid IN (emit, expand)
                 AND (p.proowner <> executor
                   OR NOT p.prosecdef
                   OR p.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
                   OR (position('trace_parent' IN p.prosrc) > 0) IS DISTINCT FROM {{(traceBodies ? "true" : "false")}}))
             OR (SELECT proconfig FROM pg_proc WHERE oid=emit)
                IS DISTINCT FROM ARRAY['search_path=pg_catalog, platform, security, pg_temp']
             OR (SELECT proconfig FROM pg_proc WHERE oid=expand)
                IS DISTINCT FROM ARRAY['search_path=pg_catalog, platform, notifications, security, pg_temp']
             OR (SELECT string_agg(pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type || ':' || acl.is_grantable::text,
                   ',' ORDER BY pg_get_userbyid(acl.grantee) COLLATE "C")
                 FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) acl WHERE p.oid=emit)
                IS DISTINCT FROM 'paqueteria_outbox_executor:EXECUTE:false'
             OR (SELECT string_agg(pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type || ':' || acl.is_grantable::text,
                   ',' ORDER BY pg_get_userbyid(acl.grantee) COLLATE "C")
                 FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) acl WHERE p.oid=expand)
                IS DISTINCT FROM 'paqueteria_outbox_executor:EXECUTE:false,paqueteria_worker:EXECUTE:false'
          THEN
            RAISE EXCEPTION 'OBS-003-OUTBOX-TRACE-CONTEXT {{label}}: the Notifications outbox writers differ from the contract';
          END IF;
        END
        $obs003_trace_verify$;
        """;

    public static string UpSql { get; } = string.Concat(
        """
        -- NTF-001 rollback blocked: only the emit_notification_status_changed and expand_order_created_notifications
        -- bodies change; signatures, owner and grants are kept.
        GRANT CREATE ON SCHEMA security TO paqueteria_outbox_executor;

        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;

        DO $obs003_trace_adoption$
        BEGIN
          IF (SELECT count(*) FROM information_schema.columns
              WHERE table_schema='platform' AND table_name='outbox_events' AND column_name='trace_parent'
                AND data_type='text' AND is_nullable='YES') <> 1
             OR NOT EXISTS (
               SELECT 1 FROM pg_constraint
               WHERE conrelid='platform.outbox_events'::regclass AND conname='outbox_events_trace_parent_check') THEN
            RAISE EXCEPTION 'OBS-003-OUTBOX-TRACE-CONTEXT requires platform.outbox_events.trace_parent and its check (AI-06, or the Dispatch lane step 20261013000100_AddOutboxTraceContext)';
          END IF;
        END
        $obs003_trace_adoption$;


        """,
        EmitSql,
        "\n",
        ExpandSql,
        """

        RESET ROLE;
        SET ROLE paqueteria_migrator;

        REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;

        """,
        VerifySql("up", traceBodies: true));

    /// <summary>Restores the published NTF-001 bodies; signatures, owner and grants are kept.</summary>
    public static string DownSql { get; } = string.Concat(
        """
        -- NTF-001 rollback blocked: restore the published NTF-001 emit_notification_status_changed and
        -- expand_order_created_notifications bodies.
        GRANT CREATE ON SCHEMA security TO paqueteria_outbox_executor;

        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;


        """,
        PublishedEmitSql,
        "\n",
        PublishedExpandSql,
        """

        RESET ROLE;
        SET ROLE paqueteria_migrator;

        REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;

        """,
        VerifySql("down", traceBodies: false));

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);

    /// <summary>
    /// One function of the published NTF-001 step (<see cref="AddTenantSafeOutboxNotifications.UpSql"/>), byte for byte,
    /// with its <c>CREATE FUNCTION</c> turned into <c>CREATE OR REPLACE FUNCTION</c>.
    /// </summary>
    private static string PublishedFunction(string name)
    {
        var sql = AddTenantSafeOutboxNotifications.UpSql;
        var start = "CREATE FUNCTION " + name;
        var from = sql.IndexOf(start, StringComparison.Ordinal);
        if (from < 0 || sql.IndexOf(start, from + start.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException($"The published NTF-001 SQL must define {name.TrimEnd('(')} exactly once.");
        }

        var to = sql.IndexOf("$fn$;\n", from, StringComparison.Ordinal);
        if (to < 0)
        {
            throw new InvalidOperationException($"The published NTF-001 definition of {name.TrimEnd('(')} has no end.");
        }

        return "CREATE OR REPLACE FUNCTION " + sql[(from + "CREATE FUNCTION ".Length)..(to + "$fn$;\n".Length)];
    }

    private static string ReplaceOnce(string sql, string published, string replacement)
    {
        var index = sql.IndexOf(published, StringComparison.Ordinal);
        if (index < 0 || sql.IndexOf(published, index + published.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                $"The published NTF-001 function must contain '{published.Trim()}' exactly once.");
        }

        return string.Concat(sql.AsSpan(0, index), replacement, sql.AsSpan(index + published.Length));
    }
}
