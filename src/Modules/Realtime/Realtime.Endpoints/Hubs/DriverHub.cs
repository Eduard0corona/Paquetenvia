using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.SignalR;
using Realtime.Application.Authorization;
using Realtime.Application.Clients;
using Realtime.Application.Observability;
using Realtime.Application.Publishing;
using Realtime.Endpoints.Connection;

namespace Realtime.Endpoints.Hubs;

public sealed class DriverHub(
    IRealtimeConnectionAuthorizer authorizer,
    IRealtimeTelemetry telemetry) : Hub<IDriverClient>
{
    private bool _accepted;

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        using var measurement = telemetry.MeasureAuthorization("driver", "oidc");
        var request = httpContext?.Items[RealtimeConnectionGateMiddleware.PrivateRequestItemKey]
            as PrivateRealtimeConnectionRequest;
        if (request is null)
        {
            Reject();
        }

        var result = await authorizer.AuthorizeDriverAsync(
            request,
            Context.ConnectionAborted);
        if (!result.IsAuthorized || result.Authorization is null)
        {
            Reject();
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Driver(result.Authorization.DriverId),
            Context.ConnectionAborted);
        foreach (var assignmentId in result.Authorization.AssignmentIds)
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                RealtimeGroupNames.Assignment(assignmentId),
                Context.ConnectionAborted);
        }

        _accepted = true;
        telemetry.ConnectionAccepted("driver", "oidc");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_accepted)
        {
            telemetry.ConnectionClosed("driver");
        }

        await base.OnDisconnectedAsync(exception);
    }

    [DoesNotReturn]
    private void Reject()
    {
        telemetry.ConnectionRejected("driver", "oidc");
        Context.Abort();
        throw new HubException("Connection rejected.");
    }
}
