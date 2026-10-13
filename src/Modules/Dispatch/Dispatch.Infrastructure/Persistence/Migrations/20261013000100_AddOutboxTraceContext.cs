using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dispatch.Infrastructure.Persistence.Migrations;

/// <summary>
/// OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13 (project owner, Q-PR7: "Columna nueva (Recommended)"). The W3C context of
/// the trace that wrote an outbox row travels with the row, so the consumer that processes it later works in the
/// trace of the originating request:
/// <list type="bullet">
/// <item><c>platform.outbox_events.trace_parent</c> and <c>platform.location_outbox_events.trace_parent</c>,
/// <c>text</c>, nullable, with a CHECK on the exact shape
/// <c>00-{32 hex trace id}-{16 hex span id}-{00|01}</c> (lowercase, ids not all zero). No row is rewritten: rows written
/// before this step, and rows written outside any trace, keep <c>NULL</c>. Every claim function returns the full row
/// type of its table (<c>SETOF platform.outbox_events</c> or <c>SETOF platform.location_outbox_events</c> with
/// <c>RETURNING o.*</c>), so each one returns the column with no change to its signature, owner, grants or body.</item>
/// <item><c>INSERT (trace_parent)</c> on <c>platform.outbox_events</c> for <c>paqueteria_operator_outbox_executor</c>,
/// and the body of <c>security.append_operator_order_outbox</c> derived from the published one by the two reviewed
/// edits in <see cref="FunctionEdits"/>: the row it inserts also stores the caller's trace context, read from the
/// transaction-local setting <c>app.trace_parent</c> exactly like it reads <c>app.current_org_ids</c> and
/// <c>app.current_user_id</c>. Its signature, owner, ACL, <c>SECURITY DEFINER</c> and <c>search_path</c> are kept, so
/// an application version that predates this step keeps calling it during a deploy (the column then stays NULL).</item>
/// </list>
/// AI-06 declares both columns and checks on fresh installations; this step adopts them there (refusing any other shape)
/// and creates them on installations whose baseline predates the decision. The outbox tables belong to no module
/// (AI-03 §8); this Dispatch lane step carries them because it is the earliest step in the migrator's order that needs
/// the column (the operator outbox executor, which this lane owns, writes it), so every SQL writer installed later finds
/// it. The executor's new column grant is owned by this step, not by AI-18: the published Dispatch step
/// <see cref="AddOperatorOwnerOutboxExecutor"/>, which runs first on every installation, verifies exactly the AI-18 set.
/// The Notifications lane step that carries the context into the notification events runs later.
/// </summary>
/// <remarks>
/// Rollback refuses while the Notifications lane step <c>20261013000200_CarryOutboxTraceContext</c> is recorded (its
/// functions write the column). Otherwise it restores the published function body, revokes the column grant and drops
/// both CHECK constraints and both columns; every outbox row stays, without its trace context. Roll the application back
/// first: this version writes the column.
/// </remarks>
[DbContext(typeof(DispatchDbContext))]
[Migration(MigrationId)]
public sealed class AddOutboxTraceContext : Migration
{
    public const string MigrationId = "20261013000100_AddOutboxTraceContext";
    public const string DecisionId = "OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13";

    /// <summary>The Notifications lane step that writes the column and must be rolled back first.</summary>
    public const string NotificationsStepMigrationId = "20261013000200_CarryOutboxTraceContext";

    public const string DowngradeOrderRefused = "OBS003_TRACE_CONTEXT_DOWNGRADE_ORDER";

    public const string Column = "trace_parent";

    public const string OutboxConstraint = "outbox_events_trace_parent_check";

    public const string LocationOutboxConstraint = "location_outbox_events_trace_parent_check";

    /// <summary>The CHECK both tables declare: the exact W3C version 00 shape, with neither id all zero.</summary>
    public const string CheckExpression =
        "trace_parent ~ '^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$' " +
        "AND substr(trace_parent, 4, 32) <> repeat('0', 32) " +
        "AND substr(trace_parent, 37, 16) <> repeat('0', 16)";

    /// <summary>The CHECK exactly as <c>pg_get_constraintdef</c> renders it.</summary>
    public const string ConstraintDefinition =
        "CHECK (((trace_parent ~ '^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$'::text) " +
        "AND (substr(trace_parent, 4, 32) <> repeat('0'::text, 32)) " +
        "AND (substr(trace_parent, 37, 16) <> repeat('0'::text, 16))))";

