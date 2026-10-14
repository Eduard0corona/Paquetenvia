using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orders.Application.Tracking;
using Paqueteria.Application.Scaling;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.UnitTests.Operations;
using Realtime.Application.Configuration;
using Realtime.Application.Dispatching;
using Realtime.Infrastructure;
using Realtime.Infrastructure.Dispatching;
using Realtime.Infrastructure.Publishing;

namespace Paqueteria.UnitTests.Scaling;

/// <summary>
/// SCL-007-ADAPTIVE-OUTBOX-WAIT-2026-10-13: the realtime outbox lanes claim again at once while claims return work and
/// wait longer after each empty claim, from the lane's poll interval up to its maximum idle wait (1 s on both lanes, the
/// owner's answer). The dispatcher loops run here on a manual clock: no time passes unless a test moves it, so every
/// wait and every claim instant is exact.
/// </summary>
public sealed class Scl007AdaptiveOutboxWaitTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public void The_owner_value_is_the_default_of_both_lanes_and_the_ceiling_is_half_the_ai15_delivery_budget()
    {
        var options = new OutboxDispatcherOptions();

        Assert.Equal(250, options.Business.PollIntervalMilliseconds);
        Assert.Equal(1_000, options.Business.MaximumIdleWaitMilliseconds);
        Assert.Equal(500, options.Location.PollIntervalMilliseconds);
        Assert.Equal(1_000, options.Location.MaximumIdleWaitMilliseconds);
        // AI-15 event_delivery_p95_seconds warning: 2 s. Half of it may be spent waiting; the other half is kept for
        // the claim, the evidence reads, the publish and the settle.
        Assert.Equal(2_000, OutboxDispatcherOptionsValidator.EventDeliveryBudgetMilliseconds);
        Assert.Equal(1_000, OutboxDispatcherOptionsValidator.MaximumIdleWaitCeilingMilliseconds);
        Assert.True(OutboxDispatcherOptionsValidator.IsValid(options));
    }

    [Theory]
    [InlineData(250, 1_000, true)]
    [InlineData(250, 250, true)]
    [InlineData(250, 600, true)]
    [InlineData(50, 1_000, true)]
    [InlineData(1_000, 1_000, true)]
    [InlineData(250, 249, false)]
    [InlineData(500, 499, false)]
    [InlineData(250, 1_001, false)]
    [InlineData(250, 2_000, false)]
    [InlineData(250, 5_000, false)]
    [InlineData(1_001, 1_001, false)]
    [InlineData(250, 0, false)]
    [InlineData(250, -1, false)]
    public void The_maximum_idle_wait_is_refused_below_the_poll_interval_or_above_the_ceiling_on_each_lane(
        int pollIntervalMilliseconds,
        int maximumIdleWaitMilliseconds,
        bool expected)
    {
        foreach (var businessLane in new[] { true, false })
        {
            var options = new OutboxDispatcherOptions();
            var lane = businessLane ? options.Business : options.Location;
            lane.PollIntervalMilliseconds = pollIntervalMilliseconds;
            lane.MaximumIdleWaitMilliseconds = maximumIdleWaitMilliseconds;

            Assert.Equal(expected, OutboxDispatcherOptionsValidator.IsValid(options));
        }
    }

    [Theory]
    [InlineData("Realtime:OutboxDispatcher:Business:MaximumIdleWaitMilliseconds", "2000")]
    [InlineData("Realtime:OutboxDispatcher:Location:MaximumIdleWaitMilliseconds", "5000")]
    [InlineData("Realtime:OutboxDispatcher:Business:MaximumIdleWaitMilliseconds", "200")]
    [InlineData("Realtime:OutboxDispatcher:Location:MaximumIdleWaitMilliseconds", "0")]
    public void An_out_of_bounds_maximum_idle_wait_refuses_the_start(string key, string value)
    {
        var failure = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));

        Assert.Contains("Realtime:OutboxDispatcher contains an invalid bounded option.", failure.Failures);
    }

    [Fact]
    public void A_maximum_idle_wait_that_is_not_a_number_refuses_the_start()
    {
        Assert.ThrowsAny<InvalidOperationException>(() => Resolve(new()
        {
            ["Realtime:OutboxDispatcher:Business:MaximumIdleWaitMilliseconds"] = "one second",
        }));
    }

    [Fact]
    public void The_configured_maximum_idle_wait_binds_per_lane()
    {
        var options = Resolve(new()
        {
            ["Realtime:OutboxDispatcher:Location:MaximumIdleWaitMilliseconds"] = "750",
        });

        Assert.Equal(1_000, options.Business.MaximumIdleWaitMilliseconds);
        Assert.Equal(750, options.Location.MaximumIdleWaitMilliseconds);
    }

    [Theory]
    [InlineData(250, 1_000, new[] { 250, 500, 1_000, 1_000, 1_000 })]
    [InlineData(500, 1_000, new[] { 500, 1_000, 1_000, 1_000, 1_000 })]
    [InlineData(50, 1_000, new[] { 50, 100, 200, 400, 800, 1_000, 1_000 })]
    [InlineData(300, 1_000, new[] { 300, 600, 1_000, 1_000 })]
    [InlineData(250, 250, new[] { 250, 250, 250 })]
    public void The_wait_doubles_from_the_poll_interval_up_to_the_maximum(
        int pollIntervalMilliseconds,
        int maximumIdleWaitMilliseconds,
        int[] expected)
    {
        var wait = new OutboxIdleWait(pollIntervalMilliseconds, maximumIdleWaitMilliseconds);

        var observed = expected.Select(_ => (int)wait.After(0).TotalMilliseconds).ToArray();

        Assert.Equal(expected, observed);
        Assert.Equal(TimeSpan.Zero, wait.After(1));
        Assert.Equal(TimeSpan.FromMilliseconds(pollIntervalMilliseconds), wait.After(0));
    }

    [Fact]
    public void The_wait_refuses_a_lane_the_validator_would_refuse()
    {
        var business = new OutboxDispatcherOptions().Business;
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboxIdleWait(500, 250));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboxIdleWait(0, 1_000));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OutboxIdleWait(business.PollIntervalMilliseconds, business.MaximumIdleWaitMilliseconds).After(-1));
    }

    [Theory]
    [InlineData("business", new[] { 0, 250, 750, 1_750, 2_750, 3_750 })]
    [InlineData("location", new[] { 0, 500, 1_500, 2_500, 3_500, 4_500 })]
    public async Task While_claims_come_back_empty_the_lane_waits_longer_each_time_up_to_one_second(
        string lane,
        int[] expectedClaimOffsetsMs)
    {
        await using var run = new DispatcherRun();
        await run.StartAsync(lane);

        await run.Clock.StepAsync(1, expectedClaimOffsetsMs.Length - 1);

        Assert.Equal(expectedClaimOffsetsMs, run.Store.ClaimOffsetsMs(lane, Start));
        Assert.All(run.Clock.Requested, wait => Assert.True(wait <= Second, $"waited {wait}"));
    }

    [Fact]
    public async Task The_wait_never_exceeds_the_maximum_over_a_long_idle_period()
    {
        await using var run = new DispatcherRun();
        await run.StartAsync("business", "location");

        await run.Clock.RunUntilAsync(Start + TimeSpan.FromMinutes(3), loops: 2);

        Assert.NotEmpty(run.Clock.Requested);
        Assert.Equal(Second, run.Clock.Requested.Max());
        foreach (var lane in new[] { "business", "location" })
        {
            var offsets = run.Store.ClaimOffsetsMs(lane, Start);
            var gaps = offsets.Zip(offsets.Skip(1), (previous, next) => next - previous).ToArray();
            Assert.All(gaps, gap => Assert.InRange(gap, 1, 1_000));
            // After the short ramp every idle claim is exactly one second apart.
            Assert.All(gaps.Skip(2), gap => Assert.Equal(1_000, gap));
        }
    }

    [Fact]
    public async Task A_full_batch_claims_again_at_once_and_work_starts_the_wait_over()
    {
        await using var run = new DispatcherRun();
        // Business claims at most min(BatchSize 10, MaximumConcurrency 4) = 4 rows: 4 is a full batch.
        run.Store.Script("business", 0, 0, 0, 4, 4, 0, 0, 0);
        await run.StartAsync("business");

        await run.Clock.StepAsync(1, 5);

        // Three empty claims (250, 500 and 1000 ms waits), two full batches claimed back to back at 1750 ms, the empty
        // claim right after them, then the wait starts over at 250 ms.
        Assert.Equal(
            [0, 250, 750, 1_750, 1_750, 1_750, 2_000, 2_500],
            run.Store.ClaimOffsetsMs("business", Start));
        Assert.Equal(8, run.Store.Settled("business"));
        Assert.Equal(
            [250, 500, 1_000, 250, 500, 1_000],
            run.Clock.Requested.Select(wait => (int)wait.TotalMilliseconds));
    }

    [Theory]
    [InlineData("business", 1, new[] { 0, 250, 750, 1_750, 1_750, 2_000, 2_500 })]
    [InlineData("business", 3, new[] { 0, 250, 750, 1_750, 1_750, 2_000, 2_500 })]
    [InlineData("location", 1, new[] { 0, 500, 1_500, 2_500, 2_500, 3_000, 4_000 })]
    [InlineData("location", 7, new[] { 0, 500, 1_500, 2_500, 2_500, 3_000, 4_000 })]
    public async Task A_partial_batch_counts_as_work_and_starts_the_wait_over(
        string lane,
        int partial,
        int[] expectedClaimOffsetsMs)
    {
        await using var run = new DispatcherRun();
        run.Store.Script(lane, 0, 0, 0, partial, 0, 0, 0);
        await run.StartAsync(lane);

        await run.Clock.StepAsync(1, 5);

        Assert.Equal(expectedClaimOffsetsMs, run.Store.ClaimOffsetsMs(lane, Start));
        Assert.Equal(partial, run.Store.Settled(lane));
    }

    [Fact]
    public async Task The_lanes_keep_independent_waits()
    {
        await using (var businessBusy = new DispatcherRun())
        {
            businessBusy.Store.Script("business", 0, 0, 4, 0);
            await businessBusy.StartAsync("business", "location");

            await businessBusy.Clock.RunUntilAsync(Start + TimeSpan.FromMilliseconds(3_500), loops: 2);

            Assert.Equal(
                [0, 250, 750, 750, 1_000, 1_500, 2_500, 3_500],
                businessBusy.Store.ClaimOffsetsMs("business", Start));
            // Work on the business lane at 750 ms does not restart the location lane's wait.
            Assert.Equal(
                [0, 500, 1_500, 2_500, 3_500],
                businessBusy.Store.ClaimOffsetsMs("location", Start));
        }

        await using var locationBusy = new DispatcherRun();
        locationBusy.Store.Script("location", 0, 0, 8, 0);
        await locationBusy.StartAsync("business", "location");

        await locationBusy.Clock.RunUntilAsync(Start + TimeSpan.FromMilliseconds(3_750), loops: 2);

        Assert.Equal(
            [0, 500, 1_500, 1_500, 2_000, 3_000],
            locationBusy.Store.ClaimOffsetsMs("location", Start));
        Assert.Equal(
            [0, 250, 750, 1_750, 2_750, 3_750],
            locationBusy.Store.ClaimOffsetsMs("business", Start));
    }

    [Fact]
    public async Task A_failed_iteration_keeps_its_fixed_poll_interval_wait()
    {
        await using var run = new DispatcherRun();
        run.Store.Script("business", 0, 0, ScriptedRealtimeOutboxStore.Fail, 0, 0);
        await run.StartAsync("business");

        await run.Clock.StepAsync(1, 4);

        // 250 and 500 ms after two empty claims, the poll interval (250 ms) after the failed one; the idle progression
        // carries on (1 s) because a failure is neither work nor an empty claim.
        Assert.Equal(
            [250, 500, 250, 1_000, 1_000],
            run.Clock.Requested.Select(wait => (int)wait.TotalMilliseconds));
    }

    [Fact]
    public async Task Cancellation_during_a_one_second_wait_stops_the_lane_without_waiting_it_out()
    {
        await using var run = new DispatcherRun();
        await run.StartAsync("business");
        await run.Clock.StepAsync(1, 2);
        await run.Clock.QuiescentAsync(1);
        Assert.Equal(Second, run.Clock.Requested[^1]);
        var claims = run.Store.ClaimOffsetsMs("business", Start).Length;
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
        Assert.Equal(claims, run.Store.ClaimOffsetsMs("business", Start).Length);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"stop took {elapsed.Elapsed}");
    }

    [Fact]
    public async Task While_idle_each_lane_claims_about_once_a_second_and_writes_its_per_minute_heartbeat()
    {
        await using var run = new DispatcherRun();
        await run.StartAsync("business", "location");

        await run.Clock.RunUntilAsync(Start + TimeSpan.FromSeconds(61), loops: 2);

        var summaries = run.LaneLog.Entries
            .Where(entry => entry.EventId == TelemetryEvents.OutboxLaneSummary)
            .ToArray();
        foreach (var lane in new[] { OutboxLanes.RealtimeBusiness, OutboxLanes.RealtimeLocation })
        {
            var summary = Assert.Single(summaries, entry => Equals(entry.Values["Lane"], lane));
            Assert.Equal(LogLevel.Information, summary.Level);
            Assert.Equal(0L, summary.Values["Claimed"]);
            Assert.Equal(0L, summary.Values["LoopFailures"]);
            Assert.Equal(60L, summary.Values["WindowSeconds"]);
        }

        // The first idle minute: 3 claims of ramp then one a second on business (before: 4 a second), 2 then one a
        // second on location (before: 2 a second). About 2 claims a second for both lanes instead of about 6.
        Assert.Equal(63, run.Store.ClaimOffsetsMs("business", Start).Count(offset => offset < 61_000));
        Assert.Equal(62, run.Store.ClaimOffsetsMs("location", Start).Count(offset => offset < 61_000));
    }

    [Fact]
    public async Task The_claim_age_of_the_lane_summary_includes_the_idle_wait()
    {
        await using var run = new DispatcherRun();
        // The fourth claim returns one row that became ready right after the third (empty) claim, the worst case: it
        // waited the whole one-second idle wait before the claim that took it.
        run.Store.Script("business", 0, 0, 0, 1, 0);
        await run.StartAsync("business");

        await run.Clock.RunUntilAsync(Start + TimeSpan.FromSeconds(61), loops: 1);

        var summary = Assert.Single(
            run.LaneLog.Entries,
            entry => entry.EventId == TelemetryEvents.OutboxLaneSummary);
        Assert.Equal(OutboxLanes.RealtimeBusiness, summary.Values["Lane"]);
        Assert.Equal(1L, summary.Values["Claimed"]);
        Assert.Equal(1_000L, summary.Values["MaxClaimAgeMs"]);
    }

    [Fact]
    public async Task The_batch_duration_measures_the_claim_and_never_the_wait_after_it()
    {
        var recorded = new ConcurrentQueue<(string? Lane, double Milliseconds)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Paquetenvia.Realtime.Outbox" &&
                    instrument.Name == "realtime.outbox.batch_duration")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string? lane = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "lane")
                {
                    lane = tag.Value as string;
                }
            }

            recorded.Enqueue((lane, value));
        });
        listener.Start();
        await using var run = new DispatcherRun();
        // Each claim takes 5 ms; the waits after the empty ones are 250, 500 and 1000 ms, then 250 ms after the work.
        run.Store.ClaimCost = TimeSpan.FromMilliseconds(5);
        run.Store.Script("business", 0, 0, 0, 1, 0);
        await run.StartAsync("business");

        await run.Clock.StepAsync(1, 3);

        Assert.Equal(
            [250, 500, 1_000, 250],
            run.Clock.Requested.Select(wait => (int)wait.TotalMilliseconds));
        // Five iterations: four empty claims and one claim with a row. Each records its claim (5 ms), never its wait.
        Assert.Equal(5, recorded.Count);
        Assert.All(recorded, measurement =>
        {
            Assert.Equal("business", measurement.Lane);
            Assert.Equal(5d, measurement.Milliseconds);
        });
    }

    private static OutboxDispatcherOptions Resolve(Dictionary<string, string?> overrides)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Realtime:Provider"] = "SignalR",
            ["Realtime:Backplane"] = "InProcess",
            ["Realtime:OutboxDispatcher:Provider"] = "PostgreSql",
            ["ConnectionStrings:PaqueteriaRealtime"] =
                "Host=127.0.0.1;Port=1;Database=paqueteria;Username=scl007_realtime;Password=unused",
        };
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddRealtimeOutboxDispatchers(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<OutboxDispatcherOptions>>().Value;
    }

    /// <summary>The two realtime dispatchers, as the API composes them, over a scripted store and a manual clock.</summary>
    private sealed class DispatcherRun : IAsyncDisposable
    {
        private readonly RealtimeOutboxTelemetry _telemetry;
        private readonly IOptions<OutboxDispatcherOptions> _options = Options.Create(new OutboxDispatcherOptions
        {
            Provider = OutboxDispatcherProviderKind.PostgreSql,
        });

        public DispatcherRun()
        {
            _telemetry = new RealtimeOutboxTelemetry(Clock);
            Store = new ScriptedRealtimeOutboxStore(Clock);
            Lanes = new OutboxLaneMonitor(LaneLog, Clock);
        }

        public ManualClock Clock { get; } = new(Start);

        public ScriptedRealtimeOutboxStore Store { get; }

        public CapturingLogger<OutboxLaneMonitor> LaneLog { get; } = new();

        public OutboxLaneMonitor Lanes { get; }

        public List<BackgroundService> Dispatchers { get; } = [];

        public async Task StartAsync(params string[] lanes)
        {
            var processor = new RealtimeOutboxProcessor(
                Store,
                UnusedEvidenceReader.Create(),
                new DisabledRealtimePublisher(),
                new NoOpRealtimeOutboxFailureInjector(),
                new PublicOrderStatusPolicy(),
                _options,
                _telemetry,
                Lanes);
            foreach (var lane in lanes)
            {
                BackgroundService dispatcher = lane switch
                {
                    "business" => new BusinessOutboxDispatcher(
                        Store,
                        processor,
                        _telemetry,
                        _options,
                        Lanes,
                        Clock,
                        NullLogger<BusinessOutboxDispatcher>.Instance),
                    "location" => new LocationOutboxDispatcher(
                        Store,
                        processor,
                        _telemetry,
                        _options,
                        Lanes,
                        Clock,
                        NullLogger<LocationOutboxDispatcher>.Instance),
                    _ => throw new ArgumentOutOfRangeException(nameof(lanes), lane, "Unknown lane."),
                };
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

            _telemetry.Dispose();
        }
    }

    /// <summary>
    /// A clock that only moves when a test moves it. <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
    /// creates its timers here, so the test sees every wait a loop asks for and decides when it elapses.
    /// </summary>
    internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly List<TimeSpan> _requested = [];
        private DateTimeOffset _now = start;

        /// <summary>Every wait asked for, in order.</summary>
        public IReadOnlyList<TimeSpan> Requested
        {
            get
            {
                lock (_gate)
                {
                    return _requested.ToArray();
                }
            }
        }

        /// <summary>Waits not yet elapsed or abandoned.</summary>
        public int Pending
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Count;
                }
            }
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        /// <summary>Durations measured on this clock pass only when a test moves it.</summary>
        public override long GetTimestamp() => GetUtcNow().UtcTicks;

        /// <summary>Moves the clock without elapsing a wait: the time a claim takes while its loop is not waiting.</summary>
        public void Spend(TimeSpan duration)
        {
            lock (_gate)
            {
                if (_timers.Any(timer => timer.DueAt <= _now + duration))
                {
                    throw new InvalidOperationException("Spending this long would skip a pending wait.");
                }

                _now += duration;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>Waits until every loop is in a wait, then elapses the earliest one, <paramref name="steps"/> times.</summary>
        public async Task StepAsync(int loops, int steps)
        {
            for (var step = 0; step < steps; step++)
            {
                await QuiescentAsync(loops);
                List<ManualTimer> due;
                lock (_gate)
                {
                    _now = _timers.Min(timer => timer.DueAt);
                    due = _timers.Where(timer => timer.DueAt <= _now).ToList();
                    _timers.RemoveAll(due.Contains);
                }

                foreach (var timer in due)
                {
                    timer.Fire();
                }
            }

            await QuiescentAsync(loops);
        }

        public async Task RunUntilAsync(DateTimeOffset end, int loops)
        {
            while (GetUtcNow() < end)
            {
                await StepAsync(loops, 1);
            }
        }

        /// <summary>Returns once exactly <paramref name="loops"/> waits are pending: every loop has finished its claim.</summary>
        public async Task QuiescentAsync(int loops)
        {
            var deadline = Stopwatch.StartNew();
            while (Pending != loops)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                {
                    throw new TimeoutException($"Expected {loops} pending waits, found {Pending}.");
                }

                await Task.Delay(1);
            }
        }

        internal sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan _period = Timeout.InfiniteTimeSpan;

            public DateTimeOffset DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    clock._timers.Remove(this);
                    _period = period;
                    if (dueTime == Timeout.InfiniteTimeSpan)
                    {
                        return true;
                    }

                    DueAt = clock._now + dueTime;
                    clock._timers.Add(this);
                    clock._requested.Add(dueTime);
                    return true;
                }
            }

            public void Fire()
            {
                callback(state);
                if (_period != Timeout.InfiniteTimeSpan && _period > TimeSpan.Zero)
                {
                    Change(_period, _period);
                }
            }

            public void Dispose()
            {
                lock (clock._gate)
                {
                    clock._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Answers each claim from a per-lane script of batch sizes (then empty), and records the clock at every claim.
    /// Rows carry a topic no lane knows, so the real processor settles each one DEAD through this store at once; each
    /// row became ready when the lane's previous claim returned, so its claim age is the wait that preceded the claim.
    /// </summary>
    internal sealed class ScriptedRealtimeOutboxStore(ManualClock clock) : IRealtimeOutboxStore
    {
        public const int Fail = -1;

        private static readonly Guid Organization = Guid.Parse("00000000-0000-0000-0000-000000000907");
        private readonly ConcurrentDictionary<string, ConcurrentQueue<int>> _scripts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _claims = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _settled = new(StringComparer.Ordinal);

        public void Script(string lane, params int[] batches) =>
            _scripts[lane] = new ConcurrentQueue<int>(batches);

        public int[] ClaimOffsetsMs(string lane, DateTimeOffset start) =>
            Claims(lane).Select(at => (int)(at - start).TotalMilliseconds).ToArray();

        public int Settled(string lane) => _settled.GetValueOrDefault(lane);

        /// <summary>How long each claim takes on the manual clock (zero unless a test sets it).</summary>
        public TimeSpan ClaimCost { get; set; }

        public Task<IReadOnlyList<ClaimedBusinessOutboxMessage>> ClaimBusinessAsync(
            string workerId,
            int batchSize,
            TimeSpan lease,
            CancellationToken cancellationToken)
        {
            Assert.Equal(4, batchSize);
            var (now, readyAt, count) = Next("business");
            return Task.FromResult<IReadOnlyList<ClaimedBusinessOutboxMessage>>(Enumerable.Range(0, count)
                .Select(_ => new ClaimedBusinessOutboxMessage(
                    Guid.NewGuid(),
                    Organization,
                    "{}",
                    "orders.scl007-unknown",
                    "order",
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

        public Task<IReadOnlyList<ClaimedLocationOutboxMessage>> ClaimLocationAsync(
            string workerId,
            int batchSize,
            TimeSpan lease,
            CancellationToken cancellationToken)
        {
            Assert.Equal(8, batchSize);
            var (now, readyAt, count) = Next("location");
            return Task.FromResult<IReadOnlyList<ClaimedLocationOutboxMessage>>(Enumerable.Range(0, count)
                .Select(_ => new ClaimedLocationOutboxMessage(
                    Guid.NewGuid(),
                    Organization,
                    Guid.NewGuid(),
                    "drivers.scl007-unknown",
                    "{}",
                    1,
                    Guid.NewGuid(),
                    now + lease,
                    readyAt,
                    readyAt))
                .ToArray());
        }

        public Task<bool> SettleBusinessAsync(
            Guid id,
            Guid leaseToken,
            string status,
            string? errorCode,
            DateTimeOffset? availableAt,
            CancellationToken cancellationToken) =>
            Settle("business", status, errorCode);

        public Task<bool> SettleLocationAsync(
            Guid id,
            Guid leaseToken,
            string status,
            string? errorCode,
            DateTimeOffset? availableAt,
            CancellationToken cancellationToken) =>
            Settle("location", status, errorCode);

        public Task<int> RequeueStaleBusinessAsync(
            int batchSize,
            int maximumAttempts,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<int> RequeueStaleLocationAsync(
            int batchSize,
            int maximumAttempts,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        private ConcurrentQueue<DateTimeOffset> Claims(string lane) =>
            _claims.GetOrAdd(lane, static _ => new ConcurrentQueue<DateTimeOffset>());

        private (DateTimeOffset Now, DateTimeOffset ReadyAt, int Count) Next(string lane)
        {
            if (ClaimCost > TimeSpan.Zero)
            {
                clock.Spend(ClaimCost);
            }

            var now = clock.GetUtcNow();
            var claims = Claims(lane);
            var readyAt = claims.IsEmpty ? now : claims.Last();
            claims.Enqueue(now);
            var count = _scripts.TryGetValue(lane, out var script) && script.TryDequeue(out var next) ? next : 0;
            if (count == Fail)
            {
                throw new TimeoutException("Synthetic claim failure.");
            }

            return (now, readyAt, count);
        }

        private Task<bool> Settle(string lane, string status, string? errorCode)
        {
            Assert.Equal("DEAD", status);
            Assert.Equal(RealtimeOutboxErrorCodes.UnknownTopic, errorCode);
            _settled.AddOrUpdate(lane, 1, static (_, value) => value + 1);
            return Task.FromResult(true);
        }
    }

    /// <summary>The scripted rows fail on their topic before any evidence is read; any read is a test failure.</summary>
    public class UnusedEvidenceReader : DispatchProxy
    {
        public static IRealtimeOutboxEvidenceReader Create() =>
            Create<IRealtimeOutboxEvidenceReader, UnusedEvidenceReader>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected evidence read: {targetMethod?.Name}.");
    }
}
