using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Realtime.Application.Observability;

namespace Paqueteria.IntegrationTests.Realtime;

/// <summary>
/// Real SignalR round trip against the real <c>realtime.connections.active</c> instrument:
/// the gauge must return to zero after clients disconnect, because SignalR handles connect
/// and disconnect on different hub instances.
/// </summary>
public sealed class RealtimeConnectionMetricIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Active_connections_gauge_returns_to_zero_after_clients_disconnect()
    {
        await using var factory = new RealtimeWebApplicationFactory();
        _ = factory.Server;
        var telemetry = factory.Services.GetRequiredService<IRealtimeTelemetry>();
        if (telemetry is RealtimeWebApplicationFactory.AcceptanceObservingRealtimeTelemetry observing)
        {
            telemetry = observing.Inner;
        }

        using var gauge = new ActiveConnectionsGauge(telemetry);

        var tracking = CreateConnection(
            factory,
            "/hubs/tracking",
            RealtimeWebApplicationFactory.ValidTrackingTokenA,
            organizationId: null);
        var operations = CreateConnection(
            factory,
            "/hubs/operations",
            MockIdentityProfiles.ActiveDispatcher,
            RealtimeWebApplicationFactory.OrganizationA);

        await tracking.StartAsync().WaitAsync(Timeout);
        await operations.StartAsync().WaitAsync(Timeout);
        await gauge.WaitUntilAsync("tracking", 1, Timeout);
        await gauge.WaitUntilAsync("operations", 1, Timeout);

        await tracking.DisposeAsync();
        await operations.DisposeAsync();

        await gauge.WaitUntilAsync("tracking", 0, Timeout);
        await gauge.WaitUntilAsync("operations", 0, Timeout);
    }

    private static HubConnection CreateConnection(
        RealtimeWebApplicationFactory factory,
        string path,
        string token,
        Guid? organizationId)
    {
        var query = organizationId is { } value ? $"?organization_id={value:D}" : string.Empty;
        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress, path + query),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            })
            .Build();
    }

    /// <summary>
    /// Listens only to the meter owned by this host's telemetry singleton, so parallel test
    /// hosts that use the same meter name never contribute measurements.
    /// </summary>
    private sealed class ActiveConnectionsGauge : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _values = new(StringComparer.Ordinal);

        internal ActiveConnectionsGauge(IRealtimeTelemetry telemetry)
        {
            var meter = telemetry.GetType()
                .GetField("_meter", BindingFlags.Instance | BindingFlags.NonPublic)?
                .GetValue(telemetry) as Meter
                ?? throw new InvalidOperationException("The host does not use the production realtime telemetry.");
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) &&
                    instrument.Name == "realtime.connections.active")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "hub" && tag.Value is string hub)
                    {
                        _values.AddOrUpdate(hub, measurement, (_, current) => current + measurement);
                    }
                }
            });
            _listener.Start();
        }

        internal long Value(string hub) => _values.GetValueOrDefault(hub);

        internal async Task WaitUntilAsync(string hub, long expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Value(hub) != expected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            Assert.Equal(expected, Value(hub));
        }

        public void Dispose() => _listener.Dispose();
    }
}