    /// <summary>The executor column grant this lane step adds to the published DSP-OPERATOR-OWNER-OUTBOX set.</summary>
    public static IReadOnlyList<string> AddedExecutorColumnGrants { get; } = Array.AsReadOnly(new[]
    {
        "platform.outbox_events.trace_parent:INSERT",
    });

    /// <summary>The executor's exact column grants once this step is recorded, ordered as the catalog check sorts them.</summary>
    public static IReadOnlyList<string> ExecutorColumnGrants { get; } = Array.AsReadOnly(
        AddOperatorOwnerOutboxExecutor.ExecutorColumnGrants
            .Concat(AddedExecutorColumnGrants)
            .Order(StringComparer.Ordinal)
            .ToArray());

    /// <summary>The only differences from the published outbox definer, as (published text, new text).</summary>
    public static IReadOnlyList<(string Published, string Replacement)> FunctionEdits { get; } = Array.AsReadOnly(new[]
    {
        (
            "    last_error,created_at,processed_at)\n" +
            "  VALUES (\n",
            "    last_error,created_at,processed_at,trace_parent)\n" +
            "  VALUES (\n"
        ),
        (
            "    NULL,p_created_at,NULL);\n",
            "    NULL,p_created_at,NULL,\n" +
            "    -- OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13: the caller's producing W3C trace context, transaction-local like\n" +
            "    -- app.current_org_ids; empty or absent means none. The column CHECK holds it to the exact shape.\n" +
            "    NULLIF(pg_catalog.current_setting('app.trace_parent', true), ''));\n"
        ),
    });

    /// <summary>The outbox definer this step installs: the published one with <see cref="FunctionEdits"/> applied.</summary>
    public static string OutboxFunctionSql { get; } = FunctionEdits.Aggregate(
        AddOperatorOwnerOutboxExecutor.OutboxFunctionSql,
        (sql, edit) => ReplaceOnce(sql, edit.Published, edit.Replacement));

    private const string OutboxFunctionSignature = AddOperatorOwnerOutboxExecutor.OutboxFunctionSignature;

    private static string GrantArray(IEnumerable<string> grants) =>
        "ARRAY[" + string.Join(",", grants.Select(static grant => "'" + grant + "'")) + "]";

    /// <summary>
    /// Catalog checks shared by Up and Down: the definer keeps its owner, SECURITY DEFINER, search_path, ACL and safety
    /// rules, and the executor holds exactly <paramref name="grants"/> as column grants and no table-wide grant.
    /// </summary>
    private static string VerifySql(string label, IEnumerable<string> grants, bool traceBody) =>
        $$"""
        DO $obs003_trace_verify$
        DECLARE
          executor oid := 'paqueteria_operator_outbox_executor'::regrole;
          fn oid := to_regprocedure('{{OutboxFunctionSignature}}');
        BEGIN
          IF fn IS NULL
             OR (SELECT proowner FROM pg_proc WHERE oid=fn) <> executor
             OR NOT (SELECT prosecdef FROM pg_proc WHERE oid=fn)
             OR (SELECT proconfig FROM pg_proc WHERE oid=fn) IS DISTINCT FROM ARRAY['{{AddOperatorOwnerOutboxExecutor.SearchPath}}']
             OR (SELECT prosrc FROM pg_proc WHERE oid=fn) ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
             OR (SELECT prosrc FROM pg_proc WHERE oid=fn) ~* 'RETURNING'
             OR (position('app.trace_parent' IN (SELECT prosrc FROM pg_proc WHERE oid=fn)) > 0) IS DISTINCT FROM {{(traceBody ? "true" : "false")}}
             OR (SELECT string_agg(
                   CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(acl.grantee) END
                     || ':' || acl.privilege_type || ':' || acl.is_grantable::text, ','
                   ORDER BY (CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(acl.grantee) END) COLLATE "C")
                 FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) acl WHERE p.oid=fn)
                IS DISTINCT FROM 'paqueteria_app:EXECUTE:false,paqueteria_operator_outbox_executor:EXECUTE:false'
          THEN
            RAISE EXCEPTION 'OBS-003-OUTBOX-TRACE-CONTEXT {{label}}: the operator outbox definer differs from the contract';
          END IF;

          IF EXISTS (
               SELECT 1 FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) acl
               WHERE c.relacl IS NOT NULL AND acl.grantee=executor)
             OR (SELECT array_agg(n.nspname || '.' || c.relname || '.' || a.attname || ':' || acl.privilege_type
                   || CASE WHEN acl.is_grantable THEN ':grantable' ELSE '' END
                   ORDER BY (n.nspname || '.' || c.relname || '.' || a.attname || ':' || acl.privilege_type) COLLATE "C")
                 FROM pg_attribute a
                 JOIN pg_class c ON c.oid=a.attrelid
                 JOIN pg_namespace n ON n.oid=c.relnamespace
                 CROSS JOIN LATERAL aclexplode(a.attacl) acl
                 WHERE a.attacl IS NOT NULL AND acl.grantee=executor)
               IS DISTINCT FROM {{GrantArray(grants)}}
          THEN
            RAISE EXCEPTION 'OBS-003-OUTBOX-TRACE-CONTEXT {{label}}: paqueteria_operator_outbox_executor privileges differ from the contract';
          END IF;

          -- CREATE on schema security is asserted after the lane, once any E-002 temporary grant is revoked.
          IF (SELECT count(*) FROM pg_proc WHERE proowner=executor) <> 2 THEN
            RAISE EXCEPTION 'OBS-003-OUTBOX-TRACE-CONTEXT {{label}}: paqueteria_operator_outbox_executor ownership differs from the contract';
          END IF;
        END
        $obs003_trace_verify$;
        """;

