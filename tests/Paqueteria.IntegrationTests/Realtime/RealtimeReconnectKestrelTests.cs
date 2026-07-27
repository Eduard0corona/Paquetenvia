using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Paqueteria.IntegrationTests.Security;
using Realtime.Application.Events;
using Realtime.Application.Publishing;

namespace Paqueteria.IntegrationTests.Realtime;

[Collection(RealtimePostgreSqlKestrelCollection.Name)]
public sealed class RealtimeReconnectKestrelTests(
    PostgreSqlSecurityWebApplicationFactory database)
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 7, 24, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid AggregateId =
        Guid.Parse("77777777-7777-7777-7777-777777777777");

    [Fact]
    public async Task PostgreSql_dispatcher_claims_committed_status_and_publishes_only_authorized_group()
    {
        var recorder = new RealtimeAuthorizationRecorder();
        await using var host = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder,
            workerConnectionString: database.WorkerConnectionString);
        var baseAddress = host.Start();
        await using var authorized = CreateOperationsConnection(
            baseAddress,
            PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
            () => Task.FromResult<string?>(MockIdentityProfiles.ActivePlatformAdminMfa));
        await using var foreign = CreateOperationsConnection(
            baseAddress,
            PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId,
            () => Task.FromResult<string?>(MockIdentityProfiles.ActiveMultiOrganization));
        var received = Completion<RealtimeEnvelope<OrderStatusChangedPayload>>();
        var locationReceived = Completion<RealtimeEnvelope<DriverLocationUpdatedPayload>>();
        var leaked = Completion<RealtimeEnvelope<OrderStatusChangedPayload>>();
        authorized.On(
            "OrderStatusChanged",
            (RealtimeEnvelope<OrderStatusChangedPayload> message) =>
                received.TrySetResult(message));
        authorized.On(
            "DriverLocationUpdated",
            (RealtimeEnvelope<DriverLocationUpdatedPayload> message) =>
                locationReceived.TrySetResult(message));
        foreign.On(
            "OrderStatusChanged",
            (RealtimeEnvelope<OrderStatusChangedPayload> message) =>
                leaked.TrySetResult(message));
        await authorized.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await recorder.WaitForNextOperationsAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await recorder.WaitForNextOperationsAcceptedAsync(TimeSpan.FromSeconds(5)));
        await foreign.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await recorder.WaitForNextOperationsAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await recorder.WaitForNextOperationsAcceptedAsync(TimeSpan.FromSeconds(5)));

        var enqueued = await database.EnqueueRealtimeStatusAsync();
        var terminal = await WaitForOutboxResultAsync(enqueued.OutboxId);
        Assert.True(
            terminal.Status == "PROCESSED",
            $"Business outbox settled as {terminal.Status}: {terminal.LastError}");
        var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(enqueued.OutboxId, delivered.EventId);
        Assert.Equal(enqueued.AggregateVersion, delivered.AggregateVersion);
        Assert.Equal("DELIVERING", delivered.Payload.NewStatus);
        await AssertNotCompletedAsync(leaked.Task, TimeSpan.FromMilliseconds(400));
        var status = await WaitForProcessedAsync(enqueued.OutboxId);
        Assert.Equal("PROCESSED", status);

        var locationOutboxId = await database.EnqueueRealtimeLocationAsync();
        var deliveredLocation = await locationReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(locationOutboxId, deliveredLocation.EventId);
        Assert.Equal("PROCESSED", await WaitForLocationProcessedAsync(locationOutboxId));
    }

    private async Task<(string? Status, string? LastError)> WaitForOutboxResultAsync(Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var result = await database.ReadOutboxResultAsync(id);
            if (result.Status is "PROCESSED" or "DEAD" or "RETRY")
            {
                return result;
            }

            await Task.Delay(50, timeout.Token);
        }

        throw new TimeoutException("The business outbox row did not settle.");
    }

    [Fact]
    public async Task Operations_real_reconnect_reauthorizes_PostgreSql_recovers_group_and_syncs_missed_state()
    {
        var recorder = new RealtimeAuthorizationRecorder();
        var diagnostics = new ConcurrentQueue<string>();
        var tokenFactoryCount = 0;
        var restSynchronizationCount = 0;
        var authoritativeSnapshotVersion = 1L;
        var localVersion = 0L;
        var reconnecting = Completion<Exception?>();
        var reconnected = Completion<string?>();
        var closed = Completion<Exception?>();
        var initialEvent = Completion<RealtimeEnvelope<OrderStatusChangedPayload>>();
        var postReconnectEvent = Completion<RealtimeEnvelope<OrderStatusChangedPayload>>();
        var crossTenantEvent = Completion<RealtimeEnvelope<OrderStatusChangedPayload>>();

        await using var initialHost = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder);
        var baseAddress = initialHost.Start();
        await using var connection = CreateOperationsConnection(
            baseAddress,
            async () =>
            {
                Interlocked.Increment(ref tokenFactoryCount);
                await Task.Yield();
                return MockIdentityProfiles.ActiveMultiOrganization;
            });
        connection.Reconnecting += error =>
        {
            diagnostics.Enqueue($"Reconnecting:{error?.GetType().Name ?? "none"}");
            reconnecting.TrySetResult(error);
            return Task.CompletedTask;
        };
        connection.Reconnected += connectionId =>
        {
            diagnostics.Enqueue("Reconnected");
            Interlocked.Increment(ref restSynchronizationCount);
            Interlocked.Exchange(ref localVersion, Volatile.Read(ref authoritativeSnapshotVersion));
            reconnected.TrySetResult(connectionId);
            return Task.CompletedTask;
        };
        connection.Closed += error =>
        {
            diagnostics.Enqueue($"Closed:{error?.GetType().Name ?? "none"}");
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };
        connection.On(
            "OrderStatusChanged",
            (RealtimeEnvelope<OrderStatusChangedPayload> message) =>
            {
                if (message.AggregateVersion == 1)
                {
                    initialEvent.TrySetResult(message);
                }
                else if (message.AggregateVersion == 6)
                {
                    postReconnectEvent.TrySetResult(message);
                }
                else if (message.AggregateVersion == 99)
                {
                    crossTenantEvent.TrySetResult(message);
                }
            });

        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HubConnectionState.Connected, connection.State);
        Assert.True(await recorder.WaitForNextOperationsAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await recorder.WaitForNextOperationsAcceptedAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, recorder.OperationsCount);

        await PublishOperationsAsync(
            initialHost,
            PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId,
            Message(1));
        Assert.Equal(
            1,
            (await initialEvent.Task.WaitAsync(TimeSpan.FromSeconds(5))).AggregateVersion);
        Interlocked.Exchange(ref localVersion, 1);

        var port = baseAddress.Port;
        await initialHost.DisposeAsync();
        await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HubConnectionState.Reconnecting, connection.State);

        Interlocked.Exchange(ref authoritativeSnapshotVersion, 5);
        await using var restoredHost = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder,
            port);
        var restoredAddress = restoredHost.Start();
        Assert.Equal(baseAddress, restoredAddress);

        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HubConnectionState.Connected, connection.State);
        Assert.True(await recorder.WaitForNextOperationsAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await recorder.WaitForNextOperationsAcceptedAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, recorder.OperationsCount);
        Assert.True(Volatile.Read(ref tokenFactoryCount) >= 2);
        Assert.Equal(1, Volatile.Read(ref restSynchronizationCount));
        Assert.Equal(5, Volatile.Read(ref localVersion));

        await PublishOperationsAsync(
            restoredHost,
            PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId,
            Message(6));
        Assert.Equal(
            6,
            (await postReconnectEvent.Task.WaitAsync(TimeSpan.FromSeconds(5))).AggregateVersion);

        await PublishOperationsAsync(
            restoredHost,
            RealtimeWebApplicationFactory.OrganizationA,
            Message(99));
        await AssertNotCompletedAsync(crossTenantEvent.Task, TimeSpan.FromMilliseconds(400));
        Assert.False(closed.Task.IsCompleted, Diagnostics(diagnostics, connection, recorder, tokenFactoryCount));
    }

    [Fact]
    public async Task Tracking_revoked_during_real_disconnect_cannot_reconnect_or_recover_group()
    {
        var recorder = new RealtimeAuthorizationRecorder();
        var reconnecting = Completion<Exception?>();
        var reconnected = Completion<string?>();
        var closed = Completion<Exception?>();
        var tokenFactoryCount = 0;

        await using var initialHost = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder);
        var baseAddress = initialHost.Start();
        await using var connection = CreateTrackingConnection(
            baseAddress,
            () =>
            {
                Interlocked.Increment(ref tokenFactoryCount);
                return Task.FromResult<string?>(
                    PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken);
            });
        connection.Reconnecting += error =>
        {
            reconnecting.TrySetResult(error);
            return Task.CompletedTask;
        };
        connection.Reconnected += connectionId =>
        {
            reconnected.TrySetResult(connectionId);
            return Task.CompletedTask;
        };
        connection.Closed += error =>
        {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };

        try
        {
            await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Tracking connection failed: {recorder.TrackingException}",
                exception);
        }
        Assert.True(await recorder.WaitForNextTrackingAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await recorder.WaitForNextTrackingAcceptedAsync(TimeSpan.FromSeconds(5)));
        var initialAuthorizationCount = recorder.TrackingCount;
        Assert.True(initialAuthorizationCount >= 1);

        var port = baseAddress.Port;
        await initialHost.DisposeAsync();
        await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await database.RevokeValidTrackingTokenAsync();

        try
        {
            await using var restoredHost = new RealtimeKestrelWebApplicationFactory(
                database.ApplicationConnectionString,
                recorder,
                port);
            restoredHost.Start();

            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(reconnected.Task.IsCompleted);
            Assert.Equal(HubConnectionState.Disconnected, connection.State);
            Assert.True(recorder.TrackingCount > initialAuthorizationCount);
            Assert.True(Volatile.Read(ref tokenFactoryCount) >= 2);
        }
        finally
        {
            await database.RestoreValidTrackingTokenAsync();
        }
    }

    [Fact]
    public async Task Tracking_maximum_lifetime_bounds_revocation_of_an_active_connection()
    {
        var recorder = new RealtimeAuthorizationRecorder();
        var reconnecting = Completion<Exception?>();
        var reconnected = Completion<string?>();
        var closed = Completion<Exception?>();

        await using var host = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Realtime:TrackingMaximumConnectionLifetimeSeconds"] = "5",
            });
        var baseAddress = host.Start();
        await using var connection = CreateTrackingConnection(
            baseAddress,
            () => Task.FromResult<string?>(
                PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken));
        connection.Reconnecting += error =>
        {
            reconnecting.TrySetResult(error);
            return Task.CompletedTask;
        };
        connection.Reconnected += connectionId =>
        {
            reconnected.TrySetResult(connectionId);
            return Task.CompletedTask;
        };
        connection.Closed += error =>
        {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };

        try
        {
            await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await recorder.WaitForNextTrackingAcceptedAsync(
                TimeSpan.FromSeconds(5)));
            var authorizationCount = recorder.TrackingCount;
            await database.RevokeValidTrackingTokenAsync();

            await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(reconnected.Task.IsCompleted);
            Assert.Equal(HubConnectionState.Disconnected, connection.State);
            Assert.True(recorder.TrackingCount > authorizationCount);
        }
        finally
        {
            await database.RestoreValidTrackingTokenAsync();
        }
    }

    private static HubConnection CreateOperationsConnection(
        Uri baseAddress,
        Func<Task<string?>> tokenFactory) =>
        CreateOperationsConnection(
            baseAddress,
            PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId,
            tokenFactory);

    private static HubConnection CreateOperationsConnection(
        Uri baseAddress,
        Guid organizationId,
        Func<Task<string?>> tokenFactory) =>
        Configure(new HubConnectionBuilder()
            .WithUrl(
                new Uri(
                    baseAddress,
                    $"/hubs/operations?organization_id={organizationId:D}"),
                options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.AccessTokenProvider = tokenFactory;
                })
            .WithAutomaticReconnect(
                [
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5),
                ]));

    private async Task<string?> WaitForProcessedAsync(Guid outboxId)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(10);
        string? status;
        do
        {
            status = await database.ReadOutboxStatusAsync(outboxId);
            if (status == "PROCESSED")
            {
                return status;
            }

            await Task.Delay(100);
        }
        while (DateTimeOffset.UtcNow < timeout);

        return status;
    }

    private async Task<string?> WaitForLocationProcessedAsync(Guid outboxId)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(10);
        string? status;
        do
        {
            status = await database.ReadLocationOutboxStatusAsync(outboxId);
            if (status == "PROCESSED")
            {
                return status;
            }

            await Task.Delay(100);
        }
        while (DateTimeOffset.UtcNow < timeout);

        return status;
    }

    private static HubConnection CreateTrackingConnection(
        Uri baseAddress,
        Func<Task<string?>> tokenFactory) =>
        Configure(new HubConnectionBuilder()
            .WithUrl(
                new Uri(baseAddress, "/hubs/tracking"),
                options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.AccessTokenProvider = tokenFactory;
                })
            .WithAutomaticReconnect(
                [
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromSeconds(1),
                ]));

    private static HubConnection Configure(IHubConnectionBuilder builder)
    {
        var connection = builder
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                options.PayloadSerializerOptions.DefaultIgnoreCondition =
                    JsonIgnoreCondition.WhenWritingNull;
            })
            .Build();
        connection.ServerTimeout = TimeSpan.FromSeconds(5);
        connection.KeepAliveInterval = TimeSpan.FromSeconds(1);
        return connection;
    }

    private static Task PublishOperationsAsync(
        RealtimeKestrelWebApplicationFactory host,
        Guid organizationId,
        RealtimeEnvelope<OrderStatusChangedPayload> message) =>
        host.Services.GetRequiredService<IRealtimePublisher>()
            .PublishOperationsOrderStatusChangedAsync(
                OperationsAudience.ForOrganization(organizationId),
                message,
                default);

    private static RealtimeEnvelope<OrderStatusChangedPayload> Message(long version) =>
        new(
            Guid.NewGuid(),
            RealtimeEventTypes.OrderStatusChanged,
            OccurredAt,
            AggregateId,
            version,
            null,
            new OrderStatusChangedPayload(
                AggregateId,
                "READY_FOR_PICKUP",
                "ASSIGNED",
                OccurredAt));

    private static async Task AssertNotCompletedAsync(Task task, TimeSpan timeout)
    {
        var marker = Task.Delay(timeout);
        Assert.Same(marker, await Task.WhenAny(task, marker));
    }

    private static string Diagnostics(
        IEnumerable<string> lifecycle,
        HubConnection connection,
        RealtimeAuthorizationRecorder recorder,
        int tokenFactoryCount) =>
        $"state={connection.State}; lifecycle={string.Join(',', lifecycle)}; " +
        $"token_factory={tokenFactoryCount}; operations_authorizations={recorder.OperationsCount}";

    private static TaskCompletionSource<T> Completion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealtimePostgreSqlKestrelCollection
    : ICollectionFixture<PostgreSqlSecurityWebApplicationFactory>
{
    public const string Name = "Realtime PostgreSQL Kestrel";
}
