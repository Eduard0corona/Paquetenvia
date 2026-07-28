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
    private readonly ConcurrentQueue<Ops001RealtimeDelivery> _rawDeliveries = new();

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
                _rawDeliveries.Enqueue(new(
                    message.EventId,
                    message.AggregateId,
                    message.AggregateVersion,
                    message.EventType));
                _deliveries.AddOrUpdate(message.EventId, 1, (_, current) => current + 1);
            });
    }

    internal IReadOnlyCollection<Ops001RealtimeDelivery> Deliveries =>
        _rawDeliveries.ToArray();

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