    public static string UpSql { get; } = string.Concat(
        $$"""
        RESET ROLE;
        SET ROLE paqueteria_migrator;

        DO $obs003_trace_adoption$
        DECLARE
          v_definition text;
        BEGIN
          IF to_regclass('platform.outbox_events') IS NULL OR to_regclass('platform.location_outbox_events') IS NULL THEN
            RAISE EXCEPTION 'OBS-003-OUTBOX-TRACE-CONTEXT requires the canonical AI-06 outbox tables';
          END IF;
          IF to_regprocedure('{{OutboxFunctionSignature}}') IS NULL
             OR (SELECT pg_get_userbyid(proowner) FROM pg_proc WHERE oid=to_regprocedure('{{OutboxFunctionSignature}}'))
                <> 'paqueteria_operator_outbox_executor' THEN
            RAISE EXCEPTION 'OBS-003-OUTBOX-TRACE-CONTEXT requires the DSP-OPERATOR-OWNER-OUTBOX definer owned by its executor';
          END IF;

          -- Expand only: a nullable column without default, so no row is read or rewritten. A column of another shape is
          -- refused, never altered.
          IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='platform' AND table_name IN ('outbox_events','location_outbox_events')
              AND column_name='trace_parent'
              AND (data_type <> 'text' OR is_nullable <> 'YES' OR column_default IS NOT NULL)
          ) THEN
            RAISE EXCEPTION 'platform outbox trace_parent does not match the OBS-003-OUTBOX-TRACE-CONTEXT contract';
          END IF;
          ALTER TABLE platform.outbox_events ADD COLUMN IF NOT EXISTS trace_parent text;
          ALTER TABLE platform.location_outbox_events ADD COLUMN IF NOT EXISTS trace_parent text;

          SELECT pg_get_constraintdef(oid) INTO v_definition FROM pg_constraint
          WHERE conrelid='platform.outbox_events'::regclass AND conname='{{OutboxConstraint}}';
          IF v_definition IS NULL THEN
            ALTER TABLE platform.outbox_events ADD CONSTRAINT {{OutboxConstraint}}
              CHECK ({{CheckExpression}});
          ELSIF v_definition IS DISTINCT FROM '{{ConstraintDefinition.Replace("'", "''", StringComparison.Ordinal)}}' THEN
            RAISE EXCEPTION '{{OutboxConstraint}} does not match the OBS-003-OUTBOX-TRACE-CONTEXT contract';
          END IF;

          SELECT pg_get_constraintdef(oid) INTO v_definition FROM pg_constraint
          WHERE conrelid='platform.location_outbox_events'::regclass AND conname='{{LocationOutboxConstraint}}';
          IF v_definition IS NULL THEN
            ALTER TABLE platform.location_outbox_events ADD CONSTRAINT {{LocationOutboxConstraint}}
              CHECK ({{CheckExpression}});
          ELSIF v_definition IS DISTINCT FROM '{{ConstraintDefinition.Replace("'", "''", StringComparison.Ordinal)}}' THEN
            RAISE EXCEPTION '{{LocationOutboxConstraint}} does not match the OBS-003-OUTBOX-TRACE-CONTEXT contract';
          END IF;
        END
        $obs003_trace_adoption$;

        -- The definer writes the column of the rows it inserts; no SELECT, UPDATE or DELETE on it.
        GRANT INSERT (trace_parent) ON platform.outbox_events TO paqueteria_operator_outbox_executor;

        -- The definer is replaced by its owner, as the Notifications lane replaces its functions; CREATE on schema
        -- security is held only for the statement.
        GRANT CREATE ON SCHEMA security TO paqueteria_operator_outbox_executor;
        RESET ROLE;
        SET ROLE paqueteria_operator_outbox_executor;


        """,
        OutboxFunctionSql,
        """

        RESET ROLE;
        SET ROLE paqueteria_migrator;
        REVOKE CREATE ON SCHEMA security FROM paqueteria_operator_outbox_executor;

        """,
        VerifySql("up", ExecutorColumnGrants, traceBody: true),
        $$"""

        DO $obs003_trace_columns$
        BEGIN
          IF (SELECT count(*) FROM information_schema.columns
              WHERE table_schema='platform' AND table_name IN ('outbox_events','location_outbox_events')
                AND column_name='trace_parent' AND data_type='text' AND is_nullable='YES' AND column_default IS NULL) <> 2
             OR (SELECT count(*) FROM pg_constraint
                 WHERE conrelid IN ('platform.outbox_events'::regclass,'platform.location_outbox_events'::regclass)
                   AND conname IN ('{{OutboxConstraint}}','{{LocationOutboxConstraint}}')
                   AND pg_get_constraintdef(oid)='{{ConstraintDefinition.Replace("'", "''", StringComparison.Ordinal)}}') <> 2 THEN
            RAISE EXCEPTION 'OBS-003-OUTBOX-TRACE-CONTEXT up: the trace_parent columns or checks differ from the contract';
          END IF;
        END
        $obs003_trace_columns$;
        """);

