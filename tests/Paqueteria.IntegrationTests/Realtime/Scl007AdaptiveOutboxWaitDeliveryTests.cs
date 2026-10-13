using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Paqueteria.IntegrationTests.Security;
using Realtime.Application.Configuration;
using Realtime.Application.Events;
using Xunit.Abstractions;

namespace Paqueteria.IntegrationTests.Realtime;

/// <summary>
/// SCL-007-ADAPTIVE-OUTBOX-WAIT-2026-10-13: with the production waits (business 250 ms, location 500 ms, both up to
/// 1 s), a notice committed after the lanes have backed off to their longest wait still reaches a real SignalR client
/// within the AI-15 <c>event_delivery_p95_seconds</c> warning (2 s). The API runs on Kestrel against real PostgreSQL;
/// each latency is measured from just before the producer's insert to the client's receipt.
/// </summary>
[Collection(RealtimePostgreSqlKestrelCollection.Name)]
public sealed class Scl007AdaptiveOutboxWaitDeliveryTests(
    PostgreSqlSecurityWebApplicationFactory database,
    ITestOutputHelper output)
{
    private const int Rounds = 2;

    private static readonly TimeSpan DeliveryBudget =
        TimeSpan.FromMilliseconds(OutboxDispatcherOptionsValidator.EventDeliveryBudgetMilliseconds);

    /// <summary>
    /// Longer than both ramps (business 250 + 500 ms of waits, location 500 ms) plus their claims, so each lane is in
    /// its 1 s wait when the notice is committed.
    /// </summary>
    private static readonly TimeSpan IdlePeriod = TimeSpan.FromSeconds(3);

    [Fact]
    [Trait("Category", "OutboxSignalRDelivery")]
    public async Task After_an_idle_period_a_new_notice_on_each_lane_arrives_within_the_ai15_delivery_budget()
    {
        var recorder = new RealtimeAuthorizationRecorder();
        await using var host = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder,
            realtimeConnectionString: database.RealtimeConnectionString,
            configurationOverrides: new Dictionary<string, string?>
            {
                // The API's own waits (appsettings.json), not the harness's 50 ms poll.
                ["Realtime:OutboxDispatcher:Business:PollIntervalMilliseconds"] = "250",
                ["Realtime:OutboxDispatcher:Business:MaximumIdleWaitMilliseconds"] = "1000",
                ["Realtime:OutboxDispatcher:Location:PollIntervalMilliseconds"] = "500",
                ["Realtime:OutboxDispatcher:Location:MaximumIdleWaitMilliseconds"] = "1000",
                ["Realtime:OutboxDispatcher:StaleRequeueIntervalSeconds"] = "30",
            });
        var baseAddress = host.Start();
        var options = host.Services.GetRequiredService<IOptions<OutboxDispatcherOptions>>().Value;
        Assert.Equal(OutboxDispatcherProviderKind.PostgreSql, options.Provider);
        Assert.Equal(1_000, options.Business.MaximumIdleWaitMilliseconds);
        Assert.Equal(1_000, options.Location.MaximumIdleWaitMilliseconds);

        var arrivals = new ConcurrentDictionary<Guid, TaskCompletionSource<long>>();
        await using var operations = CreateOperationsConnection(baseAddress);
        operations.On(
            "OrderStatusChanged",
            (RealtimeEnvelope<OrderStatusChangedPayload> message) => Arrived(arrivals, message.EventId));
        operations.On(
            "DriverLocationUpdated",
            (RealtimeEnvelope<DriverLocationUpdatedPayload> message) => Arrived(arrivals, message.EventId));
        await operations.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await recorder.WaitForNextOperationsAcceptedAsync(TimeSpan.FromSeconds(5)));

        var latencies = new List<(string Lane, TimeSpan Latency)>();
        for (var round = 0; round < Rounds; round++)
        {
            await Task.Delay(IdlePeriod);
            latencies.Add(("business", await MeasureAsync(
                arrivals,
                async () => (await database.EnqueueRealtimeStatusAsync()).OutboxId)));

            await Task.Delay(IdlePeriod);
            latencies.Add(("location", await MeasureAsync(arrivals, database.EnqueueRealtimeLocationAsync)));
        }

        foreach (var (lane, latency) in latencies)
        {
            output.WriteLine(
                $"lane={lane} delivery_after_idle_ms={latency.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}");
        }

        Assert.All(latencies, measured => Assert.True(
            measured.Latency <= DeliveryBudget,
            $"{measured.Lane} notice after {IdlePeriod.TotalSeconds} s idle took {measured.Latency.TotalMilliseconds} ms"));
    }

    private static void Arrived(ConcurrentDictionary<Guid, TaskCompletionSource<long>> arrivals, Guid eventId) =>
        arrivals.GetOrAdd(eventId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(Stopwatch.GetTimestamp());

    private static async Task<TimeSpan> MeasureAsync(
        ConcurrentDictionary<Guid, TaskCompletionSource<long>> arrivals,
        Func<Task<Guid>> enqueue)
    {
        var started = Stopwatch.GetTimestamp();
        var outboxId = await enqueue();
        var arrived = await arrivals
            .GetOrAdd(outboxId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
            .Task
            .WaitAsync(TimeSpan.FromSeconds(10));
        return Stopwatch.GetElapsedTime(started, arrived);
    }

    private static HubConnection CreateOperationsConnection(Uri baseAddress)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(
                    baseAddress,
                    $"/hubs/operations?organization_id={PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId:D}"),
                options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.AccessTokenProvider = () => Task.FromResult<string?>(MockIdentityProfiles.ActivePlatformAdminMfa);
                })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            })
            .Build();
        connection.ServerTimeout = TimeSpan.FromSeconds(5);
        connection.KeepAliveInterval = TimeSpan.FromSeconds(1);
        return connection;
    }
}
