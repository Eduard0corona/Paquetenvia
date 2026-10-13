extern alias WorkerHost;

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Paqueteria.Infrastructure.Observability;
using Realtime.Application.Configuration;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Observability;

/// <summary>
/// OBS-003 in the real host compositions: every Worker job cycle and every outbox dispatch cycle (the Worker's
/// dispatch lane and the API's in-process realtime lanes) is a new root trace, and its 32-hex id is in the JSON
/// console lines the cycle writes. The database is unreachable, so every cycle fails and logs.
/// </summary>
public sealed class Obs003CycleTraceTests
{
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=obs003;Username=unused;Password=unused;Timeout=1;Pooling=false";

    // RTM: the API's realtime dispatcher logs in with its own role, distinct from the API and Worker logins.
    private const string UnreachableRealtimeDatabase =
        "Host=127.0.0.1;Port=1;Database=obs003;Username=unused_realtime;Password=unused;Timeout=1;Pooling=false";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Worker_job_cycles_and_dispatch_cycles_each_have_their_own_trace_id_in_their_lines()
    {
        using var logs = new JsonConsoleCapture();
        await using var worker = new CycleWorkerFactory(logs);
        _ = worker.Services;

        var jobCycles = await WaitForAsync(logs, line =>
            line.GetProperty("EventId").GetInt32() == TelemetryEvents.ScheduledJobCycle.Id &&
            line.GetProperty("State").GetProperty("Job").GetString() == "orders.claim-window-finalization");
        var dispatchCycles = await WaitForAsync(logs, line =>
            line.GetProperty("Category").GetString() == "Dispatch.Infrastructure.Lifecycle.AssignmentLifecycleDispatcher" &&
            line.GetProperty("State").GetProperty("Outcome").GetString() == "LOOP_FAILURE");

        AssertDistinctTraceIds(jobCycles);
        AssertDistinctTraceIds(dispatchCycles);
        Assert.Empty(TraceIds(jobCycles).Intersect(TraceIds(dispatchCycles), StringComparer.Ordinal));
        // The OBS-002 lane heartbeat summarizes many cycles and runs in none.
        Assert.All(
            logs.Parsed().Where(line => line.GetProperty("EventId").GetInt32() == TelemetryEvents.OutboxLaneSummary.Id),
            line => Assert.Null(JsonConsoleCapture.TraceIdOf(line)));
    }

    [Fact]
    public async Task Api_realtime_dispatch_cycles_each_have_their_own_trace_id_in_their_lines()
    {
        using var logs = new JsonConsoleCapture();
        await using var api = new RealtimeDispatcherApiFactory(logs);
        _ = api.Services;

        var business = await WaitForAsync(logs, line => IsRealtimeFailure(line, "business"));
        var location = await WaitForAsync(logs, line => IsRealtimeFailure(line, "location"));

        AssertDistinctTraceIds(business);
        AssertDistinctTraceIds(location);
        Assert.Empty(TraceIds(business).Intersect(TraceIds(location), StringComparer.Ordinal));
    }

    private static bool IsRealtimeFailure(JsonElement line, string lane) =>
        line.GetProperty("Message").GetString() is { } message &&
        message.StartsWith("realtime_outbox_message_failed", StringComparison.Ordinal) &&
        line.GetProperty("State").GetProperty("Lane").GetString() == lane;

    private static async Task<IReadOnlyList<JsonElement>> WaitForAsync(JsonConsoleCapture logs, Func<JsonElement, bool> match)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (true)
        {
            var found = logs.Parsed().Where(match).ToArray();
            if (found.Length >= 2 || DateTime.UtcNow > deadline)
            {
                Assert.True(found.Length >= 2, $"expected two cycles, saw {found.Length}");
                return found;
            }

            await Task.Delay(50);
        }
    }

    private static void AssertDistinctTraceIds(IReadOnlyList<JsonElement> lines)
    {
        var traceIds = TraceIds(lines);
        Assert.All(traceIds, traceId => Assert.True(TraceCorrelation.IsTraceId(traceId), traceId));
        Assert.Equal(traceIds.Length, traceIds.Distinct(StringComparer.Ordinal).Count());
    }

    private static string[] TraceIds(IReadOnlyList<JsonElement> lines) =>
        lines.Select(line => JsonConsoleCapture.TraceIdOf(line) ?? string.Empty).ToArray();

    private sealed class CycleWorkerFactory(JsonConsoleCapture logs) : WebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:PaqueteriaWorker"] = UnreachableDatabase,
                    ["Orders:ClaimWindowFinalization:Enabled"] = "true",
                    ["Orders:ClaimWindowFinalization:PollIntervalSeconds"] = "1",
                    ["Dispatch:AssignmentLifecycle:Provider"] = "PostgreSql",
                    ["Dispatch:AssignmentLifecycle:PollIntervalMilliseconds"] = "100",
                }));
            builder.ConfigureLogging(logs.AddTo);
        }
    }

    private sealed class RealtimeDispatcherApiFactory(JsonConsoleCapture logs) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Provider"] = "Mock",
                    ["IdentityBootstrap:Provider"] = "Mock",
                    ["Realtime:Provider"] = "SignalR",
                    ["Realtime:Backplane"] = "InProcess",
                    ["Realtime:AllowedOrigins:0"] = "https://web.synthetic.local",
                    ["Realtime:OutboxDispatcher:Provider"] = "PostgreSql",
                    ["Realtime:OutboxDispatcher:Business:PollIntervalMilliseconds"] = "100",
                    ["Realtime:OutboxDispatcher:Location:PollIntervalMilliseconds"] = "100",
                    ["ConnectionStrings:Paqueteria"] = UnreachableDatabase,
                    [$"ConnectionStrings:{OutboxDispatcherOptions.ConnectionStringName}"] = UnreachableRealtimeDatabase,
                }));
            builder.ConfigureLogging(logs.AddTo);
        }
    }
}