    /// <summary>
    /// Rollback, after the Notifications lane step is rolled back: the published definer, its published grants, and both
    /// tables without the column and its check. Outbox rows stay; their trace context is dropped with the column.
    /// </summary>
    public static string DownSql { get; } = string.Concat(
        $$"""
        RESET ROLE;
        SET ROLE paqueteria_migrator;

        DO $obs003_trace_downgrade$
        BEGIN
          -- Nested so the history table is named only once it is known to exist (PL/pgSQL plans a condition whole).
          IF to_regclass('platform.__ef_migrations_history_notifications') IS NOT NULL THEN
            IF EXISTS (
                 SELECT 1 FROM platform.__ef_migrations_history_notifications
                 WHERE "MigrationId"='{{NotificationsStepMigrationId}}') THEN
              RAISE EXCEPTION USING
                MESSAGE = '{{DowngradeOrderRefused}}',
                DETAIL = 'Roll the Notifications lane step {{NotificationsStepMigrationId}} back first; its functions write platform.outbox_events.trace_parent.',
                ERRCODE = 'P0001';
            END IF;
          END IF;
        END
        $obs003_trace_downgrade$;

        GRANT CREATE ON SCHEMA security TO paqueteria_operator_outbox_executor;
        RESET ROLE;
        SET ROLE paqueteria_operator_outbox_executor;


        """,
        AddOperatorOwnerOutboxExecutor.OutboxFunctionSql,
        """

        RESET ROLE;
        SET ROLE paqueteria_migrator;
        REVOKE CREATE ON SCHEMA security FROM paqueteria_operator_outbox_executor;
        REVOKE INSERT (trace_parent) ON platform.outbox_events FROM paqueteria_operator_outbox_executor;
        ALTER TABLE platform.outbox_events DROP CONSTRAINT IF EXISTS outbox_events_trace_parent_check;
        ALTER TABLE platform.outbox_events DROP COLUMN IF EXISTS trace_parent;
        ALTER TABLE platform.location_outbox_events DROP CONSTRAINT IF EXISTS location_outbox_events_trace_parent_check;
        ALTER TABLE platform.location_outbox_events DROP COLUMN IF EXISTS trace_parent;

        """,
        VerifySql("down", AddOperatorOwnerOutboxExecutor.ExecutorColumnGrants, traceBody: false),
        """

        DO $obs003_trace_columns_dropped$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='platform' AND table_name IN ('outbox_events','location_outbox_events')
              AND column_name='trace_parent') THEN
            RAISE EXCEPTION 'OBS-003-OUTBOX-TRACE-CONTEXT down: a trace_parent column is still present';
          END IF;
        END
        $obs003_trace_columns_dropped$;
        """);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);

    private static string ReplaceOnce(string sql, string published, string replacement)
    {
        var index = sql.IndexOf(published, StringComparison.Ordinal);
        if (index < 0 || sql.IndexOf(published, index + published.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                $"The published operator outbox definer must contain '{published.Trim()}' exactly once.");
        }

        return string.Concat(sql.AsSpan(0, index), replacement, sql.AsSpan(index + published.Length));
    }
}
