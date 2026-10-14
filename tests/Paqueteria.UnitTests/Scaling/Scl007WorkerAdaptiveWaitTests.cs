using System.Collections.Concurrent;
using System.Diagnostics;
using Dispatch.Infrastructure.Lifecycle;
using Identity.Application.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.Audience;
using Notifications.Application.Dispatching;
using Notifications.Infrastructure;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Dispatching;
using Organizations.Application.Notifications;
using Paqueteria.Application.Scaling;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.UnitTests.Operations;
using Realtime.Application.Configuration;
using ManualClock = Paqueteria.UnitTests.Scaling.Scl007AdaptiveOutboxWaitTests.ManualClock;

namespace Paqueteria.UnitTests.Scaling;

/// <summary>
/// SCL-007-WORKER-ADAPTIVE-WAIT-2026-10-14: the Worker's Notifications and Dispatch assignment lifecycle lanes poll
/// again at once while their claims return work and wait longer after each empty poll, from the lane's poll interval
/// (250 ms) up to its maximum idle wait (1 s on both lanes, the owner's answer "1 segundo en ambas"). The real dispatcher
/// loops run here on a manual clock: no time passes unless a test moves it, so every wait and every claim instant is
/// exact.
/// </summary>
public sealed class Scl007WorkerAdaptiveWaitTests
{
    private const string Notifications = OutboxLanes.Notifications;
    private const string Dispatch = OutboxLanes.Dispatch;
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    public static TheoryData<string> Lanes => new() { Notifications, Dispatch };

    [Fact]
    public void The_owner_value_is_the_default_of_both_worker_lanes_with_the_realtime_ceiling()
    {
        var notifications = new NotificationsOptions();
        var dispatch = new AssignmentLifecycleOptions();

        Assert.Equal(250, notifications.PollIntervalMilliseconds);
        Assert.Equal(1_000, notifications.MaximumIdleWaitMilliseconds);
        Assert.Equal(250, dispatch.PollIntervalMilliseconds);
        Assert.Equal(1_000, dispatch.MaximumIdleWaitMilliseconds);
        Assert.Equal(1_000, OutboxIdleWait.MaximumIdleWaitCeilingMilliseconds);
        // The same ceiling as the realtime lanes (SCL-007-ADAPTIVE-OUTBOX-WAIT-2026-10-13).
        Assert.Equal(
            OutboxDispatcherOptionsValidator.MaximumIdleWaitCeilingMilliseconds,
            OutboxIdleWait.MaximumIdleWaitCeilingMilliseconds);
        Assert.True(NotificationsOptionsValidator.IsValid(notifications));
        Assert.True(AssignmentLifecycleOptions.IsValid(dispatch));
    }

