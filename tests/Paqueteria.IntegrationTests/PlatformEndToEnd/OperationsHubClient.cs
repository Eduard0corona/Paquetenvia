using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Paqueteria.IntegrationTests.Realtime;
using Realtime.Application.Events;

namespace Paqueteria.IntegrationTests.PlatformEndToEnd;

/// <summary>
/// A SignalR client of the operations hub (AI-12), connected as the synthetic dispatcher over WebSockets to the API's
/// loopback TLS listener. It keeps the instant each <c>OrderStatusChanged</c> arrived, keyed by its <c>event_id</c>
/// (the outbox id, RTM-002-STABLE-OUTBOX-ID-AS-EVENT-ID), so a delivery can be measured against the instant of the
/// browser action that committed the event. It trusts only the API's in-memory loopback certificate.
/// </summary>
internal sealed class OperationsHubClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ConcurrentDictionary<Guid, Arrival> _arrivals = new();

    internal OperationsHubClient(Uri apiAddress, Guid organizationId)
    {
        var expected = RealtimeKestrelWebApplicationFactory.LoopbackCertificate.GetCertHashString();
        bool Trusted(X509Certificate? certificate) =>
            certificate is not null &&
            string.Equals(certificate.GetCertHashString(), expected, StringComparison.OrdinalIgnoreCase);

        _connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(apiAddress, $"/hubs/operations?organization_id={organizationId:D}"),
                options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.AccessTokenProvider = () => Task.FromResult<string?>(MockIdentityProfiles.ActiveDispatcher);
                    options.HttpMessageHandlerFactory = handler =>
                    {
                        if (handler is HttpClientHandler client)
                        {
                            client.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => Trusted(certificate);
                        }
                        else if (handler is SocketsHttpHandler sockets)
                        {
                            sockets.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => Trusted(certificate);
                        }

                        return handler;
                    };
                    options.WebSocketConfiguration = socket =>
                        socket.RemoteCertificateValidationCallback = (_, certificate, _, _) => Trusted(certificate);
                })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            })
            .Build();
        _connection.On<RealtimeEnvelope<OrderStatusChangedPayload>>(
            "OrderStatusChanged",
            message => _arrivals.TryAdd(
                message.EventId,
                new Arrival(Stopwatch.GetTimestamp(), message.Payload.OrderId, message.Payload.NewStatus)));
    }

    internal Task StartAsync() => _connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(20));

    /// <summary>Waits for the first <c>OrderStatusChanged</c> of <paramref name="orderId"/> to <paramref name="status"/>.</summary>
    internal async Task<(Guid EventId, long ArrivedAt)> WaitForStatusAsync(Guid orderId, string status, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            foreach (var (eventId, arrival) in _arrivals)
            {
                if (arrival.OrderId == orderId && arrival.NewStatus == status)
                {
                    return (eventId, arrival.Timestamp);
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(
            $"No OrderStatusChanged to {status} reached the operations hub client; state={_connection.State}, " +
            $"received={_arrivals.Count}.");
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

    private sealed record Arrival(long Timestamp, Guid OrderId, string NewStatus);
}
