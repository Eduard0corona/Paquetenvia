using System.Text.RegularExpressions;
using Dispatch.Infrastructure.Persistence.Migrations;
using Notifications.Infrastructure.Persistence.Migrations;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Database.Outbox;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.UnitTests.Operations;

/// <summary>
/// OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13: static guarantees of the two lane steps that carry the producing trace
/// context through the outbox. Their behaviour, Up, Down and Up again and the catalog are proven on PostgreSQL by the
/// contract tests (<c>OutboxTraceContextPostgreSqlContractTests</c>).
/// </summary>
public sealed partial class Obs003OutboxTraceContextMigrationTests
{
    [Fact]
    public void The_column_check_is_the_stored_shape_of_the_runtime()
    {
        // The CHECK both tables declare is the same pattern the runtime writes and parses, and AI-06 declares it verbatim.
        Assert.Contains(OutboxTraceContext.Pattern, AddOutboxTraceContext.CheckExpression, StringComparison.Ordinal);
        Assert.Equal(OutboxTraceContextColumn.Name, AddOutboxTraceContext.Column);
        var schema = File.ReadAllText(TestPath("docs/normative/v0.6/database/AI-06_SCHEMA.sql"));
        Assert.Equal(2, Regex.Count(schema, Regex.Escape("CHECK (" + AddOutboxTraceContext.CheckExpression + ")")));
        Assert.Contains("trace_parent text CONSTRAINT " + AddOutboxTraceContext.OutboxConstraint, schema, StringComparison.Ordinal);
        Assert.Contains("trace_parent text CONSTRAINT " + AddOutboxTraceContext.LocationOutboxConstraint, schema, StringComparison.Ordinal);
        Assert.Equal(
            "CHECK (((trace_parent ~ '" + OutboxTraceContext.Pattern + "'::text) " +
            "AND (substr(trace_parent, 4, 32) <> repeat('0'::text, 32)) " +
            "AND (substr(trace_parent, 37, 16) <> repeat('0'::text, 16))))",
            AddOutboxTraceContext.ConstraintDefinition);
    }