    [Fact]
    public void The_worker_settings_compose_250_ms_and_one_second_on_both_lanes()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(RepositoryRoot(), "src", "Paqueteria.Worker", "appsettings.json"),
                optional: false,
                reloadOnChange: false)
            .Build();

        var notifications = Resolve<NotificationsOptions>(configuration);
        var dispatch = Resolve<AssignmentLifecycleOptions>(configuration);

        Assert.Equal(250, notifications.PollIntervalMilliseconds);
        Assert.Equal(1_000, notifications.MaximumIdleWaitMilliseconds);
        Assert.Equal(250, dispatch.PollIntervalMilliseconds);
        Assert.Equal(1_000, dispatch.MaximumIdleWaitMilliseconds);
    }

    [Theory]
    [InlineData(250, 1_000, true)]
    [InlineData(250, 250, true)]
    [InlineData(250, 600, true)]
    [InlineData(10, 1_000, true)]
    [InlineData(1_000, 1_000, true)]
    [InlineData(250, 249, false)]
    [InlineData(500, 499, false)]
    [InlineData(250, 1_001, false)]
    [InlineData(250, 2_000, false)]
    [InlineData(250, 5_000, false)]
    [InlineData(1_001, 1_001, false)]
    [InlineData(5_000, 5_000, false)]
    [InlineData(250, 0, false)]
    [InlineData(250, -1, false)]
    public void The_maximum_idle_wait_is_refused_below_the_poll_interval_or_above_one_second_on_each_lane(
        int pollIntervalMilliseconds,
        int maximumIdleWaitMilliseconds,
        bool expected)
    {
        Assert.Equal(expected, OutboxIdleWait.IsValidMaximum(pollIntervalMilliseconds, maximumIdleWaitMilliseconds));
        Assert.Equal(expected, NotificationsOptionsValidator.IsValid(new NotificationsOptions
        {
            PollIntervalMilliseconds = pollIntervalMilliseconds,
            MaximumIdleWaitMilliseconds = maximumIdleWaitMilliseconds,
        }));
        Assert.Equal(expected, AssignmentLifecycleOptions.IsValid(new AssignmentLifecycleOptions
        {
            PollIntervalMilliseconds = pollIntervalMilliseconds,
            MaximumIdleWaitMilliseconds = maximumIdleWaitMilliseconds,
        }));
    }

    [Theory]
    [InlineData("Notifications:MaximumIdleWaitMilliseconds", "5000", Notifications)]
    [InlineData("Notifications:MaximumIdleWaitMilliseconds", "200", Notifications)]
    [InlineData("Notifications:PollIntervalMilliseconds", "2000", Notifications)]
    [InlineData("Dispatch:AssignmentLifecycle:MaximumIdleWaitMilliseconds", "2000", Dispatch)]
    [InlineData("Dispatch:AssignmentLifecycle:MaximumIdleWaitMilliseconds", "0", Dispatch)]
    [InlineData("Dispatch:AssignmentLifecycle:PollIntervalMilliseconds", "1500", Dispatch)]
    public void An_out_of_bounds_maximum_idle_wait_refuses_the_start(string key, string value, string lane)
    {
        var configuration = Configuration(new() { [key] = value });

        var failure = Assert.Throws<OptionsValidationException>(() =>
        {
            _ = lane == Notifications
                ? Resolve<NotificationsOptions>(configuration)
                : (object)Resolve<AssignmentLifecycleOptions>(configuration);
        });

        Assert.Contains(
            lane == Notifications
                ? "Notifications contains an invalid bounded option."
                : "Dispatch:AssignmentLifecycle contains an invalid bounded option.",
            failure.Failures);
    }

    [Theory]
    [InlineData("Notifications:MaximumIdleWaitMilliseconds", Notifications)]
    [InlineData("Dispatch:AssignmentLifecycle:MaximumIdleWaitMilliseconds", Dispatch)]
    public void A_maximum_idle_wait_that_is_not_a_number_refuses_the_start(string key, string lane)
    {
        var configuration = Configuration(new() { [key] = "one second" });

        Assert.ThrowsAny<InvalidOperationException>(() =>
        {
            _ = lane == Notifications
                ? Resolve<NotificationsOptions>(configuration)
                : (object)Resolve<AssignmentLifecycleOptions>(configuration);
        });
    }

    [Fact]
    public void The_configured_maximum_idle_wait_binds_per_lane()
    {
        var configuration = Configuration(new() { ["Notifications:MaximumIdleWaitMilliseconds"] = "750" });

        Assert.Equal(750, Resolve<NotificationsOptions>(configuration).MaximumIdleWaitMilliseconds);
        Assert.Equal(1_000, Resolve<AssignmentLifecycleOptions>(configuration).MaximumIdleWaitMilliseconds);
    }

    [Theory]
    [MemberData(nameof(Lanes))]
    public async Task While_polls_come_back_empty_the_lane_waits_longer_each_time_up_to_one_second(string lane)
    {
        await using var run = new WorkerLanesRun();
        await run.StartAsync(lane);

        await run.Clock.StepAsync(1, 5);

        Assert.Equal([0, 250, 750, 1_750, 2_750, 3_750], run.Scripts.OffsetsMs(lane));
        // Six empty polls, each followed by its wait: 250 ms, 500 ms, then 1 s from the third on.
        Assert.Equal(
            [250, 500, 1_000, 1_000, 1_000, 1_000],
            run.Clock.Requested.Select(wait => (int)wait.TotalMilliseconds));
        if (lane == Notifications)
        {
            // Each Notifications poll claims the owned and then the unowned rows, at the same instant.
            Assert.Equal(run.Scripts.OffsetsMs(lane), run.Scripts.OffsetsMs(ClaimScripts.Unowned));
        }
    }

    [Fact]
    public async Task The_wait_never_exceeds_the_maximum_over_a_long_idle_period()
    {
        await using var run = new WorkerLanesRun();
        await run.StartAsync(Notifications, Dispatch);

        await run.Clock.RunUntilAsync(Start + TimeSpan.FromMinutes(3), loops: 2);

        Assert.NotEmpty(run.Clock.Requested);
        Assert.Equal(Second, run.Clock.Requested.Max());
        foreach (var lane in new[] { Notifications, Dispatch })
        {
            var offsets = run.Scripts.OffsetsMs(lane);
            var gaps = offsets.Zip(offsets.Skip(1), (previous, next) => next - previous).ToArray();
            Assert.All(gaps, gap => Assert.InRange(gap, 1, 1_000));
            // After the short ramp every idle poll is exactly one second apart.
            Assert.All(gaps.Skip(2), gap => Assert.Equal(1_000, gap));
        }
    }

    [Theory]
    [MemberData(nameof(Lanes))]
    public async Task A_full_batch_polls_again_at_once_and_work_starts_the_wait_over(string lane)
    {
        await using var run = new WorkerLanesRun();
        // Both lanes claim BatchSize = 10 rows at most: 10 is a full batch.
        run.Scripts.Script(lane, 0, 0, 0, 10, 10, 0, 0, 0);
        await run.StartAsync(lane);

        await run.Clock.StepAsync(1, 5);

        // Three empty polls (250, 500 and 1000 ms waits), two full batches claimed back to back at 1750 ms, the empty
        // poll right after them, then the wait starts over at 250 ms.
        Assert.Equal([0, 250, 750, 1_750, 1_750, 1_750, 2_000, 2_500], run.Scripts.OffsetsMs(lane));
        Assert.Equal(20, run.Scripts.Settled(lane));
        Assert.Equal(
            [250, 500, 1_000, 250, 500, 1_000],
            run.Clock.Requested.Select(wait => (int)wait.TotalMilliseconds));
    }

    [Theory]
    [InlineData(Notifications, Notifications, 1)]
    [InlineData(Notifications, Notifications, 9)]
    [InlineData(Notifications, ClaimScripts.Unowned, 1)]
    [InlineData(Notifications, ClaimScripts.Unowned, 3)]
    [InlineData(Dispatch, Dispatch, 1)]
    [InlineData(Dispatch, Dispatch, 9)]
    public async Task A_partial_batch_counts_as_work_and_starts_the_wait_over(string lane, string claim, int partial)
    {
        await using var run = new WorkerLanesRun();
        run.Scripts.Script(claim, 0, 0, 0, partial, 0, 0, 0);
        await run.StartAsync(lane);

        await run.Clock.StepAsync(1, 5);

        Assert.Equal([0, 250, 750, 1_750, 1_750, 2_000, 2_500], run.Scripts.OffsetsMs(lane));
        Assert.Equal(partial, run.Scripts.Settled(lane));
        Assert.Equal(
            [250, 500, 1_000, 250, 500, 1_000],
            run.Clock.Requested.Select(wait => (int)wait.TotalMilliseconds));
    }

    [Theory]
    [InlineData(Notifications, Dispatch)]
    [InlineData(Dispatch, Notifications)]
    public async Task The_lanes_keep_independent_waits(string busy, string idle)
    {
        await using var run = new WorkerLanesRun();
        run.Scripts.Script(busy, 0, 0, 4, 0);
        await run.StartAsync(Notifications, Dispatch);

        await run.Clock.RunUntilAsync(Start + TimeSpan.FromMilliseconds(3_750), loops: 2);

        Assert.Equal([0, 250, 750, 750, 1_000, 1_500, 2_500, 3_500], run.Scripts.OffsetsMs(busy));
        // Work on the busy lane at 750 ms does not restart the other lane's wait.
        Assert.Equal([0, 250, 750, 1_750, 2_750, 3_750], run.Scripts.OffsetsMs(idle));
    }

    [Theory]
    [MemberData(nameof(Lanes))]
    public async Task A_failed_iteration_keeps_its_fixed_poll_interval_wait(string lane)
    {
        await using var run = new WorkerLanesRun();
        run.Scripts.Script(lane, 0, 0, ClaimScripts.Fail, 0, 0);
        await run.StartAsync(lane);

        await run.Clock.StepAsync(1, 4);

        // 250 and 500 ms after two empty polls, the poll interval (250 ms) after the failed one; the idle progression
        // carries on (1 s) because a failure is neither work nor an empty poll.
        Assert.Equal(
            [250, 500, 250, 1_000, 1_000],
            run.Clock.Requested.Select(wait => (int)wait.TotalMilliseconds));
    }

    [Theory]
    [MemberData(nameof(Lanes))]
    public async Task Cancellation_during_a_one_second_wait_stops_the_lane_without_waiting_it_out(string lane)
    {
        await using var run = new WorkerLanesRun();
        await run.StartAsync(lane);
        await run.Clock.StepAsync(1, 2);
        await run.Clock.QuiescentAsync(1);
        Assert.Equal(Second, run.Clock.Requested[^1]);
        var claims = run.Scripts.OffsetsMs(lane).Length;
        var before = run.Clock.GetUtcNow();

        var elapsed = Stopwatch.StartNew();
        await run.Dispatchers[0].StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        elapsed.Stop();

        var execution = run.Dispatchers[0].ExecuteTask;
        Assert.NotNull(execution);
        Assert.Equal(TaskStatus.RanToCompletion, execution.Status);
        // The manual clock never moved: the wait was abandoned, not served.
        Assert.Equal(before, run.Clock.GetUtcNow());
        Assert.Equal(0, run.Clock.Pending);
        Assert.Equal(claims, run.Scripts.OffsetsMs(lane).Length);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"stop took {elapsed.Elapsed}");
    }

    [Fact]
    public async Task While_idle_each_lane_polls_about_once_a_second_and_writes_its_per_minute_heartbeat()
    {
        await using var run = new WorkerLanesRun();
        await run.StartAsync(Notifications, Dispatch);

        await run.Clock.RunUntilAsync(Start + TimeSpan.FromSeconds(61), loops: 2);

        var summaries = run.LaneLog.Entries
            .Where(entry => entry.EventId == TelemetryEvents.OutboxLaneSummary)
            .ToArray();
        foreach (var lane in new[] { Notifications, Dispatch })
        {
            var summary = Assert.Single(summaries, entry => Equals(entry.Values["Lane"], lane));
            Assert.Equal(LogLevel.Information, summary.Level);
            Assert.Equal(0L, summary.Values["Claimed"]);
            Assert.Equal(0L, summary.Values["LoopFailures"]);
            Assert.Equal(60L, summary.Values["WindowSeconds"]);

            // The first idle minute: 3 polls of ramp then one a second (before: 4 a second, about 244).
            Assert.Equal(63, run.Scripts.OffsetsMs(lane).Count(offset => offset < 61_000));
            // The stale recovery keeps its 30 s schedule, on the same clock: at the start, then at the first poll
            // 30 s after the previous recovery.
            Assert.Equal([0, 30_750, 60_750], run.Scripts.OffsetsMs(ClaimScripts.Recovery(lane)));
        }

        // A Notifications poll is two claim transactions (owned and unowned): 126 in the first idle minute, and 63 on
        // the Dispatch lane. About 3 claim transactions a second for both lanes instead of about 12.
        Assert.Equal(63, run.Scripts.OffsetsMs(ClaimScripts.Unowned).Count(offset => offset < 61_000));
    }

    [Theory]
    [MemberData(nameof(Lanes))]
    public async Task The_claim_age_of_the_lane_summary_includes_the_idle_wait(string lane)
    {
        await using var run = new WorkerLanesRun();
        // The fourth poll returns one row that became ready right after the third (empty) poll, the worst case: it
        // waited the whole one-second idle wait before the claim that took it.
        run.Scripts.Script(lane, 0, 0, 0, 1, 0);
        await run.StartAsync(lane);

        await run.Clock.RunUntilAsync(Start + TimeSpan.FromSeconds(61), loops: 1);

        var summary = Assert.Single(
            run.LaneLog.Entries,
            entry => entry.EventId == TelemetryEvents.OutboxLaneSummary);
        Assert.Equal(lane, summary.Values["Lane"]);
        Assert.Equal(1L, summary.Values["Claimed"]);
        Assert.Equal(1L, summary.Values["Dead"]);
        Assert.Equal(1_000L, summary.Values["MaxClaimAgeMs"]);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> overrides) =>
        new ConfigurationBuilder().AddInMemoryCollection(overrides).Build();

    /// <summary>The lane's options through its real registration, as the Worker composes it.</summary>
    private static TOptions Resolve<TOptions>(IConfiguration configuration)
        where TOptions : class
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddNotificationsInfrastructure(configuration);
        services.AddDispatchAssignmentLifecycleWorker(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<TOptions>>().Value;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Paqueteria.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>The two Worker lane dispatchers, as the Worker composes them, over scripted stores and a manual clock.</summary>
    private sealed class WorkerLanesRun : IAsyncDisposable
    {
        private readonly IOptions<NotificationsOptions> _notifications = Options.Create(new NotificationsOptions
        {
            Provider = NotificationsDispatcherProviderKind.PostgreSql,
        });

        private readonly IOptions<AssignmentLifecycleOptions> _dispatch = Options.Create(new AssignmentLifecycleOptions
        {
            Provider = AssignmentLifecycleProviderKind.PostgreSql,
        });

        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

        public WorkerLanesRun()
        {
            Scripts = new ClaimScripts(Clock);
            Lanes = new OutboxLaneMonitor(LaneLog, Clock);
        }

        public ManualClock Clock { get; } = new(Start);

        public ClaimScripts Scripts { get; }

        public CapturingLogger<OutboxLaneMonitor> LaneLog { get; } = new();

        public OutboxLaneMonitor Lanes { get; }

        public List<BackgroundService> Dispatchers { get; } = [];

        public async Task StartAsync(params string[] lanes)
        {
            foreach (var lane in lanes)
            {
                BackgroundService dispatcher;
                if (lane == Notifications)
                {
                    var store = new ScriptedNotificationsStore(Scripts);
                    var processor = new NotificationsOutboxProcessor(
                        store,
                        new NotificationAudienceResolver(new NoDispatchers(), new NoRecipients()),
                        new SyntheticInAppProvider(_notifications),
                        _notifications,
                        Lanes,
                        NullLogger<NotificationsOutboxProcessor>.Instance);
                    dispatcher = new NotificationsOutboxDispatcher(
                        store,
                        processor,
                        _notifications,
                        Lanes,
                        Clock,
                        NullLogger<NotificationsOutboxDispatcher>.Instance);
                }
                else if (lane == Dispatch)
                {
                    var store = new ScriptedDispatchStore(Scripts);
                    var processor = new AssignmentLifecycleProcessor(
                        store,
                        _services.GetRequiredService<IServiceScopeFactory>(),
                        _dispatch,
                        new SystemClock(),
                        Lanes,
                        NullLogger<AssignmentLifecycleProcessor>.Instance);
                    dispatcher = new AssignmentLifecycleDispatcher(
                        store,
                        processor,
                        _dispatch,
                        Lanes,
                        Clock,
                        NullLogger<AssignmentLifecycleDispatcher>.Instance);
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(lanes), lane, "Unknown lane.");
                }

                Dispatchers.Add(dispatcher);
                await dispatcher.StartAsync(CancellationToken.None);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var dispatcher in Dispatchers)
            {
                await dispatcher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                dispatcher.Dispose();
            }

            await _services.DisposeAsync();
        }
    }

    /// <summary>
    /// Answers each claim from a per-claim script of batch sizes (then empty), and records the clock at every claim and
    /// every stale recovery. Rows carry a topic no lane knows, so the real processors settle each one DEAD through the
    /// scripted stores at once; each row became ready when the previous claim of the same kind returned, so its claim
    /// age is the wait that preceded the claim.
    /// </summary>
    internal sealed class ClaimScripts(ManualClock clock)
    {
        public const int Fail = -1;

        /// <summary>The Notifications claim of unowned rows; the owned claim is keyed by the lane name.</summary>
        public const string Unowned = "notifications_unowned";

        private readonly ConcurrentDictionary<string, ConcurrentQueue<int>> _scripts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _calls = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _settled = new(StringComparer.Ordinal);

        public static string Recovery(string lane) => lane + "_recovery";

        public void Script(string claim, params int[] batches) =>
            _scripts[claim] = new ConcurrentQueue<int>(batches);

        public int[] OffsetsMs(string key) =>
            Calls(key).Select(at => (int)(at - Start).TotalMilliseconds).ToArray();

        public int Settled(string lane) => _settled.GetValueOrDefault(lane);

        public void Record(string key) => Calls(key).Enqueue(clock.GetUtcNow());

        public void Settle(string lane) => _settled.AddOrUpdate(lane, 1, static (_, value) => value + 1);

        public (DateTimeOffset Now, DateTimeOffset ReadyAt, int Count) Next(string claim)
        {
            var now = clock.GetUtcNow();
            var calls = Calls(claim);
            var readyAt = calls.IsEmpty ? now : calls.Last();
            calls.Enqueue(now);
            var count = _scripts.TryGetValue(claim, out var script) && script.TryDequeue(out var next) ? next : 0;
            if (count == Fail)
            {
                throw new TimeoutException("Synthetic claim failure.");
            }

            return (now, readyAt, count);
        }

        private ConcurrentQueue<DateTimeOffset> Calls(string key) =>
            _calls.GetOrAdd(key, static _ => new ConcurrentQueue<DateTimeOffset>());
    }

    private sealed class ScriptedNotificationsStore(ClaimScripts scripts) : INotificationsStore
    {
        private static readonly Guid Organization = Guid.Parse("00000000-0000-0000-0000-000000000914");

        public Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> ClaimNotificationsAsync(
            string workerId,
            int batchSize,
            TimeSpan lease,
            CancellationToken cancellationToken) =>
            Claim(Notifications, batchSize, lease);

        public Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> ClaimUnownedAsync(
            string workerId,
            int batchSize,
            TimeSpan lease,
            CancellationToken cancellationToken) =>
            Claim(ClaimScripts.Unowned, batchSize, lease);

        public Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> RecoverStaleAsync(
            string workerId,
            int batchSize,
            int maximumAttempts,
            TimeSpan lease,
            CancellationToken cancellationToken)
        {
            scripts.Record(ClaimScripts.Recovery(Notifications));
            return Task.FromResult<IReadOnlyList<ClaimedNotificationOutboxMessage>>([]);
        }

        public Task<int> RecoverStaleUnownedAsync(
            int batchSize,
            int maximumAttempts,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<bool> SettleAsync(
            Guid id,
            Guid leaseToken,
            string status,
            string? errorCode,
            DateTimeOffset? availableAt,
            CancellationToken cancellationToken)
        {
            Assert.Equal("DEAD", status);
            Assert.Equal(NotificationErrorCodes.UnknownTopic, errorCode);
            scripts.Settle(Notifications);
            return Task.FromResult(true);
        }

        public Task<string> ExpandSourceAsync(
            ClaimedNotificationOutboxMessage source,
            ParsedOrderCreated parsed,
            IReadOnlyList<Guid> recipients,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No orders.created row is scripted.");

        public Task<NotificationDelivery?> ReadDeliveryAsync(
            Guid notificationId,
            Guid ownerOrganizationId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No send request is scripted.");

        public Task<bool> ApplyOutcomeAsync(
            ClaimedNotificationOutboxMessage request,
            NotificationDelivery delivery,
            string outcome,
            string code,
            DateTimeOffset attemptedAt,
            DateTimeOffset? availableAt,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No send request is scripted.");

        public Task<bool> FinalizeMaximumAttemptsAsync(
            ClaimedNotificationOutboxMessage request,
            NotificationDelivery delivery,
            DateTimeOffset finalizedAt,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No exhausted row is scripted.");

        public Task<string?> ResolveConsumerAsync(string topic, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("NOTIFICATIONS");

        private Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> Claim(string claim, int batchSize, TimeSpan lease)
        {
            Assert.Equal(10, batchSize);
            var (now, readyAt, count) = scripts.Next(claim);
            return Task.FromResult<IReadOnlyList<ClaimedNotificationOutboxMessage>>(Enumerable.Range(0, count)
                .Select(_ => new ClaimedNotificationOutboxMessage(
                    Guid.NewGuid(),
                    Organization,
                    "{}",
                    "notifications.scl007-unknown",
                    "Notification",
                    Guid.NewGuid(),
                    1,
                    "{}",
                    1,
                    Guid.NewGuid(),
                    now + lease,
                    readyAt,
                    readyAt))
                .ToArray());
        }
    }

    private sealed class ScriptedDispatchStore(ClaimScripts scripts) : IDispatchOutboxStore
    {
        private static readonly Guid Organization = Guid.Parse("00000000-0000-0000-0000-000000000915");

        public Task<IReadOnlyList<ClaimedDispatchOutboxMessage>> ClaimAsync(
            string workerId,
            int batchSize,
            TimeSpan lease,
            CancellationToken cancellationToken)
        {
            Assert.Equal(10, batchSize);
            var (now, readyAt, count) = scripts.Next(Dispatch);
            return Task.FromResult<IReadOnlyList<ClaimedDispatchOutboxMessage>>(Enumerable.Range(0, count)
                .Select(_ => new ClaimedDispatchOutboxMessage(
                    Guid.NewGuid(),
                    Organization,
                    "dispatch.scl007-unknown",
                    "Order",
                    Guid.NewGuid(),
                    1,
                    "{}",
                    1,
                    Guid.NewGuid(),
                    now + lease,
                    readyAt))
                .ToArray());
        }

        public Task<int> RequeueStaleAsync(int batchSize, int maximumAttempts, CancellationToken cancellationToken)
        {
            scripts.Record(ClaimScripts.Recovery(Dispatch));
            return Task.FromResult(0);
        }

        public Task<bool> SettleAsync(
            Guid id,
            Guid leaseToken,
            string status,
            string? errorCode,
            DateTimeOffset? availableAt,
            CancellationToken cancellationToken)
        {
            Assert.Equal("DEAD", status);
            Assert.Equal(AssignmentLifecycleErrorCodes.InvalidPayload, errorCode);
            scripts.Settle(Dispatch);
            return Task.FromResult(true);
        }

        public Task<string?> ResolveConsumerAsync(string topic, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("DISPATCH");
    }

    private sealed class NoDispatchers : IOwnerOrganizationDispatcherReader
    {
        public Task<IReadOnlyList<Guid>> ReadActiveDispatcherUserIdsAsync(
            Guid ownerOrganizationId,
            int limit,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No orders.created row is scripted.");
    }

    private sealed class NoRecipients : IActiveNotificationRecipientReader
    {
        public Task<IReadOnlyList<Guid>> ReadActiveUserIdsAsync(
            IReadOnlyCollection<Guid> requestedUserIds,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No orders.created row is scripted.");
    }
}
