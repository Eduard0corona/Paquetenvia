using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dispatch.Infrastructure.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Notifications.Application.Audience;
using Notifications.Application.Dispatching;
using Notifications.Infrastructure;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Dispatching;
using Identity.Application.Notifications;
using Organizations.Application.Notifications;
using Paqueteria.Application;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Observability;
using Realtime.Application.Configuration;
using Realtime.Application.Dispatching;
using Realtime.Application.Publishing;
using Realtime.Infrastructure.Dispatching;

namespace Paqueteria.UnitTests.Operations;

/// <summary>
/// OBS-003 phase 4 PR 7 (OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13, owner: "Columna nueva (Recommended)"): the producing
/// trace context travels with an outbox row as a W3C version 00 value, every consumer processes each message in that
/// trace (so its log lines carry the originating request's trace id) while the dispatch cycle stays its own root, and
/// the value never reaches an event payload, a SignalR message or a notification.
/// </summary>
public sealed class Obs003OutboxTraceContextTests
{
    private const string SampledTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string SampledSpanId = "00f067aa0ba902b7";
    private const string Sampled = "00-" + SampledTraceId + "-" + SampledSpanId + "-01";
    private const string UnsampledTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string UnsampledSpanId = "b7ad6b7169203331";
    private const string Unsampled = "00-" + UnsampledTraceId + "-" + UnsampledSpanId + "-00";

    [Fact]
    public void The_stored_value_is_the_w3c_version_00_context_of_the_producing_activity()
    {
        using var source = new ActivitySource("Obs003.OutboxTraceContext.Producer");
        using var recorded = Listener(source.Name, ActivitySamplingResult.AllDataAndRecorded);
        Activity.Current = null;
        using (var producer = source.StartActivity("producer"))
        {
            Assert.NotNull(producer);
            var value = OutboxTraceContext.Current();
            Assert.Equal($"00-{producer.TraceId.ToHexString()}-{producer.SpanId.ToHexString()}-01", value);
            Assert.Equal(value, OutboxTraceContext.Of(producer));
            Assert.True(OutboxTraceContext.IsTraceParent(value));
        }

        Assert.Null(OutboxTraceContext.Current());
        Assert.Null(OutboxTraceContext.Of(null));
        using var hierarchical = new Activity("hierarchical").SetIdFormat(ActivityIdFormat.Hierarchical).Start();
        Assert.Null(OutboxTraceContext.Of(hierarchical));
        hierarchical.Stop();

        // The sampled flag is the producing activity's own: a propagation-only activity stores 00.
        using var unrecorded = new Activity("unrecorded").SetIdFormat(ActivityIdFormat.W3C).Start();
        Assert.EndsWith("-00", OutboxTraceContext.Of(unrecorded), StringComparison.Ordinal);
        unrecorded.Stop();
        Activity.Current = null;
    }

