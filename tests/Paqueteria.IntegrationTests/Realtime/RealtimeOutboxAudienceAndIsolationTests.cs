using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Paqueteria.IntegrationTests.Security;
using Realtime.Application.Dispatching;
using Realtime.Application.Events;

namespace Paqueteria.IntegrationTests.Realtime;

[Collection(RealtimePostgreSqlKestrelCollection.Name)]
public sealed class RealtimeOutboxAudienceAndIsolationTests(
    PostgreSqlSecurityWebApplicationFactory database)
{
    [Fact]
    [Trait("Category", "OutboxSignalRDelivery")]
    public async Task Real_driver_hub_receives_authorized_status_and_assignment_and_skips_revoked()
    {
        await database.SetActiveDriverMembershipStatusAsync("ACTIVE");
        var scenario = await database.CreateDriverAudienceOutboxScenarioAsync(
            payloadAdditionalTicks: 7);
        using var metrics = new OutboxMetricProbe();
        var recorder = new RealtimeAuthorizationRecorder();
        await using var host = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder,
            workerConnectionString: database.WorkerConnectionString);
        var baseAddress = host.Start();
        await using var operations = CreateOperationsConnection(baseAddress);
        await using var authorizedDriver = CreateDriverConnection(
            baseAddress,
            MockIdentityProfiles.ActiveDriver);
        await using var otherDriver = CreateDriverConnection(
            baseAddress,
            MockIdentityProfiles.SecondaryDriver);
        await using var viewerOnly = CreateDriverConnection(
            baseAddress,
            MockIdentityProfiles.ActiveViewer);
        var operationStatuses = new MessageCollector<RealtimeEnvelope<OrderStatusChangedPayload>>();
        var operationAssignments = new MessageCollector<RealtimeEnvelope<AssignmentChangedPayload>>();
        var operationTimeline =
            new MessageCollector<RealtimeEnvelope<OrderTimelineEventAddedPayload>>();
        var authorizedStatuses = new MessageCollector<RealtimeEnvelope<OrderStatusChangedPayload>>();
        var authorizedAssignments = new MessageCollector<RealtimeEnvelope<AssignmentChangedPayload>>();
        var otherStatuses = new MessageCollector<RealtimeEnvelope<OrderStatusChangedPayload>>();
        var otherAssignments = new MessageCollector<RealtimeEnvelope<AssignmentChangedPayload>>();
        operations.On<RealtimeEnvelope<OrderStatusChangedPayload>>(
            "OrderStatusChanged",
            operationStatuses.Add);
        operations.On<RealtimeEnvelope<AssignmentChangedPayload>>(
            "AssignmentChanged",
            operationAssignments.Add);
        operations.On<RealtimeEnvelope<OrderTimelineEventAddedPayload>>(
            "OrderTimelineEventAdded",
            operationTimeline.Add);
        authorizedDriver.On<RealtimeEnvelope<OrderStatusChangedPayload>>(
            "OrderStatusChanged",
            authorizedStatuses.Add);
        authorizedDriver.On<RealtimeEnvelope<AssignmentChangedPayload>>(
            "AssignmentChanged",
            authorizedAssignments.Add);
        otherDriver.On<RealtimeEnvelope<OrderStatusChangedPayload>>(
            "OrderStatusChanged",
            otherStatuses.Add);
        otherDriver.On<RealtimeEnvelope<AssignmentChangedPayload>>(
            "AssignmentChanged",
            otherAssignments.Add);

        try
        {
            await operations.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await recorder.WaitForNextOperationsAcceptedAsync(TimeSpan.FromSeconds(5)));
            // StartAsync completes at the handshake, before DriverHub.OnConnectedAsync has
            // joined the driver/assignment groups; wait for the server-side acceptance of
            // each driver (one at a time, so the recorder's authorization-to-acceptance
            // mapping stays unambiguous) before the outbox rows become available.
            await authorizedDriver.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await recorder.WaitForDriverAcceptedAsync(
                PostgreSqlSecurityWebApplicationFactory.ActiveDriverId,
                TimeSpan.FromSeconds(5)));
            await otherDriver.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await recorder.WaitForDriverAcceptedAsync(
                PostgreSqlSecurityWebApplicationFactory.SecondaryDriverId,
                TimeSpan.FromSeconds(5)));
            await AssertConnectionRejectedAsync(viewerOnly);

            await database.MakeBusinessOutboxAvailableAsync(scenario.StatusOutboxId);
            await database.MakeBusinessOutboxAvailableAsync(scenario.AssignmentOutboxId);
            var operationStatus = await operationStatuses.WaitForAsync(
                value => value.EventId == scenario.StatusOutboxId);
            var driverStatus = await authorizedStatuses.WaitForAsync(
                value => value.EventId == scenario.StatusOutboxId);
            var operationAssignment = await operationAssignments.WaitForAsync(
                value => value.EventId == scenario.AssignmentOutboxId);
            var driverAssignment = await authorizedAssignments.WaitForAsync(
                value => value.EventId == scenario.AssignmentOutboxId);
            var historicalTimeline = await database.EnqueueHistoricalTimelineAsync(
                scenario.OrderEventId,
                payloadAdditionalTicks: 7);
            var timeline = await operationTimeline.WaitForAsync(
                value => value.EventId == historicalTimeline.OutboxId);

            Assert.Equal(scenario.DriverId, driverAssignment.Payload.DriverId);
            Assert.Equal(scenario.AssignmentId, driverAssignment.Payload.AssignmentId);
            Assert.Equal(operationStatus, driverStatus);
            Assert.Equal(operationAssignment, driverAssignment);
            Assert.Equal(scenario.OccurredAt, operationStatus.OccurredAt);
            Assert.Equal(operationStatus.OccurredAt, operationStatus.Payload.OccurredAt);
            Assert.Equal(scenario.OccurredAt, operationAssignment.OccurredAt);
            Assert.Equal(operationAssignment.OccurredAt, operationAssignment.Payload.OccurredAt);
            Assert.Equal(historicalTimeline.OccurredAt, timeline.OccurredAt);
            Assert.Equal(timeline.OccurredAt, timeline.Payload.OccurredAt);
            Assert.All(
                new[]
                {
                    operationStatus.OccurredAt,
                    operationAssignment.OccurredAt,
                    timeline.OccurredAt,
                },
                value => Assert.Equal(0, value.UtcTicks % 10));
            Assert.Equal("PROCESSED", await WaitForBusinessTerminalAsync(scenario.StatusOutboxId));
            Assert.Equal("PROCESSED", await WaitForBusinessTerminalAsync(scenario.AssignmentOutboxId));
            Assert.Equal(
                "PROCESSED",
                await WaitForBusinessTerminalAsync(historicalTimeline.OutboxId));
            await AssertStableCountAsync(otherStatuses, 0);
            await AssertStableCountAsync(otherAssignments, 0);

            var statusCountBeforeRevocation = authorizedStatuses.Count;
            var revokedStatus = await database.EnqueueDriverStatusAsync(scenario.AssignmentId);
            await database.SetActiveDriverMembershipStatusAsync("SUSPENDED");
            await database.MakeBusinessOutboxAvailableAsync(revokedStatus.OutboxId);
            await operationStatuses.WaitForAsync(
                value => value.EventId == revokedStatus.OutboxId);
            Assert.Equal("PROCESSED", await WaitForBusinessTerminalAsync(revokedStatus.OutboxId));
            await AssertStableCountAsync(authorizedStatuses, statusCountBeforeRevocation);

            await database.SetActiveDriverMembershipStatusAsync("ACTIVE");
            var inactiveAssignment =
                await database.CreateDriverAssignmentOutboxScenarioAsync();
            var assignmentCountBeforeRevocation = authorizedAssignments.Count;
            await database.SetAssignmentStatusAsync(
                inactiveAssignment.AssignmentId,
                "CANCELLED");
            await database.MakeBusinessOutboxAvailableAsync(inactiveAssignment.OutboxId);
            await operationAssignments.WaitForAsync(
                value => value.EventId == inactiveAssignment.OutboxId);
            Assert.Equal(
                "PROCESSED",
                await WaitForBusinessTerminalAsync(inactiveAssignment.OutboxId));
            await AssertStableCountAsync(
                authorizedAssignments,
                assignmentCountBeforeRevocation);

            await metrics.WaitForAsync(
                "realtime.outbox.audience_deliveries",
                tags => tags["lane"] == "business" &&
                    tags["audience"] == "driver" &&
                    tags["outcome"] == "driver_audience_skipped",
                minimumCount: 2);
        }
        finally
        {
            await database.SetActiveDriverMembershipStatusAsync("ACTIVE");
            await database.SetAssignmentStatusAsync(scenario.AssignmentId, "CANCELLED");
        }
    }

    [Fact]
    [Trait("Category", "OutboxSignalRDelivery")]
    public async Task Real_location_lane_publishes_exact_payload_only_to_same_tenant_operations()
    {
        using var metrics = new OutboxMetricProbe();
        using var logs = new CapturingLogProvider();
        var recorder = new RealtimeAuthorizationRecorder();
        await using var host = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder,
            workerConnectionString: database.WorkerConnectionString,
            logProvider: logs);
        var baseAddress = host.Start();
        await using var operations = CreateOperationsConnection(baseAddress);
        await using var foreignOperations = CreateOperationsConnection(
            baseAddress,
            PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId,
            MockIdentityProfiles.ActiveMultiOrganization);
        await using var driver = CreateDriverConnection(
            baseAddress,
            MockIdentityProfiles.ActiveDriver);
        await using var tracking = CreateTrackingConnection(baseAddress);
        var delivered = new MessageCollector<RealtimeEnvelope<DriverLocationUpdatedPayload>>();
        var foreign = new MessageCollector<RealtimeEnvelope<DriverLocationUpdatedPayload>>();
        var driverLeak = new MessageCollector<RealtimeEnvelope<DriverLocationUpdatedPayload>>();
        var trackingLeak = new MessageCollector<RealtimeEnvelope<DriverLocationUpdatedPayload>>();
        operations.On<RealtimeEnvelope<DriverLocationUpdatedPayload>>(
            "DriverLocationUpdated",
            delivered.Add);
        foreignOperations.On<RealtimeEnvelope<DriverLocationUpdatedPayload>>(
            "DriverLocationUpdated",
            foreign.Add);
        driver.On<RealtimeEnvelope<DriverLocationUpdatedPayload>>(
            "DriverLocationUpdated",
            driverLeak.Add);
        tracking.On<RealtimeEnvelope<DriverLocationUpdatedPayload>>(
            "DriverLocationUpdated",
            trackingLeak.Add);
        await operations.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await recorder.WaitForNextOperationsAcceptedAsync(TimeSpan.FromSeconds(5)));
        await foreignOperations.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await recorder.WaitForNextOperationsAcceptedAsync(TimeSpan.FromSeconds(5)));
        await driver.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        // The driver connection is a leak probe: wait until DriverHub joined its groups so
        // the "no delivery" assertion below is not satisfied vacuously.
        Assert.True(await recorder.WaitForDriverAcceptedAsync(
            PostgreSqlSecurityWebApplicationFactory.ActiveDriverId,
            TimeSpan.FromSeconds(5)));
        await tracking.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await recorder.WaitForNextTrackingAcceptedAsync(TimeSpan.FromSeconds(5)));

        var clientEventId = Guid.NewGuid();
        var body =
            $$"""
              {"positions":[{
                "client_event_id":"{{clientEventId:D}}",
                "lat":24.809064,
                "lng":-107.394011,
                "accuracy_m":7.5,
                "captured_at":"2026-07-25T18:00:00.1234567Z"
              }]}
              """;
        using var http = new HttpClient { BaseAddress = baseAddress };
        using (var request = DriverLocationRequest(body))
        using (var response = await http.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            using var responseJson = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            Assert.Equal(
                "ACCEPTED",
                responseJson.RootElement.GetProperty("items")[0]
                    .GetProperty("status").GetString());
        }

        var scenario = await database.ReadHttpLocationScenarioAsync(clientEventId);
        var terminal = await WaitForLocationTerminalAsync(scenario.OutboxId);
        var result = await database.ReadLocationOutboxResultAsync(scenario.OutboxId);
        Assert.True(
            terminal == "PROCESSED",
            $"Location outbox settled as {terminal}: {result.LastError}");
        var message = await delivered.WaitForAsync(
            value => value.EventId == scenario.OutboxId);

        Assert.Equal(scenario.OutboxId, message.EventId);
        Assert.Equal(scenario.DriverId, message.AggregateId);
        Assert.Equal(
            RealtimeLocationCursor.FromCapturedAt(scenario.CapturedAt),
            message.AggregateVersion);
        Assert.Equal(scenario.CapturedAt, message.OccurredAt);
        Assert.Equal(scenario.DriverId, message.Payload.DriverId);
        Assert.Equal(scenario.Lat, message.Payload.Lat, 6);
        Assert.Equal(scenario.Lng, message.Payload.Lng, 6);
        Assert.Equal(scenario.AccuracyM, message.Payload.AccuracyM, 6);
        Assert.Equal(scenario.CapturedAt, message.Payload.CapturedAt);
        Assert.Equal(0, message.OccurredAt.UtcTicks % 10);
        Assert.Equal(
            DateTimeOffset.Parse(
                "2026-07-25T18:00:00.1234560Z",
                System.Globalization.CultureInfo.InvariantCulture),
            message.OccurredAt);
        await AssertStableCountAsync(foreign, 0);
        await AssertStableCountAsync(driverLeak, 0);
        await AssertStableCountAsync(trackingLeak, 0);
        Assert.DoesNotContain(
            metrics.Measurements,
            measurement => measurement.Tags.Keys.Any(
                key => key.Contains("lat", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("lng", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("coordinate", StringComparison.OrdinalIgnoreCase)));
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        Assert.DoesNotContain(
            logs.Messages,
            message =>
                message.Contains(scenario.Lat.ToString("R", invariant), StringComparison.Ordinal) ||
                message.Contains(scenario.Lng.ToString("R", invariant), StringComparison.Ordinal) ||
                message.Contains(
                    scenario.AccuracyM.ToString("R", invariant),
                    StringComparison.Ordinal));

        using (var duplicate = DriverLocationRequest(body))
        using (var duplicateResponse = await http.SendAsync(duplicate))
        {
            Assert.Equal(HttpStatusCode.Accepted, duplicateResponse.StatusCode);
            using var responseJson = JsonDocument.Parse(
                await duplicateResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                "DUPLICATE",
                responseJson.RootElement.GetProperty("items")[0]
                    .GetProperty("status").GetString());
        }

        var corruptBusiness = await database.EnqueueRealtimeStatusAsync(
            isPublic: false,
            payloadAdditionalTicks: 10);
        var corruptLocation = await database.EnqueueRealtimeLocationEvidenceAsync(
            payloadAdditionalTicks: 10);
        Assert.Equal("DEAD", await WaitForBusinessTerminalAsync(corruptBusiness.OutboxId));
        Assert.Equal("DEAD", await WaitForLocationTerminalAsync(corruptLocation.OutboxId));
        Assert.Equal(
            RealtimeOutboxErrorCodes.InvalidPayload,
            (await database.ReadOutboxResultAsync(corruptBusiness.OutboxId)).LastError);
        Assert.Equal(
            RealtimeOutboxErrorCodes.InvalidPayload,
            (await database.ReadLocationOutboxResultAsync(corruptLocation.OutboxId)).LastError);
        await AssertStableCountAsync(delivered, 1);
    }

    [Fact]
    [Trait("Category", "OutboxSignalRDelivery")]
    public async Task Real_poison_in_each_lane_does_not_starve_the_other_lane()
    {
        using var metrics = new OutboxMetricProbe();
        var recorder = new RealtimeAuthorizationRecorder();
        await using var host = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder,
            workerConnectionString: database.WorkerConnectionString);
        host.Start();

        var businessPoison = await database.EnqueueBusinessPoisonAsync();
        var validLocation = await database.EnqueueRealtimeLocationEvidenceAsync();
        Assert.Equal("DEAD", await WaitForBusinessTerminalAsync(businessPoison));
        Assert.Equal("PROCESSED", await WaitForLocationTerminalAsync(validLocation.OutboxId));

        var locationPoison = await database.EnqueueLocationPoisonAsync();
        var validBusiness = await database.EnqueueRealtimeStatusAsync(isPublic: false);
        Assert.Equal("DEAD", await WaitForLocationTerminalAsync(locationPoison));
        Assert.Equal("PROCESSED", await WaitForBusinessTerminalAsync(validBusiness.OutboxId));

        await metrics.WaitForAsync(
            "realtime.outbox.mapping_failures",
            tags => tags["lane"] == "business",
            minimumCount: 1);
        await metrics.WaitForAsync(
            "realtime.outbox.mapping_failures",
            tags => tags["lane"] == "location",
            minimumCount: 1);
        Assert.Equal(
            RealtimeOutboxErrorCodes.InvalidPayload,
            (await database.ReadOutboxResultAsync(businessPoison)).LastError);
        Assert.Equal(
            RealtimeOutboxErrorCodes.InvalidPayload,
            (await database.ReadLocationOutboxResultAsync(locationPoison)).LastError);
    }

    private async Task<string?> WaitForBusinessTerminalAsync(Guid outboxId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            var status = await database.ReadOutboxStatusAsync(outboxId);
            if (status is "PROCESSED" or "DEAD")
            {
                return status;
            }

            await Task.Delay(50, timeout.Token);
        }

        throw new TimeoutException("The business outbox row did not reach a terminal state.");
    }

    private async Task<string?> WaitForLocationTerminalAsync(Guid outboxId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            var status = await database.ReadLocationOutboxStatusAsync(outboxId);
            if (status is "PROCESSED" or "DEAD")
            {
                return status;
            }

            await Task.Delay(50, timeout.Token);
        }

        throw new TimeoutException("The location outbox row did not reach a terminal state.");
    }

    private static HubConnection CreateOperationsConnection(
        Uri baseAddress,
        Guid? organizationId = null,
        string? token = null) =>
        Configure(new HubConnectionBuilder().WithUrl(
            new Uri(
                baseAddress,
                $"/hubs/operations?organization_id={organizationId ??
                    PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId:D}"),
            options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = () => Task.FromResult<string?>(
                    token ?? MockIdentityProfiles.ActivePlatformAdminMfa);
            }));

    private static HubConnection CreateDriverConnection(
        Uri baseAddress,
        string token)
    {
        var organizationId =
            PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId;
        return Configure(new HubConnectionBuilder().WithUrl(
            new Uri(
                baseAddress,
                $"/hubs/driver?organization_id={organizationId:D}"),
            options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            }));
    }

    private static HubConnection CreateTrackingConnection(Uri baseAddress) =>
        Configure(new HubConnectionBuilder().WithUrl(
            new Uri(baseAddress, "/hubs/tracking"),
            options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = () => Task.FromResult<string?>(
                    PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken);
            }));

    private static HttpRequestMessage DriverLocationRequest(string body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/driver/me/location-updates");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            MockIdentityProfiles.ActiveDriver);
        request.Headers.Add(
            "X-Organization-Id",
            PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId.ToString("D"));
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private static HubConnection Configure(IHubConnectionBuilder builder)
    {
        var connection = builder
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy =
                    JsonNamingPolicy.SnakeCaseLower;
                options.PayloadSerializerOptions.DefaultIgnoreCondition =
                    JsonIgnoreCondition.WhenWritingNull;
            })
            .Build();
        connection.ServerTimeout = TimeSpan.FromSeconds(5);
        connection.KeepAliveInterval = TimeSpan.FromSeconds(1);
        return connection;
    }

    private static async Task AssertConnectionRejectedAsync(HubConnection connection)
    {
        var closed = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += exception =>
        {
            closed.TrySetResult(exception);
            return Task.CompletedTask;
        };
        try
        {
            await connection.StartAsync();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // Rejection can surface during handshake or immediately after OnConnected.
        }

        Assert.NotEqual(HubConnectionState.Connected, connection.State);
    }

    private static async Task AssertStableCountAsync<T>(
        MessageCollector<T> collector,
        int expected)
        where T : class
    {
        await Task.Delay(350);
        Assert.Equal(expected, collector.Count);
    }

    private sealed class MessageCollector<T>
        where T : class
    {
        private readonly ConcurrentQueue<T> _messages = new();
        private readonly SemaphoreSlim _arrived = new(0);

        internal int Count => _messages.Count;

        internal void Add(T message)
        {
            _messages.Enqueue(message);
            _arrived.Release();
        }

        internal async Task<T> WaitForAsync(
            Func<T, bool> predicate,
            TimeSpan? timeout = null)
        {
            using var cancellation = new CancellationTokenSource(
                timeout ?? TimeSpan.FromSeconds(10));
            while (!cancellation.IsCancellationRequested)
            {
                var match = _messages.FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }

                await _arrived.WaitAsync(cancellation.Token);
            }

            throw new TimeoutException("The expected SignalR message was not observed.");
        }
    }

    private sealed class OutboxMetricProbe : IDisposable
    {
        private const string MeterName = "Paquetenvia.Realtime.Outbox";
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<MetricMeasurement> _measurements = new();
        private readonly SemaphoreSlim _recorded = new(0);

        internal OutboxMetricProbe()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var copiedTags = tags.ToArray().ToDictionary(
                    pair => pair.Key,
                    pair => Convert.ToString(
                        pair.Value,
                        System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    StringComparer.Ordinal);
                _measurements.Enqueue(new(instrument.Name, value, copiedTags));
                _recorded.Release();
            });
            _listener.Start();
        }

        internal IReadOnlyCollection<MetricMeasurement> Measurements =>
            _measurements.ToArray();

        internal async Task WaitForAsync(
            string instrumentName,
            Func<IReadOnlyDictionary<string, string>, bool> predicate,
            int minimumCount)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!cancellation.IsCancellationRequested)
            {
                var count = _measurements.Count(
                    measurement => measurement.InstrumentName == instrumentName &&
                        predicate(measurement.Tags));
                if (count >= minimumCount)
                {
                    return;
                }

                await _recorded.WaitAsync(cancellation.Token);
            }

            throw new TimeoutException($"Metric {instrumentName} was not observed.");
        }

        public void Dispose()
        {
            _listener.Dispose();
            _recorded.Dispose();
        }
    }

    private sealed record MetricMeasurement(
        string InstrumentName,
        long Value,
        IReadOnlyDictionary<string, string> Tags);

    private sealed class CapturingLogProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        internal IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
                if (exception is not null)
                {
                    messages.Enqueue(exception.ToString());
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            internal static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
