using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Realtime.Application.Events;

namespace Paqueteria.IntegrationTests.Operations;

internal sealed class Ops001RealtimeObserver : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ConcurrentDictionary<Guid, int> _deliveries = new();
    private readonly ConcurrentDictionary<Guid, int> _maximumVersions = new();

    internal Ops001RealtimeObserver(Uri baseAddress, Guid organizationId)
    {
        var hub = new Uri(
            baseAddress,
            $"/hubs/operations?organization_id={organizationId:D}");
        _connection = new HubConnectionBuilder()
            .WithUrl(hub, options =>
            {
                options.AccessTokenProvider = () =>
                    Task.FromResult<string?>(MockIdentityProfiles.ActiveDispatcher);
                options.Transports = HttpTransportType.WebSockets;
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy =
                    JsonNamingPolicy.SnakeCaseLower;
                options.PayloadSerializerOptions.DefaultIgnoreCondition =
                    JsonIgnoreCondition.WhenWritingNull;
            })
            .Build();
        _connection.On<RealtimeEnvelope<OrderStatusChangedPayload>>(
            "OrderStatusChanged",
            message =>
            {
                _deliveries.AddOrUpdate(message.EventId, 1, (_, current) => current + 1);
                _maximumVersions.AddOrUpdate(
                    message.AggregateId,
                    checked((int)message.AggregateVersion),
                    (_, current) => Math.Max(current, checked((int)message.AggregateVersion)));
            });
    }

    internal IReadOnlyCollection<Guid> EventIds => _deliveries.Keys.ToArray();
    internal int RawDeliveryCount => _deliveries.Values.Sum();
    internal IReadOnlyDictionary<Guid, int> MaximumVersions =>
        new Dictionary<Guid, int>(_maximumVersions);

    internal Task StartAsync(CancellationToken cancellationToken) =>
        _connection.StartAsync(cancellationToken);

    internal async Task WaitForEventAsync(
        Guid eventId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (!_deliveries.ContainsKey(eventId))
            {
                await Task.Delay(25, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Realtime event {eventId:D} was not observed. " +
                $"connection_state={_connection.State}; observed={string.Join(',', _deliveries.Keys)}");
        }
    }

    internal async Task WaitForUniqueEventsAsync(
        int expected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (_deliveries.Count < expected)
        {
            await Task.Delay(50, deadline.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_connection.State != HubConnectionState.Disconnected)
            {
                await _connection.StopAsync();
            }
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}