    [Theory]
    [InlineData(Sampled, true)]
    [InlineData(Unsampled, true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("00-4BF92F3577B34DA6A3CE929D0E0E4736-00F067AA0BA902B7-01", false)]
    [InlineData("01-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", false)]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-02", false)]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-ff", false)]
    [InlineData("00-00000000000000000000000000000000-00f067aa0ba902b7-01", false)]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-0000000000000000-01", false)]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01,congo=t61rcWkgMzE", false)]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-1", false)]
    [InlineData("00_4bf92f3577b34da6a3ce929d0e0e4736_00f067aa0ba902b7_01", false)]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e473-600f067aa0ba902b7-01", false)]
    public void Only_the_exact_w3c_version_00_shape_is_a_stored_value(string? value, bool expected)
    {
        Assert.Equal(expected, OutboxTraceContext.IsTraceParent(value));
        Assert.Equal(expected, OutboxTraceContext.TryParse(value, out _));
    }

    [Fact]
    public void A_stored_value_parses_as_a_local_parent_with_its_sampled_flag()
    {
        Assert.True(OutboxTraceContext.TryParse(Sampled, out var sampled));
        Assert.Equal(SampledTraceId, sampled.TraceId.ToHexString());
        Assert.Equal(SampledSpanId, sampled.SpanId.ToHexString());
        Assert.Equal(ActivityTraceFlags.Recorded, sampled.TraceFlags);
        Assert.False(sampled.IsRemote);
        Assert.Null(sampled.TraceState);

        Assert.True(OutboxTraceContext.TryParse(Unsampled, out var unsampled));
        Assert.Equal(ActivityTraceFlags.None, unsampled.TraceFlags);
        Assert.False(unsampled.IsRemote);
    }

    [Fact]
    public void A_message_runs_in_the_producer_trace_and_its_dispatch_cycle_stays_its_own_root()
    {
        TraceCorrelation.ConfigureProcess();
        using var cycle = TraceCorrelation.StartOutboxDispatchCycle(OutboxLanes.Notifications);
        Assert.Null(cycle.Parent);
        Assert.NotEqual(SampledTraceId, cycle.TraceId.ToHexString());

        using (var message = TraceCorrelation.StartOutboxMessage(OutboxLanes.Notifications, Sampled))
        {
            Assert.Same(message, Activity.Current);
            Assert.Equal(SampledTraceId, message.TraceId.ToHexString());
            Assert.Equal(SampledSpanId, message.ParentSpanId.ToHexString());
            Assert.Equal(TraceCorrelation.OutboxMessageOperation, message.OperationName);
            Assert.Equal(OutboxLanes.Notifications, message.DisplayName);
            Assert.Equal(ActivityKind.Consumer, message.Kind);
            Assert.False(message.HasRemoteParent);
            Assert.Equal(SampledTraceId, TraceCorrelation.CurrentTraceId());
            // A row the message writes carries the same trace, with the message's own span as its parent.
            Assert.Equal($"00-{SampledTraceId}-{message.SpanId.ToHexString()}", OutboxTraceContext.Current()![..52]);
            Assert.Empty(message.TagObjects);
            Assert.Empty(message.Baggage);
            Assert.Empty(message.Links);
        }

        // The cycle is current again and keeps its own trace for the rest of the cycle.
        Assert.Same(cycle, Activity.Current);
        Assert.Equal(cycle.TraceId.ToHexString(), TraceCorrelation.CurrentTraceId());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00-4BF92F3577B34DA6A3CE929D0E0E4736-00F067AA0BA902B7-01")]
    [InlineData("00-00000000000000000000000000000000-00f067aa0ba902b7-01")]
    public void A_message_without_a_valid_stored_context_runs_under_its_dispatch_cycle(string? stored)
    {
        TraceCorrelation.ConfigureProcess();
        using var cycle = TraceCorrelation.StartOutboxDispatchCycle(OutboxLanes.Dispatch);
        using (var message = TraceCorrelation.StartOutboxMessage(OutboxLanes.Dispatch, stored))
        {
            Assert.Equal(cycle.TraceId, message.TraceId);
            Assert.Same(cycle, message.Parent);
            Assert.Equal(OutboxLanes.Dispatch, message.DisplayName);
        }

        Assert.Same(cycle, Activity.Current);
    }

    [Fact]
    public async Task Notification_lines_carry_the_trace_of_the_request_that_wrote_each_message_and_the_rendered_notification_none()
    {
        TraceCorrelation.ConfigureProcess();
        using var lines = new TraceJsonConsoleLines();
        var sendRequest = NotificationMessage(NotificationOutboxTopics.SendRequested, Sampled);
        var unowned = NotificationMessage("synthetic.unrouted", Unsampled);
        var store = new ScriptedNotificationsStore([sendRequest], [unowned]);
        var provider = new CapturingInAppProvider();
        var options = Options.Create(new NotificationsOptions
        {
            Provider = NotificationsDispatcherProviderKind.PostgreSql,
            PollIntervalMilliseconds = 10,
            StaleRecoveryIntervalSeconds = 600,
        });
        var lanes = new OutboxLaneMonitor(NullLogger<OutboxLaneMonitor>.Instance, TimeProvider.System);
        var processor = new NotificationsOutboxProcessor(
            store,
            new NotificationAudienceResolver(new NoDispatchers(), new NoRecipients()),
            provider,
            options,
            lanes,
            lines.Factory.CreateLogger<NotificationsOutboxProcessor>());
        using var dispatcher = new NotificationsOutboxDispatcher(
            store, processor, options, lanes, TimeProvider.System, lines.Factory.CreateLogger<NotificationsOutboxDispatcher>());

        await dispatcher.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => store.Settled.Count >= 2);
        await dispatcher.StopAsync(CancellationToken.None);

        // Each message's own line carries the trace id of the request that wrote it, never a cycle's.
        var completed = Enumerable.Range(0, lines.Count)
            .Select(lines.Parse)
            .Where(line => line.GetProperty("State").TryGetProperty("Owner", out _))
            .ToArray();
        Assert.Equal(2, completed.Length);
        Assert.Contains(completed, line =>
            line.GetProperty("State").GetProperty("Outcome").GetString() == NotificationDeliveryOutcome.Success &&
            TraceIdOf(line) == SampledTraceId);
        Assert.Contains(completed, line =>
            line.GetProperty("State").GetProperty("Outcome").GetString() == "DEAD" &&
            TraceIdOf(line) == UnsampledTraceId);
        Assert.Equal(SampledTraceId, store.Settled[sendRequest.Id]);
        Assert.Equal(UnsampledTraceId, store.Settled[unowned.Id]);
        // The claim ran in a dispatch cycle, a different root trace.
        Assert.All(store.ClaimTraceIds, traceId =>
            Assert.True(TraceCorrelation.IsTraceId(traceId) && traceId is not (SampledTraceId or UnsampledTraceId)));

        // The provider rendered the notification inside the producing trace, and nothing it received names it.
        var (request, renderedTraceId) = Assert.Single(provider.Requests);
        Assert.Equal(SampledTraceId, renderedTraceId);
        var rendered = NotificationTemplateRenderer.Render(request.Body, request.Variables);
        AssertNoTraceContext(rendered);
        AssertNoTraceContext(JsonSerializer.Serialize(request));
        AssertNoTraceMembers(typeof(SyntheticInAppRequest));
    }

    [Fact]
    public async Task Dispatch_lifecycle_lines_carry_the_trace_of_the_request_that_wrote_each_message()
    {
        TraceCorrelation.ConfigureProcess();
        using var lines = new TraceJsonConsoleLines();
        var first = DispatchMessage(Sampled);
        var second = DispatchMessage(Unsampled);
        var legacy = DispatchMessage(null);
        var store = new ScriptedDispatchOutboxStore([first, second, legacy]);
        var options = Options.Create(new AssignmentLifecycleOptions
        {
            Provider = AssignmentLifecycleProviderKind.PostgreSql,
            PollIntervalMilliseconds = 10,
            StaleRecoveryIntervalSeconds = 600,
        });
        var lanes = new OutboxLaneMonitor(NullLogger<OutboxLaneMonitor>.Instance, TimeProvider.System);
        using var services = new ServiceCollection().BuildServiceProvider();
        var processor = new AssignmentLifecycleProcessor(
            store,
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            new SystemClock(),
            lanes,
            lines.Factory.CreateLogger<AssignmentLifecycleProcessor>());
        using var dispatcher = new AssignmentLifecycleDispatcher(
            store, processor, options, lanes, TimeProvider.System, lines.Factory.CreateLogger<AssignmentLifecycleDispatcher>());

        await dispatcher.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => store.Settled.Count >= 3);
        await dispatcher.StopAsync(CancellationToken.None);

        var rejected = Enumerable.Range(0, lines.Count)
            .Select(lines.Parse)
            .Where(line => line.GetProperty("State").TryGetProperty("Outcome", out var outcome) &&
                           outcome.GetString() == "INVALID_PAYLOAD")
            .Select(TraceIdOf)
            .ToArray();
        Assert.Equal(3, rejected.Length);
        Assert.Contains(SampledTraceId, rejected);
        Assert.Contains(UnsampledTraceId, rejected);
        Assert.Equal(SampledTraceId, store.Settled[first.Id]);
        Assert.Equal(UnsampledTraceId, store.Settled[second.Id]);
        // A row without a stored context is processed in the trace of its dispatch cycle, as before PR 7.
        var claimTrace = Assert.Single(store.ClaimTraceIds.Distinct(StringComparer.Ordinal));
        Assert.Equal(claimTrace, store.Settled[legacy.Id]);
        Assert.Contains(claimTrace, rejected);
    }

    [Fact]
    public async Task A_realtime_message_is_published_in_the_producer_trace_and_the_delivered_message_carries_no_trace_context()
    {
        TraceCorrelation.ConfigureProcess();
        var store = new SettlingRealtimeStore();
        var publisher = RecordingRealtimePublisher.Create();
        var lanes = new OutboxLaneMonitor(NullLogger<OutboxLaneMonitor>.Instance, TimeProvider.System);
        using var telemetry = new RealtimeOutboxTelemetry(TimeProvider.System);
        var processor = new RealtimeOutboxProcessor(
            store,
            UnusedEvidenceReader.Create(),
            publisher,
            new NoOpRealtimeOutboxFailureInjector(),
            new global::Orders.Application.Tracking.PublicOrderStatusPolicy(),
            Options.Create(new OutboxDispatcherOptions { Provider = OutboxDispatcherProviderKind.PostgreSql }),
            telemetry,
            lanes);
        var organization = Guid.NewGuid();
        var notification = Guid.NewGuid();
        var message = new ClaimedBusinessOutboxMessage(
            Guid.NewGuid(),
            organization,
            JsonSerializer.Serialize(new { organization_ids = new[] { organization } }),
            RealtimeOutboxTopics.NotificationStatusChanged,
            "Notification",
            notification,
            2,
            JsonSerializer.Serialize(new
            {
                schema_version = "notification-status-changed-v1",
                notification_id = notification,
                channel = "IN_APP",
                status = "SENT",
                attempts = 1,
                occurred_at = "2026-10-13T12:00:00.000000Z",
            }),
            1,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Sampled);

        using (var cycle = TraceCorrelation.StartOutboxDispatchCycle(OutboxLanes.RealtimeBusiness))
        {
            await processor.ProcessBusinessAsync(message, CancellationToken.None);
            Assert.Same(cycle, Activity.Current);
        }

        Assert.Equal("PROCESSED", Assert.Single(store.Statuses));
        var published = Assert.Single(((RecordingRealtimePublisher)publisher).Published);
        Assert.Equal(SampledTraceId, published.TraceId);
        // The SignalR hub serializes with these options (Realtime.Endpoints): the envelope is the AI-12 contract only.
        var json = JsonSerializer.Serialize(published.Message, published.Message.GetType(), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        AssertNoTraceContext(json);
        Assert.Equal(
            ["aggregate_id", "aggregate_version", "event_id", "event_type", "occurred_at", "payload"],
            JsonDocument.Parse(json).RootElement.EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void No_realtime_envelope_payload_or_notification_contract_has_a_trace_member()
    {
        var types = typeof(global::Realtime.Application.Events.RealtimeEnvelope<>).Assembly.GetTypes()
            .Where(type => type.Namespace == "Realtime.Application.Events")
            .Concat(typeof(SyntheticInAppRequest).Assembly.GetTypes()
                .Where(type => type.Namespace == "Notifications.Infrastructure.Delivery"))
            .Concat(typeof(Paqueteria.Application.Messaging.IMessagingProvider).Assembly.GetTypes()
                .Where(type => type.Namespace == "Paqueteria.Application.Messaging"))
            .ToArray();
        Assert.NotEmpty(types);
        Assert.All(types, AssertNoTraceMembers);
    }

    private static void AssertNoTraceMembers(Type type)
    {
        var members = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(property => property.Name)
            .Concat(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(field => field.Name));
        Assert.DoesNotContain(members, name =>
            name.Contains("trace", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("span", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("baggage", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertNoTraceContext(string text)
    {
        foreach (var fragment in new[] { SampledTraceId, SampledSpanId, UnsampledTraceId, UnsampledSpanId, "trace", "span_id", "baggage" })
        {
            Assert.DoesNotContain(fragment, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? TraceIdOf(JsonElement line) =>
        line.GetProperty("State").TryGetProperty(TraceCorrelation.LogProperty, out var value) ? value.GetString() : null;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the dispatcher did not settle every scripted message in time");
    }

    private static ActivityListener Listener(string sourceName, ActivitySamplingResult result)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => result,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static ClaimedNotificationOutboxMessage NotificationMessage(string topic, string traceParent)
    {
        var organization = Guid.NewGuid();
        var notification = Guid.NewGuid();
        return new ClaimedNotificationOutboxMessage(
            Guid.NewGuid(),
            organization,
            JsonSerializer.Serialize(new { organization_ids = new[] { organization } }),
            topic,
            "Notification",
            notification,
            1,
            JsonSerializer.Serialize(new { schema_version = "notification-send-requested-v1", notification_id = notification }),
            1,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(2),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            traceParent);
    }

    private static ClaimedDispatchOutboxMessage DispatchMessage(string? traceParent) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "synthetic.not-a-reaction",
            "Order",
            Guid.NewGuid(),
            1,
            "{}",
            1,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(2),
            DateTimeOffset.UtcNow,
            traceParent);

    private sealed class ScriptedNotificationsStore(
        IReadOnlyList<ClaimedNotificationOutboxMessage> owned,
        IReadOnlyList<ClaimedNotificationOutboxMessage> unowned) : INotificationsStore
    {
        private int _ownedClaims;
        private int _unownedClaims;

        public ConcurrentDictionary<Guid, string> Settled { get; } = new();

        public ConcurrentQueue<string> ClaimTraceIds { get; } = new();

        public Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> ClaimNotificationsAsync(
            string workerId, int batchSize, TimeSpan lease, CancellationToken cancellationToken)
        {
            ClaimTraceIds.Enqueue(TraceCorrelation.CurrentTraceId() ?? "none");
            return Task.FromResult(Interlocked.Increment(ref _ownedClaims) == 1 ? owned : []);
        }

        public Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> ClaimUnownedAsync(
            string workerId, int batchSize, TimeSpan lease, CancellationToken cancellationToken) =>
            Task.FromResult(Interlocked.Increment(ref _unownedClaims) == 1 ? unowned : []);

        public Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> RecoverStaleAsync(
            string workerId, int batchSize, int maximumAttempts, TimeSpan lease, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClaimedNotificationOutboxMessage>>([]);

        public Task<int> RecoverStaleUnownedAsync(int batchSize, int maximumAttempts, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<bool> SettleAsync(
            Guid id, Guid leaseToken, string status, string? errorCode, DateTimeOffset? availableAt,
            CancellationToken cancellationToken)
        {
            Settled[id] = TraceCorrelation.CurrentTraceId() ?? "none";
            return Task.FromResult(true);
        }

        public Task<string> ExpandSourceAsync(
            ClaimedNotificationOutboxMessage source, ParsedOrderCreated parsed, IReadOnlyList<Guid> recipients,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No orders.created row is scripted.");

        public Task<NotificationDelivery?> ReadDeliveryAsync(
            Guid notificationId, Guid ownerOrganizationId, CancellationToken cancellationToken) =>
            Task.FromResult<NotificationDelivery?>(new NotificationDelivery(
                notificationId,
                ownerOrganizationId,
                NotificationTemplate.Channel,
                "PENDING",
                0,
                1,
                NotificationTemplate.Key,
                NotificationTemplate.Version,
                """{"order_public_id":"ORD_1234567890123456789012","order_status":"DRAFT","occurred_at":"2026-10-13T12:00:00Z"}""",
                "Nueva orden {{order_public_id}} ({{order_status}}) {{occurred_at}}"));

        public Task<bool> ApplyOutcomeAsync(
            ClaimedNotificationOutboxMessage request, NotificationDelivery delivery, string outcome, string code,
            DateTimeOffset attemptedAt, DateTimeOffset? availableAt, CancellationToken cancellationToken)
        {
            Settled[request.Id] = TraceCorrelation.CurrentTraceId() ?? "none";
            return Task.FromResult(true);
        }

        public Task<bool> FinalizeMaximumAttemptsAsync(
            ClaimedNotificationOutboxMessage request, NotificationDelivery delivery, DateTimeOffset finalizedAt,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No exhausted row is scripted.");

        public Task<string?> ResolveConsumerAsync(string topic, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("NOTIFICATIONS");
    }

    private sealed class CapturingInAppProvider : ISyntheticInAppProvider
    {
        public ConcurrentQueue<(SyntheticInAppRequest Request, string? TraceId)> Requests { get; } = new();

        public ValueTask<SyntheticInAppResult> SendAsync(SyntheticInAppRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request, TraceCorrelation.CurrentTraceId()));
            return ValueTask.FromResult(new SyntheticInAppResult(
                NotificationDeliveryOutcome.Success, NotificationErrorCodes.ProviderAccepted, "synthetic-receipt"));
        }
    }

    private sealed class NoDispatchers : IOwnerOrganizationDispatcherReader
    {
        public Task<IReadOnlyList<Guid>> ReadActiveDispatcherUserIdsAsync(
            Guid ownerOrganizationId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class NoRecipients : IActiveNotificationRecipientReader
    {
        public Task<IReadOnlyList<Guid>> ReadActiveUserIdsAsync(
            IReadOnlyCollection<Guid> requestedUserIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class ScriptedDispatchOutboxStore(IReadOnlyList<ClaimedDispatchOutboxMessage> messages)
        : IDispatchOutboxStore
    {
        private int _claims;

        public ConcurrentDictionary<Guid, string> Settled { get; } = new();

        public ConcurrentQueue<string> ClaimTraceIds { get; } = new();

        public Task<IReadOnlyList<ClaimedDispatchOutboxMessage>> ClaimAsync(
            string workerId, int batchSize, TimeSpan lease, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _claims) != 1)
            {
                return Task.FromResult<IReadOnlyList<ClaimedDispatchOutboxMessage>>([]);
            }

            ClaimTraceIds.Enqueue(TraceCorrelation.CurrentTraceId() ?? "none");
            return Task.FromResult(messages);
        }

        public Task<int> RequeueStaleAsync(int batchSize, int maximumAttempts, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<bool> SettleAsync(
            Guid id, Guid leaseToken, string status, string? errorCode, DateTimeOffset? availableAt,
            CancellationToken cancellationToken)
        {
            Settled[id] = TraceCorrelation.CurrentTraceId() ?? "none";
            return Task.FromResult(true);
        }

        public Task<string?> ResolveConsumerAsync(string topic, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("DISPATCH");
    }

    private sealed class SettlingRealtimeStore : IRealtimeOutboxStore
    {
        public ConcurrentQueue<string> Statuses { get; } = new();

        public Task<IReadOnlyList<ClaimedBusinessOutboxMessage>> ClaimBusinessAsync(
            string workerId, int batchSize, TimeSpan lease, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClaimedBusinessOutboxMessage>>([]);

        public Task<IReadOnlyList<ClaimedLocationOutboxMessage>> ClaimLocationAsync(
            string workerId, int batchSize, TimeSpan lease, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClaimedLocationOutboxMessage>>([]);

        public Task<bool> SettleBusinessAsync(
            Guid id, Guid leaseToken, string status, string? errorCode, DateTimeOffset? availableAt,
            CancellationToken cancellationToken)
        {
            Statuses.Enqueue(status);
            return Task.FromResult(true);
        }

        public Task<bool> SettleLocationAsync(
            Guid id, Guid leaseToken, string status, string? errorCode, DateTimeOffset? availableAt,
            CancellationToken cancellationToken)
        {
            Statuses.Enqueue(status);
            return Task.FromResult(true);
        }

        public Task<int> RequeueStaleBusinessAsync(int batchSize, int maximumAttempts, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<int> RequeueStaleLocationAsync(int batchSize, int maximumAttempts, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    /// <summary>Records every published message and the trace it was published in; publishes nothing.</summary>
    public class RecordingRealtimePublisher : DispatchProxy
    {
        public ConcurrentQueue<(object Message, string? TraceId)> Published { get; } = new();

        public static IRealtimePublisher Create() => Create<IRealtimePublisher, RecordingRealtimePublisher>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Published.Enqueue((args![1]!, TraceCorrelation.CurrentTraceId()));
            return Task.CompletedTask;
        }
    }

    /// <summary>A notification status change needs no evidence read; any read is a test failure.</summary>
    public class UnusedEvidenceReader : DispatchProxy
    {
        public static IRealtimeOutboxEvidenceReader Create() =>
            Create<IRealtimeOutboxEvidenceReader, UnusedEvidenceReader>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected evidence read: {targetMethod?.Name}.");
    }

    /// <summary>
    /// The lines the JSON console would write: the framework's own JSON formatter (production options, no scopes)
    /// behind <see cref="TraceCorrelatedLoggerFactory"/>, exactly as the hosts compose it.
    /// </summary>
    private sealed class TraceJsonConsoleLines : IDisposable
    {
        private readonly ServiceProvider _formatters;
        private readonly ConcurrentQueue<string> _lines = new();

        internal TraceJsonConsoleLines()
        {
            _formatters = new ServiceCollection()
                .AddLogging(logging => logging.AddJsonConsole())
                .BuildServiceProvider();
            var formatter = _formatters.GetServices<ConsoleFormatter>()
                .Single(item => item.Name == ConsoleFormatterNames.Json);
            Factory = new TraceCorrelatedLoggerFactory(
                LoggerFactory.Create(logging => logging
                    .SetMinimumLevel(LogLevel.Trace)
                    .AddProvider(new FormattingProvider(formatter, _lines))));
        }

        internal ILoggerFactory Factory { get; }

        internal int Count => _lines.Count;

        internal JsonElement Parse(int index) => JsonDocument.Parse(_lines.ElementAt(index)).RootElement.Clone();

        public void Dispose()
        {
            Factory.Dispose();
            _formatters.Dispose();
        }

        private sealed class FormattingProvider(ConsoleFormatter formatter, ConcurrentQueue<string> lines) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new FormattingLogger(categoryName, formatter, lines);

            public void Dispose()
            {
            }
        }

        private sealed class FormattingLogger(string category, ConsoleFormatter formatter, ConcurrentQueue<string> lines)
            : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> messageFormatter)
            {
                using var writer = new StringWriter();
                formatter.Write(
                    new Microsoft.Extensions.Logging.Abstractions.LogEntry<TState>(
                        logLevel, category, eventId, state, exception, messageFormatter),
                    scopeProvider: null,
                    writer);
                lines.Enqueue(writer.ToString().TrimEnd());
            }
        }
    }
}
