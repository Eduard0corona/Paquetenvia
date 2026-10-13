using System.Text.RegularExpressions;
using Dispatch.Infrastructure.Persistence.Migrations;
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
        // Every function body, in every migration, that inserts into a platform outbox table.
        var writers = Directory.EnumerateFiles(TestRepository.GetPath("src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => FunctionsWritingOutbox(File.ReadAllText(file)))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(SqlProducers.Keys.Order(StringComparer.Ordinal), writers.Order(StringComparer.Ordinal));
        var schema = File.ReadAllText(TestRepository.GetPath("docs/normative/v0.6/database/AI-06_SCHEMA.sql"));
        Assert.Empty(OutboxInserts(schema));

        // The current definer writes the caller's context; the Notifications writers copy the originating row's.
        Assert.Contains("last_error,created_at,processed_at,trace_parent)", AddOutboxTraceContext.OutboxFunctionSql, StringComparison.Ordinal);
        var notifications = File.ReadAllText(TestRepository.GetPath(SqlProducers["security.emit_notification_status_changed"]));
        Assert.Equal(2, Regex.Count(notifications, Regex.Escape("payload,priority,status,attempts,available_at,created_at,trace_parent)")));
        Assert.Contains("p_occurred_at,p_occurred_at,v_trace_parent);", notifications, StringComparison.Ordinal);
        Assert.Contains("p_occurred_at,p_occurred_at,v_source.trace_parent);", notifications, StringComparison.Ordinal);

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
        // Only the consumers (as the parent of the message's activity) and the EF column mapping read the member; the
        // stores fill it from the claimed row through OutboxTraceContextColumn.Read.
        var readers = RuntimeSources()
            .Where(file => CodeWithoutComments(file).Contains(".TraceParent", StringComparison.Ordinal))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            Consumers.Keys.Append(OutboxModel).Order(StringComparer.Ordinal),
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

    private static IEnumerable<string> FunctionsWritingOutbox(string source)
    {
        foreach (Match insert in OutboxInsertPattern().Matches(source))
        {
            var function = FunctionHeaderPattern().Matches(source[..insert.Index]).LastOrDefault();
            Assert.True(function is not null, "an outbox INSERT in a migration must be inside a function body");
            yield return function!.Groups["name"].Value;
        }
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

    [GeneratedRegex(@"INSERT\s+INTO\s+platform\.(?:location_)?outbox_events\b", RegexOptions.IgnoreCase)]
    private static partial Regex OutboxInsertPattern();

    [GeneratedRegex(@"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?<name>[a-z_]+\.[a-z_]+)\(", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionHeaderPattern();

    // A set-returning claim call (the Notifications store passes the claim function's name as {function}).
    [GeneratedRegex(@"FROM\s+(?:\{function\}|security\.(?:claim_[a-z_]*outbox|recover_stale_notifications_outbox))\(@worker_id,")]
    private static partial Regex ClaimCallPattern();

    [GeneratedRegex(@"TraceParent|trace_parent|OutboxTraceContext|traceparent", RegexOptions.IgnoreCase)]
    private static partial Regex TraceContextNamePattern();
}
