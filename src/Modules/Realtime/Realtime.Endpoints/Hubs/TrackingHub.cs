using Microsoft.AspNetCore.SignalR;
using System.Diagnostics.CodeAnalysis;
using Realtime.Application.Authorization;
using Realtime.Application.Clients;
using Realtime.Application.Observability;
using Realtime.Application.Publishing;

namespace Realtime.Endpoints.Hubs;

public sealed class TrackingHub(IRealtimeTelemetry telemetry) : Hub<ITrackingClient>
{
    internal const string AuthorizationItemKey = "Realtime.TrackingAuthorization";
    private bool _accepted;

    public override async Task OnConnectedAsync()
    {
        using var measurement = telemetry.MeasureAuthorization("tracking", "tracking_token");
        var httpContext = Context.GetHttpContext();
        var authorization =
            httpContext?.Items[AuthorizationItemKey] as TrackingConnectionAuthorization;
        if (authorization is null)
        {
            Reject();
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Tracking(authorization.PublicOrderId),
            Context.ConnectionAborted);
        _accepted = true;
        telemetry.ConnectionAccepted("tracking", "tracking_token");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_accepted)
        {
            telemetry.ConnectionClosed("tracking");
        }

        await base.OnDisconnectedAsync(exception);
    }

    [DoesNotReturn]
    private void Reject()
    {
        telemetry.ConnectionRejected("tracking", "tracking_token");
        Context.Abort();
        throw new HubException("Connection rejected.");
    }
}