    [Fact]
    public void The_operator_definer_differs_from_the_published_one_only_by_the_reviewed_edits()
    {
        var published = AddOperatorOwnerOutboxExecutor.OutboxFunctionSql;
        var installed = AddOutboxTraceContext.OutboxFunctionSql;

        // Same header (signature, return type, SECURITY DEFINER, search_path) and same verification statements.
        Assert.Equal(Header(published), Header(installed));
        var reverted = AddOutboxTraceContext.FunctionEdits.Aggregate(
            installed, (sql, edit) => sql.Replace(edit.Replacement, edit.Published, StringComparison.Ordinal));
        Assert.Equal(published, reverted);
        Assert.Contains("last_error,created_at,processed_at,trace_parent)", installed, StringComparison.Ordinal);
        Assert.Contains("NULLIF(pg_catalog.current_setting('app.trace_parent', true), ''));", installed, StringComparison.Ordinal);
        Assert.Equal(OutboxTraceContextColumn.TransactionSetting, "app.trace_parent");
        Assert.DoesNotMatch(ExecuteKeyword(), installed);
        Assert.DoesNotContain("RETURNING", installed, StringComparison.OrdinalIgnoreCase);

        // Down reinstalls the published definer byte for byte and drops exactly the two columns and checks.
        Assert.Contains(published, AddOutboxTraceContext.DownSql, StringComparison.Ordinal);
        Assert.DoesNotContain(installed, AddOutboxTraceContext.DownSql, StringComparison.Ordinal);
        Assert.Contains(installed, AddOutboxTraceContext.UpSql, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(AddOutboxTraceContext.DownSql, "DROP COLUMN"));
        Assert.Equal(2, Regex.Count(AddOutboxTraceContext.DownSql, "DROP CONSTRAINT"));
        Assert.DoesNotContain("DROP FUNCTION", AddOutboxTraceContext.DownSql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP FUNCTION", AddOutboxTraceContext.UpSql, StringComparison.Ordinal);
        Assert.Contains(AddOutboxTraceContext.DowngradeOrderRefused, AddOutboxTraceContext.DownSql, StringComparison.Ordinal);
        Assert.Contains(CarryOutboxTraceContext.MigrationId, AddOutboxTraceContext.DownSql, StringComparison.Ordinal);
        Assert.Equal(CarryOutboxTraceContext.MigrationId, AddOutboxTraceContext.NotificationsStepMigrationId);
        Assert.DoesNotContain("OWNER TO", AddOutboxTraceContext.UpSql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT EXECUTE", AddOutboxTraceContext.UpSql, StringComparison.Ordinal);
        Assert.DoesNotContain("REVOKE EXECUTE", AddOutboxTraceContext.UpSql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_content_guards_keep_the_trace_context_immutable_and_down_restores_the_published_ones()
    {
        var schema = File.ReadAllText(TestPath("docs/normative/v0.6/database/AI-06_SCHEMA.sql"));
        foreach (var (published, installed) in new[]
                 {
                     (AddOutboxTraceContext.PublishedOutboxContentGuardSql, AddOutboxTraceContext.OutboxContentGuardSql),
                     (AddOutboxTraceContext.PublishedLocationOutboxContentGuardSql, AddOutboxTraceContext.LocationOutboxContentGuardSql),
                 })
        {
            // AI-06 declares exactly the installed guard; reverting the two reviewed edits gives the published one.
            Assert.Equal(1, Regex.Count(schema, Regex.Escape(installed)));
            Assert.DoesNotContain(published, schema, StringComparison.Ordinal);
            Assert.Equal(
                published,
                AddOutboxTraceContext.ContentGuardEdits.Aggregate(
                    installed, (sql, edit) => sql.Replace(edit.Replacement, edit.Published, StringComparison.Ordinal)));
            Assert.Contains("NEW.trace_parent)", installed, StringComparison.Ordinal);
            Assert.Contains("OLD.trace_parent) THEN", installed, StringComparison.Ordinal);
            Assert.StartsWith("CREATE OR REPLACE FUNCTION platform.", installed, StringComparison.Ordinal);

            // Up installs the guard after the column exists; Down restores the published one before dropping it.
            var up = AddOutboxTraceContext.UpSql;
            Assert.True(
                up.IndexOf("ADD COLUMN IF NOT EXISTS trace_parent", StringComparison.Ordinal) < up.IndexOf(installed, StringComparison.Ordinal));
            var down = AddOutboxTraceContext.DownSql;
            Assert.DoesNotContain(installed, down, StringComparison.Ordinal);
            Assert.True(down.IndexOf(published, StringComparison.Ordinal) is >= 0 and var at &&
                        at < down.IndexOf("DROP COLUMN", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_executor_gains_exactly_one_insert_grant_owned_by_the_lane_step()
    {
        Assert.Equal(["platform.outbox_events.trace_parent:INSERT"], AddOutboxTraceContext.AddedExecutorColumnGrants);
        Assert.Equal(AddOutboxTraceContext.AddedExecutorColumnGrants, DatabaseBaselineAssertions.OperatorOutboxExecutorTraceContextGrants);
        Assert.Equal(
            AddOperatorOwnerOutboxExecutor.ExecutorColumnGrants.Count + 1,
            AddOutboxTraceContext.ExecutorColumnGrants.Count);
        Assert.Equal(
            AddOutboxTraceContext.ExecutorColumnGrants.Order(StringComparer.Ordinal),
            AddOutboxTraceContext.ExecutorColumnGrants);
        Assert.Equal(AddOutboxTraceContext.MigrationId, E002OperatorOutboxStateReader.OutboxTraceContextMigrationId);

        // AI-18 keeps the published executor statement (the published Dispatch step verifies exactly it).
        var roles = File.ReadAllText(TestPath("docs/normative/v0.6/database/AI-18_DATABASE_ROLE_MODEL.sql"));
        Assert.Contains(
            "GRANT INSERT (id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,priority,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,last_error,created_at,processed_at) ON platform.outbox_events TO paqueteria_operator_outbox_executor;",
            roles,
            StringComparison.Ordinal);
        Assert.DoesNotContain("trace_parent) ON platform.outbox_events TO", RemoveComments(roles), StringComparison.Ordinal);
    }

    [Fact]
    public void The_notification_writers_differ_from_the_published_ones_only_by_the_reviewed_edits()
    {
        foreach (var (published, installed, edits) in new[]
                 {
                     (CarryOutboxTraceContext.PublishedEmitSql, CarryOutboxTraceContext.EmitSql, CarryOutboxTraceContext.EmitEdits),
                     (CarryOutboxTraceContext.PublishedExpandSql, CarryOutboxTraceContext.ExpandSql, CarryOutboxTraceContext.ExpandEdits),
                 })
        {
            Assert.StartsWith("CREATE OR REPLACE FUNCTION ", published, StringComparison.Ordinal);
            Assert.Contains(
                published.Replace("CREATE OR REPLACE FUNCTION ", "CREATE FUNCTION ", StringComparison.Ordinal),
                AddTenantSafeOutboxNotifications.UpSql,
                StringComparison.Ordinal);
            Assert.Equal(Header(published), Header(installed));
            Assert.Equal(
                published,
                edits.Aggregate(installed, (sql, edit) => sql.Replace(edit.Replacement, edit.Published, StringComparison.Ordinal)));
            Assert.Contains("created_at,trace_parent)", installed, StringComparison.Ordinal);
            Assert.DoesNotMatch(ExecuteKeyword(), installed);
            Assert.Contains(installed, CarryOutboxTraceContext.UpSql, StringComparison.Ordinal);
            Assert.Contains(published, CarryOutboxTraceContext.DownSql, StringComparison.Ordinal);
        }

        // The emitter copies the trace of the event that created the notification; the expansion the one it expands.
        Assert.Contains("JOIN platform.outbox_events o ON o.id=n.source_event_id", CarryOutboxTraceContext.EmitSql, StringComparison.Ordinal);
        Assert.Contains("p_occurred_at,p_occurred_at,v_trace_parent);", CarryOutboxTraceContext.EmitSql, StringComparison.Ordinal);
        Assert.Contains("p_occurred_at,p_occurred_at,v_source.trace_parent);", CarryOutboxTraceContext.ExpandSql, StringComparison.Ordinal);
        // Payloads are untouched: no jsonb_build_object gains a member.
        Assert.Equal(
            Regex.Count(CarryOutboxTraceContext.PublishedEmitSql, "jsonb_build_object"),
            Regex.Count(CarryOutboxTraceContext.EmitSql, "jsonb_build_object"));
        Assert.DoesNotContain("'trace", CarryOutboxTraceContext.EmitSql, StringComparison.Ordinal);
        Assert.DoesNotContain("'trace", CarryOutboxTraceContext.ExpandSql, StringComparison.Ordinal);

        // Signatures, owner and grants are untouched; the temporary CREATE on security is revoked again.
        foreach (var sql in new[] { CarryOutboxTraceContext.UpSql, CarryOutboxTraceContext.DownSql })
        {
            Assert.DoesNotContain("GRANT EXECUTE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("REVOKE EXECUTE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("OWNER TO", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("DROP ", sql, StringComparison.Ordinal);
            Assert.Contains("REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;", sql, StringComparison.Ordinal);
        }

        // NTF-RECIPIENT-NOTICES-2026-10-20: the recipient notice lane step follows it in the recorded history.
        var history = E002NotificationStateReaderHistory();
        Assert.Contains(CarryOutboxTraceContext.MigrationId, history);
        Assert.Contains(AddRecipientNoticeLane.MigrationId, history);
        Assert.Equal(AddRecipientNoticeLane.MigrationId, E002NotificationStateReader.RecipientNoticeLaneMigrationId);
    }

    private static IReadOnlyList<string> E002NotificationStateReaderHistory()
    {
        var source = File.ReadAllText(TestPath(
            "src/BuildingBlocks/Paqueteria.Infrastructure/Database/Baseline/E002NotificationStateReader.cs"));
        return Regex.Matches(source, "\"(\\d{14}_[A-Za-z]+)\"").Select(match => match.Groups[1].Value).ToArray();
    }

    private static string Header(string definition) =>
        definition[..(definition.IndexOf("AS $", StringComparison.Ordinal) + "AS $".Length)];

    private static string RemoveComments(string sql) =>
        string.Join('\n', sql.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    private static string TestPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Paqueteria.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    [GeneratedRegex("(^|[^a-z_])EXECUTE([^a-z_]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExecuteKeyword();
}
