using System.Reflection;
using System.Text.RegularExpressions;
using Dispatch.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Notifications.Infrastructure.Persistence.Migrations;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13 (project owner, Q-PR7: "Columna nueva (Recommended)"): no producer of a
/// platform outbox row is missed. Every runtime INSERT names <c>trace_parent</c> and binds it to
/// <c>OutboxTraceContextColumn.Parameter()</c> (the producer's own <c>Activity.Current</c>, in its transaction, no RETURNING);
/// every SQL function that inserts an outbox row writes it too; every claim reader reads it back and every consumer
/// processes each message in it. The value never reaches a payload, a SignalR message or a notification.
/// </summary>
public sealed partial class OutboxTraceContextArchitectureTests
{
    /// <summary>Every runtime file that inserts a platform outbox row, with its number of INSERT statements.</summary>
    private static readonly IReadOnlyDictionary<string, int> RuntimeProducers = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["src/Modules/Dispatch/Dispatch.Infrastructure/Assignments/PostgreSqlAssignmentToOrderCoordinator.cs"] = 3,
        ["src/Modules/Dispatch/Dispatch.Infrastructure/ExternalOffers/PostgreSqlExternalOfferService.cs"] = 1,
        ["src/Modules/Dispatch/Dispatch.Infrastructure/Lifecycle/PostgreSqlAssignmentLifecycleReactor.cs"] = 1,
        ["src/Modules/Drivers/Drivers.Infrastructure/Locations/PostgreSqlDriverLocationIngestionService.cs"] = 1,
        ["src/Modules/Orders/Orders.Infrastructure/Orders/PostgreSqlOrderTransitionService.cs"] = 3,
        ["src/Modules/Orders/Orders.Infrastructure/Orders/QuoteSnapshotToOrderCoordinator.cs"] = 1,
        ["src/Modules/Routing/Routing.Infrastructure/Routes/PostgreSqlRouteService.Persistence.cs"] = 1,
    };

    /// <summary>
    /// Every SQL function any migration defines with an INSERT into a platform outbox table, and the lane step that
    /// installs its current, trace-carrying body.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SqlProducers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["security.append_operator_order_outbox"] =
            "src/Modules/Dispatch/Dispatch.Infrastructure/Persistence/Migrations/20261013000100_AddOutboxTraceContext.cs",
        ["security.emit_notification_status_changed"] =
            "src/Modules/Notifications/Notifications.Infrastructure/Persistence/Migrations/20261013000200_CarryOutboxTraceContext.cs",
        ["security.expand_order_created_notifications"] =
            "src/Modules/Notifications/Notifications.Infrastructure/Persistence/Migrations/20261013000200_CarryOutboxTraceContext.cs",
    };

    /// <summary>The claim readers (one reader loop per claim call) and the consumers (one activity per processing entry).</summary>
    private static readonly IReadOnlyDictionary<string, int> ClaimReaders = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["src/Modules/Dispatch/Dispatch.Infrastructure/Lifecycle/PostgreSqlDispatchOutboxStore.cs"] = 1,
        ["src/Modules/Notifications/Notifications.Infrastructure/Dispatching/PostgreSqlNotificationsStore.cs"] = 1,
        ["src/Modules/Realtime/Realtime.Infrastructure/Dispatching/PostgreSqlRealtimeOutboxStore.cs"] = 2,
    };

    private const string OutboxModel = "src/BuildingBlocks/Paqueteria.Infrastructure/Database/Outbox/PlatformOutboxDbContext.cs";

    /// <summary>
    /// OBS-003-WORKER-AUDIT-TRACE-2026-10-14: the one Worker audit writer that stores the trace id of the message it
    /// processes in <c>platform.audit_logs.request_id</c>.
    /// </summary>
    private const string AuditTraceReader = "src/Modules/Dispatch/Dispatch.Infrastructure/Lifecycle/PostgreSqlAssignmentLifecycleReactor.cs";

    /// <summary>
    /// OBS-003-WORKER-AUDIT-TRACE-2026-10-14 (Q1 "Sí, en un PR aparte (Recommended)", QA "Se quedan vacías
    /// (Recommended)"): the assignment lifecycle audit row takes the trace id of the claimed message, once, through
    /// <c>OutboxTraceContext.TraceIdOf</c> (NULL without a valid stored context, never the cycle's trace or
    /// <c>Activity.Current</c>); proof validation, which processes no outbox message, keeps request_id NULL, and no other
    /// Worker audit writer reads the context.
    /// </summary>
    [Fact]
    public void The_assignment_lifecycle_audit_row_takes_the_message_trace_id_and_proof_validation_keeps_null()
    {
        var reactor = CodeWithoutComments(TestRepository.GetPath(AuditTraceReader));
        Assert.Equal(1, Regex.Count(reactor, Regex.Escape(".TraceParent")));
        Assert.Equal(1, Regex.Count(reactor, Regex.Escape("OutboxTraceContext.TraceIdOf(message.TraceParent)")));
        Assert.Equal(1, Regex.Count(reactor, Regex.Escape("new AuditEntry(")));
        var entry = reactor[reactor.IndexOf("new AuditEntry(", StringComparison.Ordinal)..];
        entry = entry[..entry.IndexOf("cancellationToken);", StringComparison.Ordinal)];
        Assert.Contains("OutboxTraceContext.TraceIdOf(message.TraceParent)", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("Activity.Current", reactor, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentTraceId", reactor, StringComparison.Ordinal);

        // Proof validation: its one audit entry passes null as the request id (the seventh argument).
        var proofs = CodeWithoutComments(TestRepository.GetPath(
            "src/Modules/Custody/Custody.Infrastructure/Proofs/ProofValidationProcessor.cs"));
        Assert.Equal(1, Regex.Count(proofs, Regex.Escape("new AuditEntry(")));
        Assert.Matches(
            @"new AuditEntry\(\s*Guid\.NewGuid\(\),\s*claim\.OwnerOrganizationId,\s*claim\.RequestedBy,\s*status == ""READY""[^;]*?""proof_upload_session"",\s*claim\.SessionId,\s*null,",
            proofs);
        Assert.DoesNotContain("TraceParent", proofs, StringComparison.Ordinal);
        Assert.DoesNotContain("TraceIdOf", proofs, StringComparison.Ordinal);
    }

    private static readonly IReadOnlyDictionary<string, int> Consumers = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["src/Modules/Dispatch/Dispatch.Infrastructure/Lifecycle/AssignmentLifecycleProcessor.cs"] = 1,
        ["src/Modules/Notifications/Notifications.Infrastructure/Dispatching/NotificationsOutboxDispatcher.cs"] = 1,
        ["src/Modules/Realtime/Realtime.Infrastructure/Dispatching/RealtimeOutboxProcessor.cs"] = 2,
    };

    [Fact]
    public void Every_runtime_outbox_insert_supplies_the_producing_trace_context()
    {
        var inserts = RuntimeSources()
            .Select(file => (File: Relative(file), Code: CodeWithoutComments(file)))
            .Select(source => (source.File, source.Code, Statements: OutboxInserts(source.Code).ToArray()))
            .Where(source => source.Statements.Length > 0)
            .ToArray();

        // A new producer must be reviewed here, and each listed one still writes exactly its statements.
        Assert.Equal(
            RuntimeProducers.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            inserts.Select(source => new KeyValuePair<string, int>(source.File, source.Statements.Length))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal));
        foreach (var (file, code, statements) in inserts)
        {
            Assert.All(statements, statement =>
            {
                var columns = statement[..statement.IndexOf("VALUES", StringComparison.Ordinal)];
                var values = statement[statement.IndexOf("VALUES", StringComparison.Ordinal)..];
                Assert.Equal(1, Regex.Count(columns, @"\btrace_parent\b"));
                Assert.Equal(1, Regex.Count(values, "@trace_parent"));
                Assert.DoesNotContain("RETURNING", statement, StringComparison.OrdinalIgnoreCase);
            });
            Assert.True(
                statements.Length == Regex.Count(code, Regex.Escape("command.Parameters.Add(OutboxTraceContextColumn.Parameter());")),
                $"{file}: every outbox INSERT binds @trace_parent to OutboxTraceContextColumn.Parameter()");
        }

        // Nothing in src writes an outbox row through the EF model or any other path.
        Assert.DoesNotContain(
            RuntimeSources().Select(Relative),
            file => Regex.IsMatch(CodeWithoutComments(TestRepository.GetPath(file)), @"\b(OutboxEvents|LocationOutboxEvents)\.Add") &&
                    !string.Equals(file, OutboxModel, StringComparison.Ordinal));
    }

    [Fact]
    public void The_operator_outbox_definer_receives_the_trace_context_in_the_callers_transaction()
    {
        var callers = RuntimeSources()
            .Where(file => CodeWithoutComments(file).Contains("security.append_operator_order_outbox(", StringComparison.Ordinal))
            .Select(Relative)
            .ToArray();
        Assert.Equal(["src/Modules/Dispatch/Dispatch.Infrastructure/Assignments/OperatorOwnerEventWriter.cs"], callers);
        var writer = CodeWithoutComments(TestRepository.GetPath(callers[0]));
        Assert.Contains(
            "\"SELECT pg_catalog.set_config('app.trace_parent', @trace_parent, true)\"",
            writer,
            StringComparison.Ordinal);
        var method = writer[writer.IndexOf("public static async Task AppendOutboxAsync(", StringComparison.Ordinal)..
                            writer.IndexOf("public static async Task AppendAuditAsync(", StringComparison.Ordinal)];
        Assert.True(
            method.IndexOf("TraceContextSql", StringComparison.Ordinal) is >= 0 and var traceAt &&
            traceAt < method.IndexOf("OutboxSql", traceAt + 1, StringComparison.Ordinal),
            "the transaction-local trace context must be set before the definer is called");
        Assert.Contains("OutboxTraceContext.Current() ?? string.Empty", method, StringComparison.Ordinal);
        Assert.Contains("NULLIF(pg_catalog.current_setting('app.trace_parent', true), '')", AddOutboxTraceContext.OutboxFunctionSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_sql_function_that_writes_an_outbox_row_carries_a_trace_context()
    {
        // The SQL every migration actually runs (its Up operations, in migration order, after the AI-06 baseline), and
        // the latest definition of every function in it: a later redefinition replaces the earlier one.
        var latest = LatestFunctionDefinitions(BaselineAndMigrationUpSql());
        var (writers, missing) = OutboxWriters(latest);
        Assert.Equal(SqlProducers.Keys.Order(StringComparer.Ordinal), writers.Order(StringComparer.Ordinal));
        Assert.Empty(missing);

        // The current definer writes the caller's context; the Notifications writers copy the originating row's.
        Assert.StartsWith(AddOutboxTraceContext.OutboxFunctionSql, latest["security.append_operator_order_outbox"], StringComparison.Ordinal);
        Assert.StartsWith(CarryOutboxTraceContext.EmitSql, latest["security.emit_notification_status_changed"], StringComparison.Ordinal);
        Assert.StartsWith(CarryOutboxTraceContext.ExpandSql, latest["security.expand_order_created_notifications"], StringComparison.Ordinal);
        Assert.Contains("last_error,created_at,processed_at,trace_parent)", AddOutboxTraceContext.OutboxFunctionSql, StringComparison.Ordinal);
        Assert.Contains("p_occurred_at,p_occurred_at,v_trace_parent);", CarryOutboxTraceContext.EmitSql, StringComparison.Ordinal);
        Assert.Contains("p_occurred_at,p_occurred_at,v_source.trace_parent);", CarryOutboxTraceContext.ExpandSql, StringComparison.Ordinal);

        // The content guards keep the column as immutable as the content.
        Assert.Contains("NEW.created_at,NEW.trace_parent)", latest["platform.protect_outbox_content"], StringComparison.Ordinal);
        Assert.Contains("NEW.created_at,NEW.trace_parent)", latest["platform.protect_location_outbox_content"], StringComparison.Ordinal);

        // The migrator applies both steps as the latest of their lanes.
        var coordinator = File.ReadAllText(TestRepository.GetPath("tools/Paqueteria.DatabaseMigrator/ModuleMigrationCoordinator.cs"));
        Assert.Contains("(\"Dispatch\", \"__ef_migrations_history_dispatch\", AddOutboxTraceContext.MigrationId,", coordinator, StringComparison.Ordinal);
        Assert.Contains("(\"Notifications\", \"__ef_migrations_history_notifications\", CarryOutboxTraceContext.MigrationId,", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_claim_reader_reads_the_trace_context_and_every_consumer_processes_in_it()
    {
        var readers = RuntimeSources()
            .Where(file => ClaimCallPattern().IsMatch(CodeWithoutComments(file)))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ClaimReaders.Keys.Order(StringComparer.Ordinal), readers);
        foreach (var (file, count) in ClaimReaders)
        {
            Assert.Equal(count, Regex.Count(CodeWithoutComments(TestRepository.GetPath(file)), Regex.Escape("OutboxTraceContextColumn.Read(reader)")));
        }

        var consumers = RuntimeSources()
            .Where(file => CodeWithoutComments(file).Contains("TraceCorrelation.StartOutboxMessage(", StringComparison.Ordinal))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(Consumers.Keys.Order(StringComparer.Ordinal), consumers);
        foreach (var (file, count) in Consumers)
        {
            var code = CodeWithoutComments(TestRepository.GetPath(file));
            Assert.Equal(count, Regex.Count(code, Regex.Escape("TraceCorrelation.StartOutboxMessage(")));
            Assert.Equal(count, Regex.Count(code, Regex.Escape("message.TraceParent)")));
        }
    }

    [Fact]
    public void The_trace_context_never_reaches_a_payload_a_signalr_message_or_a_notification()
    {
        // Only the consumers (as the parent of the message's activity), the EF column mapping and the assignment
        // lifecycle audit writer (only its trace id, as request_id) read the member; the stores fill it from the claimed
        // row through OutboxTraceContextColumn.Read.
        var readers = RuntimeSources()
            .Where(file => CodeWithoutComments(file).Contains(".TraceParent", StringComparison.Ordinal))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            Consumers.Keys.Append(OutboxModel).Append(AuditTraceReader).Order(StringComparer.Ordinal),
            readers);

        // No payload, envelope, hub, publisher, notification or messaging code names it.
        foreach (var directory in new[]
                 {
                     "src/Modules/Realtime/Realtime.Application/Events",
                     "src/Modules/Realtime/Realtime.Application/Publishing",
                     "src/Modules/Realtime/Realtime.Infrastructure/Publishing",
                     "src/Modules/Realtime/Realtime.Endpoints",
                     "src/Modules/Notifications/Notifications.Infrastructure/Delivery",
                     "src/BuildingBlocks/Paqueteria.Application/Messaging",
                     "src/BuildingBlocks/Paqueteria.Infrastructure/Messaging",
                 })
        {
            foreach (var file in Directory.EnumerateFiles(TestRepository.GetPath(directory), "*.cs", SearchOption.AllDirectories))
            {
                var code = CodeWithoutComments(file);
                Assert.False(
                    TraceContextNamePattern().IsMatch(code),
                    $"{Relative(file)} must not name the outbox trace context");
            }
        }

        // Every producer builds its payload without it: the context is a column of the row, never a payload member.
        foreach (var file in RuntimeProducers.Keys)
        {
            var code = CodeWithoutComments(TestRepository.GetPath(file));
            Assert.DoesNotMatch(@"trace_?parent\s*=", code);
            Assert.DoesNotMatch("\"trace", code);
        }
    }

    private static IEnumerable<string> OutboxInserts(string code)
    {
        foreach (Match match in OutboxInsertPattern().Matches(code))
        {
            var end = code.IndexOf(';', match.Index);
            var closing = code.IndexOf("\"\"\"", match.Index, StringComparison.Ordinal);
            if (closing >= 0 && (end < 0 || closing < end))
            {
                end = closing;
            }

            yield return code[match.Index..end];
        }
    }

    [Fact]
    public void The_sql_writer_check_catches_a_later_redefinition_without_the_trace_context()
    {
        // A later migration that redefines the emitter with an outbox INSERT that does not name trace_parent.
        var latest = LatestFunctionDefinitions(
        [
            CarryOutboxTraceContext.UpSql,
            """
            CREATE OR REPLACE FUNCTION security.emit_notification_status_changed(p_owner_org_id uuid)
            RETURNS void LANGUAGE plpgsql SECURITY DEFINER AS $fn$
            BEGIN
              INSERT INTO platform.outbox_events(id,owner_org_id,topic) VALUES (gen_random_uuid(),p_owner_org_id,'x');
            END
            $fn$;
            """,
        ]);
        var (writers, missing) = OutboxWriters(latest);
        Assert.Contains("security.emit_notification_status_changed", writers);
        Assert.Equal(["security.emit_notification_status_changed"], missing);
    }

    [Fact]
    public void The_sql_writer_check_catches_unqualified_quoted_and_listless_outbox_inserts()
    {
        var latest = LatestFunctionDefinitions(
        [
            """
            CREATE FUNCTION security.synthetic_unqualified() RETURNS void LANGUAGE sql AS $fn$
              INSERT INTO outbox_events(id,topic) VALUES (gen_random_uuid(),'x');
            $fn$;
            CREATE FUNCTION security.synthetic_listless() RETURNS void LANGUAGE sql AS $fn$
              INSERT INTO platform.location_outbox_events SELECT * FROM platform.location_outbox_events;
            $fn$;
            CREATE OR REPLACE FUNCTION "security"."synthetic_quoted"() RETURNS void LANGUAGE sql AS $fn$
              INSERT INTO "platform"."location_outbox_events" (id,trace_parent) VALUES (gen_random_uuid(),NULL);
            $fn$;
            CREATE FUNCTION synthetic_unscoped() RETURNS void LANGUAGE sql AS $fn$
              SELECT 1;
            $fn$;
            """,
            "INSERT INTO platform.outbox_events(id) VALUES (gen_random_uuid());",
        ]);
        var (writers, missing) = OutboxWriters(latest);
        Assert.Equal(
            ["(top level of SQL 2)", "security.synthetic_listless", "security.synthetic_quoted", "security.synthetic_unqualified"],
            writers.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["(top level of SQL 2)", "security.synthetic_listless", "security.synthetic_unqualified"],
            missing.Order(StringComparer.Ordinal));
        Assert.True(latest.ContainsKey("synthetic_unscoped"));
    }

    /// <summary>
    /// The AI-06 baseline followed by the Up SQL of every EF migration of every lane, in migration id order: what
    /// installs the current definition of each function. Every source file that declares a migration is accounted for.
    /// </summary>
    private static IReadOnlyList<string> BaselineAndMigrationUpSql()
    {
        var separator = Path.DirectorySeparatorChar;
        var sources = Directory.EnumerateFiles(TestRepository.GetPath("src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => file.Contains($"{separator}Migrations{separator}", StringComparison.Ordinal) &&
                           !file.Contains($"{separator}bin{separator}", StringComparison.Ordinal) &&
                           !file.Contains($"{separator}obj{separator}", StringComparison.Ordinal) &&
                           File.ReadAllText(file).Contains("[Migration(", StringComparison.Ordinal))
            .ToArray();
        var migrations = sources
            .Select(ProjectAssemblyName)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(name => Assembly.Load(new AssemblyName(name)).GetTypes())
            .Where(type => !type.IsAbstract && typeof(Migration).IsAssignableFrom(type) &&
                           type.GetCustomAttribute<MigrationAttribute>() is not null)
            .Select(type =>
            {
                var migration = (Migration)Activator.CreateInstance(type, nonPublic: true)!;
                migration.ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
                return (
                    Id: type.GetCustomAttribute<MigrationAttribute>()!.Id,
                    Sql: string.Join("\n", migration.UpOperations.OfType<SqlOperation>().Select(operation => operation.Sql)));
            })
            .OrderBy(migration => migration.Id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(sources.Length, migrations.Length);
        return
        [
            File.ReadAllText(TestRepository.GetPath("docs/normative/v0.6/database/AI-06_SCHEMA.sql")),
            .. migrations.Select(migration => migration.Sql),
        ];
    }

    /// <summary>The assembly of the project that holds <paramref name="file"/> (named after its .csproj).</summary>
    private static string ProjectAssemblyName(string file)
    {
        for (var directory = new FileInfo(file).Directory; directory is not null; directory = directory.Parent)
        {
            var project = directory.EnumerateFiles("*.csproj").SingleOrDefault();
            if (project is not null)
            {
                return Path.GetFileNameWithoutExtension(project.Name);
            }
        }

        throw new InvalidOperationException($"{file} belongs to no project");
    }

    /// <summary>
    /// The latest definition of every function in <paramref name="orderedSql"/>: from its CREATE [OR REPLACE] FUNCTION
    /// header to the next header of the same script. Statements before the first header of a script are kept as
    /// "(top level of SQL n)", so an outbox INSERT outside any function is reported too.
    /// </summary>
    private static Dictionary<string, string> LatestFunctionDefinitions(IReadOnlyList<string> orderedSql)
    {
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var script = 0; script < orderedSql.Count; script++)
        {
            var sql = orderedSql[script];
            var headers = FunctionHeaderPattern().Matches(sql).ToArray();
            var prefix = sql[..(headers.Length == 0 ? sql.Length : headers[0].Index)];
            if (OutboxInsertPattern().IsMatch(prefix))
            {
                latest[$"(top level of SQL {script + 1})"] = prefix;
            }

            for (var index = 0; index < headers.Length; index++)
            {
                var end = index + 1 < headers.Length ? headers[index + 1].Index : sql.Length;
                var name = headers[index].Groups["name"].Value.Replace("\"", string.Empty, StringComparison.Ordinal);
                latest[Regex.Replace(name, @"\s+", string.Empty).ToLowerInvariant()] = sql[headers[index].Index..end];
            }
        }

        return latest;
    }

    /// <summary>
    /// The definitions that insert into either outbox table, and those with an INSERT whose column list does not name
    /// trace_parent (or that has no column list).
    /// </summary>
    private static (IReadOnlyList<string> Writers, IReadOnlyList<string> Missing) OutboxWriters(
        IReadOnlyDictionary<string, string> definitions)
    {
        var writers = definitions.Where(definition => OutboxInsertPattern().IsMatch(definition.Value))
            .Select(definition => definition.Key)
            .ToArray();
        var missing = writers.Where(name => OutboxInsertPattern().Matches(definitions[name]).Any(insert =>
            {
                var rest = definitions[name][(insert.Index + insert.Length)..].TrimStart();
                return !rest.StartsWith('(') ||
                       !Regex.IsMatch(rest[..(rest.IndexOf(')', StringComparison.Ordinal) + 1)], @"\btrace_parent\b");
            }))
            .ToArray();
        return (writers, missing);
    }

    private static IEnumerable<string> RuntimeSources() =>
        Directory.EnumerateFiles(TestRepository.GetPath("src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !file.Contains($"{Path.DirectorySeparatorChar}Baseline{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string CodeWithoutComments(string file) => string.Join(
        '\n',
        File.ReadLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Relative(string file) =>
        Path.GetRelativePath(TestRepository.Root, file).Replace(Path.DirectorySeparatorChar, '/');

    // Qualified or not (search_path), quoted or not.
    [GeneratedRegex(@"INSERT\s+INTO\s+(?:""?platform""?\s*\.\s*)?""?(?:location_)?outbox_events""?(?![A-Za-z0-9_])", RegexOptions.IgnoreCase)]
    private static partial Regex OutboxInsertPattern();

    [GeneratedRegex(@"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?<name>(?:""?[A-Za-z_][A-Za-z0-9_]*""?\s*\.\s*)?""?[A-Za-z_][A-Za-z0-9_]*""?)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionHeaderPattern();

    // A set-returning claim call (the Notifications store passes the claim function's name as {function}).
    [GeneratedRegex(@"FROM\s+(?:\{function\}|security\.(?:claim_[a-z_]*outbox|recover_stale_notifications_outbox))\(@worker_id,")]
    private static partial Regex ClaimCallPattern();

    [GeneratedRegex(@"TraceParent|trace_parent|OutboxTraceContext|traceparent", RegexOptions.IgnoreCase)]
    private static partial Regex TraceContextNamePattern();
}
